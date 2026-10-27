using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using System.Runtime.InteropServices;
namespace StartupStudio;

public static class Images
{
    public static StartupImage Import(string path, bool color = false)
    {
        using var photo = Load(path); return Prepare(photo, photo.FullImage, color);
    }
    public static PhotoDocument Load(string path)
    {
        Check.That(new FileInfo(path).Length <= 32L * 1024 * 1024, "图片文件过大，请先缩小文件。");
        using var stream = File.OpenRead(path); using var codec = SKCodec.Create(stream);
        Check.That(codec != null, "图片解码失败，请选择 PNG 或 JPEG。");
        Check.That(codec!.EncodedFormat is SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg && codec.FrameCount <= 1, "请选择单张 PNG 或 JPEG 图片。");
        var w = codec.Info.Width; var h = codec.Info.Height; var origin = codec.EncodedOrigin;
        var rotate = (int)origin >= 5; var ow = rotate ? h : w; var oh = rotate ? w : h;
        Check.That(ow is >= 1 and <= 1024 && oh is >= 1 and <= 768, "导入图片宽度不得超过 1024、高度不得超过 768 像素，请先调整大小。");
        var depth = 8;
        if (codec.EncodedFormat == SKEncodedImageFormat.Png)
        {
            var header = new byte[25]; using var input = File.OpenRead(path);
            if (input.Read(header) == header.Length) depth = header[24];
        }
        using var space = SKColorSpace.CreateSrgb();
        var type = depth > 8 ? SKColorType.RgbaF16 : SKColorType.Rgba8888;
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, type, SKAlphaType.Premul, space));
        Check.That(bitmap.GetPixels() != IntPtr.Zero, "图片解码内存不足，请缩小图片后重试。");
        Check.That(codec.GetPixels(bitmap.Info, bitmap.GetPixels()) == SKCodecResult.Success, "图片解码不完整。");
        var oriented = new SKBitmap(new SKImageInfo(ow, oh, type, SKAlphaType.Premul, space));
        try
        {
            Check.That(oriented.GetPixels() != IntPtr.Zero, "图片处理内存不足，请缩小图片后重试。");
            using var canvas = new SKCanvas(oriented); canvas.Clear(SKColors.Transparent);
            var bw = bitmap.Width; var bh = bitmap.Height;
            var matrix = (int)origin switch
            {
                1 => Matrix(1, 0, 0, 0, 1, 0), 2 => Matrix(-1, 0, bw, 0, 1, 0),
                3 => Matrix(-1, 0, bw, 0, -1, bh), 4 => Matrix(1, 0, 0, 0, -1, bh),
                5 => Matrix(0, 1, 0, 1, 0, 0), 6 => Matrix(0, -1, bh, 1, 0, 0),
                7 => Matrix(0, -1, bh, -1, 0, bw), 8 => Matrix(0, 1, 0, -1, 0, bw),
                _ => throw new StudioException("图片方向无效。")
            };
            canvas.Concat(matrix); using var input = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(input, new SKRect(0, 0, bw, bh), new SKRect(0, 0, bw, bh), new SKSamplingOptions(SKFilterMode.Nearest));
            return new(oriented, ow, oh, depth);
        }
        catch { oriented.Dispose(); throw; }
    }
    private static SKMatrix Matrix(float sx, float kx, float tx, float ky, float sy, float ty) => new() { ScaleX = sx, SkewX = kx, TransX = tx, SkewY = ky, ScaleY = sy, TransY = ty, Persp2 = 1 };
    public static StartupImage Prepare(PhotoDocument photo, Rect crop, bool color)
    {
        var x = Math.Clamp((int)Math.Round(crop.X), 0, photo.Width - 1); var y = Math.Clamp((int)Math.Round(crop.Y), 0, photo.Height - 1);
        var width = Math.Clamp((int)Math.Round(crop.Width), 1, photo.Width - x); var height = Math.Clamp((int)Math.Round(crop.Height), 1, photo.Height - y);
        using var space = SKColorSpace.CreateSrgb();
        using var output = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque, space));
        using (var canvas = new SKCanvas(output))
        {
            canvas.Clear(SKColors.Black); using var input = SKImage.FromBitmap(photo.Raster);
            canvas.DrawImage(input, new SKRect(x, y, x + width, y + height), new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Nearest));
        }
        var pixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++) Marshal.Copy(output.GetPixels() + row * output.RowBytes, pixels, row * width * 4, width * 4);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 0;
        var image = new StartupImage(width, height, pixels, 4); return color ? image : image.Grayscale();
    }
    public static WriteableBitmap Bitmap(PhotoDocument photo) => Bitmap(Prepare(photo, photo.FullImage, true));
    public static StartupImage CameraCanvas(StartupImage image)
    {
        image.Validate(); if (image.Width == 1024 && image.Height == 768) return image;
        var pixels = new byte[1024 * 768 * image.Channels]; var left = (1024 - image.Width) / 2; var top = (768 - image.Height) / 2;
        for (var y = 0; y < image.Height; y++) Array.Copy(image.Pixels, y * image.Width * image.Channels, pixels, ((top + y) * 1024 + left) * image.Channels, image.Width * image.Channels);
        return new(1024, 768, pixels, image.Channels);
    }
    public static (int X, int Y) Orient(int origin, int x, int y, int w, int h) => origin switch
    {
        1 => (x, y), 2 => (w - 1 - x, y), 3 => (w - 1 - x, h - 1 - y), 4 => (x, h - 1 - y),
        5 => (y, x), 6 => (y, h - 1 - x), 7 => (w - 1 - y, h - 1 - x), 8 => (w - 1 - y, x), _ => throw new StudioException("图片方向无效。")
    };
    public static void Export(StartupImage image, string path)
    {
        image.Validate(); using var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, image.Channels == 4 ? SKColorType.Bgra8888 : SKColorType.Gray8, SKAlphaType.Opaque));
        for (var row = 0; row < image.Height; row++) Marshal.Copy(image.Pixels, row * image.Width * image.Channels, bitmap.GetPixels() + row * bitmap.RowBytes, image.Width * image.Channels);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100); Check.That(data != null, "图片保存失败。"); Check.Atomic(path, data!.ToArray());
    }
    public static WriteableBitmap Bitmap(StartupImage image)
    {
        image.Validate(); var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var framebuffer = bitmap.Lock(); var row = new byte[image.Width * 4];
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++) { if (image.Channels == 4) Array.Copy(image.Pixels, (y * image.Width + x) * 4, row, x * 4, 3);
                else row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = image.Pixels[y * image.Width + x]; row[x * 4 + 3] = 255; }
            Marshal.Copy(row, 0, framebuffer.Address + y * framebuffer.RowBytes, row.Length);
        }
        return bitmap;
    }
}
