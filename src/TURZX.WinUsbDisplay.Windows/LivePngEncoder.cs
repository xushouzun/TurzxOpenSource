using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TURZX.WinUsbDisplay.Windows;

/// <summary>编码实时视频叠加所需的 RGBA PNG 帧。</summary>
internal static class LivePngEncoder
{
    /// <summary>PNG 文件的固定签名。</summary>
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];
    /// <summary>计算 PNG 块校验值所用的 CRC 查找表。</summary>
    private static readonly uint[] CrcTable = CreateCrcTable();

    /// <summary>检查输入是否声明为 RGBA PNG。</summary>
    internal static bool IsRgba(ReadOnlySpan<byte> data) =>
        data.Length >= 26 && data.StartsWith(Signature) && data[24] == 8 && data[25] == 6;

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

        // PNG 只包含三个块，直接写入最终数组，省去输出流扩容和复制。
        ReadOnlySpan<byte> imageData = compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length));
        byte[] output = new byte[checked(Signature.Length + 13 + 3 * 12 + imageData.Length)];
        Signature.CopyTo(output);
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 6; // RGBA 颜色类型。
        Span<byte> remaining = output.AsSpan(Signature.Length);
        remaining = WriteChunk(remaining, "IHDR"u8, header);
        remaining = WriteChunk(remaining, "IDAT"u8, imageData);
        WriteChunk(remaining, "IEND"u8, []);
        return output;
    }

    /// <summary>写入 PNG 块长度、类型、内容和 CRC。</summary>
    private static Span<byte> WriteChunk(Span<byte> output, ReadOnlySpan<byte> type, scoped ReadOnlySpan<byte> data)
    {
        BinaryPrimitives.WriteInt32BigEndian(output, data.Length);
        type.CopyTo(output[4..]);
        data.CopyTo(output[8..]);

        uint crc = uint.MaxValue;
        foreach (byte value in type)
            crc = CrcTable[(byte)(crc ^ value)] ^ (crc >> 8);
        foreach (byte value in data)
            crc = CrcTable[(byte)(crc ^ value)] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(output[(8 + data.Length)..], ~crc);
        return output[(12 + data.Length)..];
    }

    /// <summary>生成 PNG 校验使用的 CRC-32 查找表。</summary>
    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
                value = (value >> 1) ^ ((value & 1) == 0 ? 0u : 0xedb88320u);
            table[i] = value;
        }
        return table;
    }
}
