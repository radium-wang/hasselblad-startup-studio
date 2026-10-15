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
    // Independent PNG encoder: true 16-bit RGBA samples, not an 8-bit bitmap
    // merely tagged as high depth. Also generates header-only oversized fixtures.
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
    public static void Run(Action<string, Action> test, string temp, string repo)
    {
        var png = Path.Combine(temp, "high-depth.png"); Png16(png, 1600, 1200);
        test("image-16bit-png-becomes-exact-camera-canvas", () =>
        {
            using var photo = Images.Load(png); Require(photo.OriginalWidth == 1600 && photo.OriginalHeight == 1200 && photo.BitDepth == 16);
            var prepared = Images.CameraCanvas(Images.Prepare(photo, photo.FullImage, true));
            Require(prepared.Width == 1024 && prepared.Height == 768 && prepared.Channels == 4 && prepared.Pixels.Length == 1024 * 768 * 4);
            var red = (384 * 1024 + 256) * 4; var blue = (384 * 1024 + 768) * 4;
            Require(prepared.Pixels[red] == 0 && Math.Abs(prepared.Pixels[red + 1] - 128) <= 1 && prepared.Pixels[red + 2] == 255);
            Require(Math.Abs(prepared.Pixels[blue] - 128) <= 1 && prepared.Pixels[blue + 1] == 0 && prepared.Pixels[blue + 2] == 0);
            var output = Path.Combine(temp, "camera-rgb.png"); Images.Export(prepared, output); Require(File.ReadAllBytes(output)[24] == 8);
            Require(StartupImage.Decode(prepared.Envelope()).Pixels.SequenceEqual(prepared.Pixels));
        });
        test("image-16bit-to-grayscale-is-8bit-and-preserves-source", () =>
        {
            var before = Check.Hash(File.ReadAllBytes(png)); using var photo = Images.Load(png);
            var output = Images.CameraCanvas(Images.Prepare(photo, photo.FullImage, false)); Require(output.Channels == 1 && output.Pixels.Length == 1024 * 768);
            var path = Path.Combine(temp, "camera-gray.png"); Images.Export(output, path); var header = File.ReadAllBytes(path);
            Require(header[24] == 8 && header[25] == 0 && Check.Hash(File.ReadAllBytes(png)) == before);
        });
        test("image-hd-jpeg-downsamples-before-editing-and-keeps-aspect", () =>
        {
            var jpeg = Path.Combine(temp, "high-resolution.jpg"); using (var b = new SKBitmap(6000, 4000))
            {
                b.Erase(new SKColor(200, 100, 30)); using var data = b.Encode(SKEncodedImageFormat.Jpeg, 95); File.WriteAllBytes(jpeg, data.ToArray());
            }
            using var photo = Images.Load(jpeg); Require(photo.OriginalWidth == 6000 && photo.OriginalHeight == 4000 && photo.Width <= 4096 && photo.Height <= 4096 && photo.Width > 1024);
            var fit = Images.Prepare(photo, photo.FullImage, true); Require(fit.Width == 1024 && Math.Abs(fit.Height - 683) <= 1);
            var canvas = Images.CameraCanvas(fit); Require(canvas.Pixels.Take(1024 * 4 * 20).All(b => b == 0));
            var center = (384 * 1024 + 512) * 4; Require(Math.Abs(canvas.Pixels[center + 2] - 200) <= 3 && Math.Abs(canvas.Pixels[center + 1] - 100) <= 3);
        });
        test("image-high-resolution-crop-selects-original-working-region", () =>
        {
            using var photo = Images.Load(png);
            var left = Images.CameraCanvas(Images.Prepare(photo, new Rect(0, 0, 800, 1200), true));
            var right = Images.CameraCanvas(Images.Prepare(photo, new Rect(800, 0, 800, 1200), true));
            var center = (384 * 1024 + 512) * 4;
            Require(left.Pixels[center + 2] > 250 && right.Pixels[center + 2] == 0 && right.Pixels[center] is >= 127 and <= 129);
        });
        test("image-header-size-limit-refuses-before-large-allocation", () =>
        {
            var path = Path.Combine(temp, "oversized-header.png"); Png16(path, 20000, 10000, pixels: false);
            var rejected = false; try { using var photo = Images.Load(path); } catch (StudioException) { rejected = true; } Require(rejected);
        });
        var window = new StudioWindow { Width = 1260, Height = 800 };
        var bytes = new byte[640 * 480]; for (var y = 0; y < 480; y++) for (var x = 0; x < 640; x++) bytes[y * 640 + x] = (byte)((x + y) % 251);
        window.SetImage(new StartupImage(640, 480, bytes), "editing.png"); window.Mode.SelectedIndex = 1; window.Show(); Flush();
        Point At(double x, double y)
        {
            var f = window.Editor.ImageViewport; return window.Editor.TranslatePoint(new Point(f.X + x * f.Width / 640, f.Y + y * f.Height / 480), window)!.Value;
        }
        void Drag(Point from, Point to)
        {
            window.MouseDown(from, MouseButton.Left); window.MouseMove(to, RawInputModifiers.LeftMouseButton); window.MouseUp(to, MouseButton.Left); Flush();
        }
        test("crop-default-selection-has-room-to-move", () =>
        {
            var crop = window.Editor.Crop; Require(crop.Width < 640 && crop.Height < 480);
            Drag(At(crop.Center.X, crop.Center.Y), At(crop.Center.X + 20, crop.Center.Y + 10)); Require(window.Editor.Crop.X == crop.X + 20 && window.Editor.Crop.Y == crop.Y + 10);
            Require(window.Editor.Bounds.Width > 300 && window.Editor.IsVisible && !window.Preview.IsVisible);
        });
        foreach (var corner in new[] { "top-left", "top-right", "bottom-left", "bottom-right" })
        {
            test("crop-real-mouse-resizes-" + corner, () =>
            {
                var crop = new Rect(100, 100, 300, 200); window.Editor.Crop = crop;
                var x = corner.EndsWith("left") ? 100 : 400; var y = corner.StartsWith("top") ? 100 : 300;
                var targetX = x + (x == 100 ? 20 : -20); var targetY = y + (y == 100 ? 15 : -15);
                Drag(At(x, y), At(targetX, targetY));
                Require(window.Editor.Crop.Width == 280 && window.Editor.Crop.Height == 185);
            });
        }
        test("crop-corner-handle-outside-image-still-receives-pointer", () =>
        {
            window.Editor.Crop = new Rect(0, 0, 640, 480); var handle = At(640, 480) + new Vector(3, 3); Drag(handle, At(500, 370));
            Require(window.Editor.Crop.Width == 500 && window.Editor.Crop.Height == 370);
        });
        foreach (var edge in new[] { "left", "top", "right", "bottom" })
        {
            test("crop-independent-edge-" + edge, () =>
            {
                window.Editor.Crop = new Rect(100, 100, 300, 200);
                var (from, to, expected) = edge switch
                {
                    "left" => (At(100, 170), At(60, 140), new Rect(60, 100, 340, 200)),
                    "top" => (At(180, 100), At(210, 60), new Rect(100, 60, 300, 240)),
                    "right" => (At(400, 170), At(450, 140), new Rect(100, 100, 350, 200)),
                    _ => (At(180, 300), At(210, 350), new Rect(100, 100, 300, 250))
                };
                Drag(from, to); Require(window.Editor.Crop == expected);
                var prepared = Images.CameraCanvas(Images.Prepare(window.Editor.Image!, expected, false));
                Require(prepared.Pixels.SequenceEqual(window.Output!.Pixels));
            });
            test("crop-edge-cannot-cross-opposite-" + edge, () =>
            {
                window.Editor.Crop = new Rect(100, 100, 300, 200);
                var (from, to, expected) = edge switch
                {
                    "left" => (At(100, 170), At(600, 170), new Rect(399, 100, 1, 200)),
                    "top" => (At(180, 100), At(180, 450), new Rect(100, 299, 300, 1)),
                    "right" => (At(400, 170), At(0, 170), new Rect(100, 100, 1, 200)),
                    _ => (At(180, 300), At(180, 0), new Rect(100, 100, 300, 1))
                };
                Drag(from, to); Require(window.Editor.Crop == expected);
            });
        }
        void DrawButton()
        {
            Flush(); var button = window.DrawCrop;
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Flush();
        }
        test("crop-draw-button-always-arms-one-shot-tool", () =>
        {
            window.Editor.Crop = new Rect(100, 100, 300, 200); DrawButton(); DrawButton();
            Require(window.Editor.DrawNewSelection && window.DrawCrop.Content?.ToString() == "绘制画框");
            Drag(At(200, 150), At(500, 400));
            Require(window.Editor.Crop == new Rect(200, 150, 300, 250) && !window.Editor.DrawNewSelection);
        });
        test("crop-unarmed-outside-click-keeps-existing-selection", () =>
        {
            window.Editor.Crop = new Rect(100, 100, 300, 200); Drag(At(30, 30), At(70, 60));
            Require(window.Editor.Crop == new Rect(100, 100, 300, 200));
        });
        foreach (var end in new[] { new Point(500, 350), new Point(50, 350), new Point(500, 30), new Point(50, 30) })
        {
            test("crop-draw-button-inside-existing-selection-" + end, () =>
            {
                window.Editor.Crop = new Rect(100, 100, 300, 200); DrawButton();
                Require(window.Editor.DrawNewSelection && window.DrawCrop.Content?.ToString() == "绘制画框");
                Drag(At(200, 150), At(end.X, end.Y));
                Require(window.Editor.Crop == new Rect(Math.Min(200, end.X), Math.Min(150, end.Y), Math.Abs(200 - end.X), Math.Abs(150 - end.Y)));
                Require(!window.Editor.DrawNewSelection && window.DrawCrop.Content?.ToString() == "绘制画框");
                var before = window.Editor.Crop;
                Drag(At(before.Center.X, before.Center.Y), At(before.Center.X + 5, before.Center.Y + 5));
                Require(window.Editor.Crop.X == before.X + 5 && window.Editor.Crop.Y == before.Y + 5 && window.Editor.Crop.Size == before.Size);
            });
        }
        test("crop-draw-tool-starts-outside-old-selection-and-clamps-to-image", () =>
        {
            window.Editor.Crop = new Rect(100, 100, 300, 200); DrawButton();
            Drag(At(30, 30), At(800, 700)); Require(window.Editor.Crop == new Rect(30, 30, 610, 450));
        });
        test("crop-draw-tool-still-allows-independent-edge-dragging", () =>
        {
            window.Editor.Crop = new Rect(100, 100, 300, 200); DrawButton();
            Drag(At(100, 170), At(60, 140)); Require(window.Editor.Crop == new Rect(60, 100, 340, 200) && window.Editor.DrawNewSelection);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Require(!window.Editor.DrawNewSelection);
        });
        test("crop-edge-at-image-boundary-accepts-outside-handle", () =>
        {
            window.Editor.Crop = new Rect(0, 0, 640, 480);
            Drag(At(0, 180) - new Vector(3, 0), At(50, 180)); Require(window.Editor.Crop == new Rect(50, 0, 590, 480));
        });
        test("crop-draw-click-without-drag-keeps-selection", () =>
        {
            var before = new Rect(100, 100, 300, 200); window.Editor.Crop = before; DrawButton();
            Drag(At(200, 150), At(200, 150)); Require(window.Editor.Crop == before && !window.Editor.DrawNewSelection);
        });
        test("crop-escape-cancels-drawing-and-restores-selection", () =>
        {
            var before = new Rect(100, 100, 300, 200); window.Editor.Crop = before; DrawButton();
            window.MouseDown(At(200, 150), MouseButton.Left); window.MouseMove(At(500, 400), RawInputModifiers.LeftMouseButton);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); window.MouseUp(At(500, 400), MouseButton.Left); Flush();
            Require(window.Editor.Crop == before && !window.Editor.DrawNewSelection);
        });
        test("crop-switching-mode-clears-draw-tool", () =>
        {
            DrawButton(); Require(window.Editor.DrawNewSelection); window.Mode.SelectedIndex = 0; Flush();
            Require(!window.Editor.DrawNewSelection && !window.DrawCrop.IsVisible);
            window.Mode.SelectedIndex = 1; Flush(); Require(window.DrawCrop.IsVisible && !window.Editor.DrawNewSelection);
        });
        test("crop-drag-clamps-at-image-edge-and-release-stops-movement", () =>
        {
            window.Editor.Crop = new Rect(100, 100, 200, 150); Drag(At(200, 175), At(800, 700));
            Require(window.Editor.Crop.Right <= 640 && window.Editor.Crop.Bottom <= 480);
            var after = window.Editor.Crop; window.MouseMove(At(200, 175)); Require(window.Editor.Crop == after);
        });
        test("crop-wheel-zoom-and-output-stay-in-sync", () =>
        {
            window.Editor.Crop = new Rect(100, 100, 300, 200); window.MouseWheel(At(250, 200), new Vector(0, 2)); Flush();
            Require(window.Editor.Crop.Width < 300 && window.Editor.Crop.Height < 200 && window.Output!.Width == 1024 && window.Output.Height == 768);
            var expected = Images.CameraCanvas(Images.Prepare(window.Editor.Image!, window.Editor.Crop, false)); Require(expected.Pixels.SequenceEqual(window.Output!.Pixels));
        });
        test("crop-hd-import-keeps-source-independent-of-camera-canvas", () =>
        {
            window.SetPhoto(Images.Load(png), "16-bit sample.png"); Require(window.Editor.Image!.Width == 1600 && window.Output!.Width == 1024);
            window.Editor.Crop = new Rect(200, 200, 800, 600); Require(window.Output!.Pixels.Length == 1024 * 768 * 4);
            window.Colors.SelectedIndex = 1; Require(window.Output!.Channels == 1 && window.Output.Pixels.Length == 1024 * 768);
            window.Width = 1080; window.Height = 640; Flush();
            using var frame = window.CaptureRenderedFrame(); Require(frame != null); frame!.Save(Path.Combine(repo, "outputs", "windows-crop-small.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        });
        window.Close();
    }
    static void Flush() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
}
