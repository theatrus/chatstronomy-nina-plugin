using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Chatstronomy.NINA.Direct;

internal static class DirectThumbnailEncoder
{
    internal const int MaxWidth = 1_024;
    private const int JpegQuality = 85;

    internal static byte[] Encode(BitmapSource source)
    {
        var scale = source.PixelWidth > MaxWidth
            ? (double)MaxWidth / source.PixelWidth
            : 1d;
        BitmapSource thumbnail = source;
        if (scale < 1)
        {
            var transformed = new TransformedBitmap(
                source,
                new ScaleTransform(scale, scale));
            transformed.Freeze();
            thumbnail = transformed;
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(thumbnail));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
