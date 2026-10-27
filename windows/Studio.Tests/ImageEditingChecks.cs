using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using SkiaSharp;
using StartupStudio;

static class ImageEditingChecks
{
    static void Require(bool value) { if (!value) throw new Exception("Image editing assertion failed"); }
    static void Chunk(Stream output, string type, byte[] data)
    {
        var tag = Encoding.ASCII.GetBytes(type); Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)data.Length); output.Write(bytes); output.Write(tag); output.Write(data);
        uint crc = uint.MaxValue;
        foreach (var b in tag.Concat(data)) { crc ^= b; for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
        BinaryPrimitives.WriteUInt32BigEndian(bytes, ~crc); output.Write(bytes);
    }
    static void Png16(string path, int width, int height, bool pixels = true)
    {
        using var output = File.Create(path); output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height); header[8] = 16; header[9] = 6;
        Chunk(output, "IHDR", header); Chunk(output, "sRGB", [0]);
        using var packed = new MemoryStream();
        using (var compressor = new ZLibStream(packed, CompressionLevel.Fastest, true))
        {
            if (pixels)
            {
                var row = new byte[1 + width * 8];
                for (var x = 0; x < width; x++)
                {
                    var samples = x < width / 2 ? new ushort[] { 65535, 32768, 0, 65535 } : new ushort[] { 0, 0, 65535, 32768 };
                    for (var c = 0; c < 4; c++) BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(1 + x * 8 + c * 2), samples[c]);
                }
                for (var y = 0; y < height; y++) compressor.Write(row);
            }
        }
        Chunk(output, "IDAT", packed.ToArray()); Chunk(output, "IEND", []);
    }
    static void Flush() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    public static void Run(Action<string, Action> test, string temp, string repo)
    {
        var png = Path.Combine(temp, "bounded-rgb-16bit.png"); Png16(png, 640, 480);
        test("image-rgb-retains-preview3-input-size", () =>
        {
            using var photo = Images.Load(png); Require(photo.OriginalWidth == 640 && photo.OriginalHeight == 480 && photo.BitDepth == 16);
            var image = Images.Prepare(photo, photo.FullImage, true); Require(image.Width == 640 && image.Height == 480 && image.Channels == 4);
            var red = (240 * 640 + 160) * 4; Require(image.Pixels[red] == 0 && Math.Abs(image.Pixels[red + 1] - 128) <= 1 && image.Pixels[red + 2] == 255);
            var path = Path.Combine(temp, "camera-rgb.png"); Images.Export(image, path);
            Require(File.ReadAllBytes(path)[24] == 8 && StartupImage.Decode(image.Envelope()).Pixels.SequenceEqual(image.Pixels));
        });
        test("image-grayscale-selection-preserves-source", () =>
        {
            var before = Check.Hash(File.ReadAllBytes(png)); using var photo = Images.Load(png);
            var image = Images.Prepare(photo, photo.FullImage, false); Require(image.Width == 640 && image.Height == 480 && image.Channels == 1);
            var path = Path.Combine(temp, "camera-gray.png"); Images.Export(image, path);
            Require(File.ReadAllBytes(path)[25] == 0 && Check.Hash(File.ReadAllBytes(png)) == before);
        });
        test("image-preview3-size-limit-refuses-oversized-input", () =>
        {
            foreach (var (width, height) in new[] { (1025, 768), (1024, 769), (1600, 1200) })
            {
                var path = Path.Combine(temp, $"oversized-{width}-{height}.png"); Png16(path, width, height, pixels: false);
                var rejected = false; try { using var photo = Images.Load(path); } catch (StudioException) { rejected = true; }
                Require(rejected);
            }
        });
        test("image-jpeg-preserves-original-dimensions", () =>
        {
            var path = Path.Combine(temp, "small-image.jpg"); using (var bitmap = new SKBitmap(320, 240))
            {
                bitmap.Erase(new SKColor(200, 100, 30)); using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95); File.WriteAllBytes(path, data.ToArray());
            }
            using var photo = Images.Load(path); var image = Images.Prepare(photo, photo.FullImage, true); Require(image.Width == 320 && image.Height == 240);
        });
        var window = new StudioWindow { Width = 1260, Height = 800 };
        window.SetImage(new StartupImage(640, 480, new byte[640 * 480]), "editing.png"); window.Mode.SelectedIndex = 1; window.Show(); Flush();
        Point At(double x, double y)
        {
            var frame = window.Editor.ImageViewport;
            return window.Editor.TranslatePoint(new Point(frame.X + x * frame.Width / 640, frame.Y + y * frame.Height / 480), window)!.Value;
        }
        void Drag(Point from, Point to)
        {
            window.MouseDown(from, MouseButton.Left); window.MouseMove(to, RawInputModifiers.LeftMouseButton); window.MouseUp(to, MouseButton.Left); Flush();
        }
        test("preview3-crop-controls-stay-left-and-result-stays-centered", () =>
        {
            Require(window.Editor.BasicOnly && window.Editor.IsVisible && window.Preview.IsVisible);
            Require(window.Editor.TranslatePoint(default, window)!.Value.X < window.Preview.TranslatePoint(default, window)!.Value.X);
        });
        test("preview3-first-drag-inside-full-image-starts-crop", () =>
        {
            window.Editor.Crop = new Rect(0, 0, 640, 480);
            Drag(At(200, 150), At(420, 330));
            Require(window.Editor.Crop == new Rect(200, 150, 220, 180) && window.Output!.Width == 220 && window.Output.Height == 180);
        });
        test("preview3-crop-moves-and-keeps-original-dimensions", () =>
        {
            window.Editor.Crop = new Rect(100, 100, 300, 200);
            Drag(At(200, 175), At(220, 185));
            Require(window.Editor.Crop.X == 120 && window.Editor.Crop.Y == 110);
            Require(window.Output!.Width == 300 && window.Output.Height == 200 && window.Preview.Image!.Pixels.SequenceEqual(window.Output.Pixels));
        });
        test("preview3-crop-corner-resize-and-color-toggle", () =>
        {
            window.Editor.Crop = new Rect(100, 100, 300, 200);
            Drag(At(100, 100), At(120, 115));
            Require(window.Editor.Crop.Width == 280 && window.Editor.Crop.Height == 185);
            var crop = window.Editor.Crop; window.Colors.SelectedIndex = 1; Flush();
            Require(window.Editor.Crop == crop && window.Output!.Channels == 1 && window.Output.Width == 280 && window.Output.Height == 185);
        });
        test("preview3-crop-has-no-rgb-preview4-wheel-tool", () =>
        {
            var before = window.Editor.Crop; window.MouseWheel(At(before.Center.X, before.Center.Y), new Vector(0, 3)); Flush();
            Require(window.Editor.Crop == before && !window.Editor.DrawNewSelection);
        });
        window.Close();
    }
}
