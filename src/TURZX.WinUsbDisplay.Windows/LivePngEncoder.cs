using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TURZX.WinUsbDisplay.Windows;

/// <summary>编码实时视频叠加所需的 RGBA PNG 帧。</summary>
internal static class LivePngEncoder
{
    /// <summary>检查输入是否声明为 RGBA PNG。</summary>
    internal static bool IsRgba(ReadOnlySpan<byte> data) =>
        data.Length >= 26 && data.StartsWith(DisplaySession.PngSignature) && data[24] == 8 && data[25] == 6;

    /// <summary>从输入位图像素构造完整 PNG 帧。</summary>
    internal static byte[] Encode(BitmapSource bitmap, bool preserveTransparency)
    {
        // 预乘后的 RGB 值等同于像素与黑色背景合成后的结果。
        var format = preserveTransparency ? PixelFormats.Bgra32 : PixelFormats.Pbgra32;
        if (bitmap.Format != format)
            bitmap = new FormatConvertedBitmap(bitmap, format, null, 0);

        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        int stride = checked(width * 4);
        using var compressed = new MemoryStream();
        var pixels = ArrayPool<byte>.Shared.Rent(checked(stride * height));
        try
        {
            bitmap.CopyPixels(pixels, stride, 0);
            using var deflate = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true);
            var row = new byte[stride + 1];
            row[0] = 1; // 使用 PNG 的 Sub 滤波器。
            for (int y = 0; y < height; y++)
            {
                int offset = y * stride;
                int r = 0, g = 0, b = 0, a = 0;
                for (int x = 0; x < stride; x += 4)
                {
                    int index = offset + x;
                    int nextR = pixels[index + 2], nextG = pixels[index + 1], nextB = pixels[index];
                    int nextA = preserveTransparency ? pixels[index + 3] : 255;
                    row[x + 1] = unchecked((byte)(nextR - r));
                    row[x + 2] = unchecked((byte)(nextG - g));
                    row[x + 3] = unchecked((byte)(nextB - b));
                    row[x + 4] = unchecked((byte)(nextA - a));
                    r = nextR;
                    g = nextG;
                    b = nextB;
                    a = nextA;
                }
                deflate.Write(row);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }

        ReadOnlySpan<byte> imageData = compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length));
        return RgbaPng.Encode(width, height, imageData);
    }
}
