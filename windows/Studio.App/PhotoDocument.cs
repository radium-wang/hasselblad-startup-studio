using Avalonia;
using SkiaSharp;
using System.Runtime.InteropServices;
namespace StartupStudio;

// Desktop editing data is separate from the camera's bounded image protocol.
public sealed class PhotoDocument : IDisposable
{
    internal SKBitmap Raster { get; }
    public int Width => Raster.Width;
    public int Height => Raster.Height;
    public int OriginalWidth { get; }
    public int OriginalHeight { get; }
    public int BitDepth { get; }
    public bool GrayscaleSource { get; }
    internal PhotoDocument(SKBitmap raster, int originalWidth, int originalHeight, int bitDepth, bool grayscale = false)
    {
        Raster = raster; OriginalWidth = originalWidth; OriginalHeight = originalHeight; BitDepth = bitDepth; GrayscaleSource = grayscale;
    }
    public static PhotoDocument FromImage(StartupImage image)
    {
        image.Validate(); using var space = SKColorSpace.CreateSrgb();
        var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Opaque, space));
        var row = new byte[image.Width * 4];
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (image.Channels == 4) Array.Copy(image.Pixels, (y * image.Width + x) * 4, row, x * 4, 3);
                else row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = image.Pixels[y * image.Width + x];
                row[x * 4 + 3] = 255;
            }
            Marshal.Copy(row, 0, bitmap.GetPixels() + y * bitmap.RowBytes, row.Length);
        }
        return new(bitmap, image.Width, image.Height, 8, image.Channels == 1);
    }
    public Rect FullImage => new(0, 0, Width, Height);
    public void Dispose() => Raster.Dispose();
}
