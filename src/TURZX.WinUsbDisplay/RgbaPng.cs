using System.Buffers.Binary;
using System.IO.Compression;

namespace TURZX.WinUsbDisplay;

internal static class RgbaPng
{
    private static readonly uint[] CrcTable = CreateCrcTable();

    internal static byte[] CreateTransparent(int width, int height)
    {
        using var compressed = new MemoryStream();
        using (var encoder = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            // 每行以 PNG 滤波类型 0 开始，随后是全透明黑色像素。
            byte[] row = new byte[checked(width * 4 + 1)];
            for (int y = 0; y < height; y++)
                encoder.Write(row);
        }
        return Encode(width, height, compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));
    }

    internal static byte[] Encode(int width, int height, ReadOnlySpan<byte> imageData)
    {
        var signature = DisplaySession.PngSignature;
        byte[] output = new byte[checked(signature.Length + 13 + 3 * 12 + imageData.Length)];
        signature.CopyTo(output);
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 6;
        Span<byte> remaining = output.AsSpan(signature.Length);
        remaining = WriteChunk(remaining, "IHDR"u8, header);
        remaining = WriteChunk(remaining, "IDAT"u8, imageData);
        WriteChunk(remaining, "IEND"u8, []);
        return output;
    }

    internal static void ValidateImageData(ReadOnlySpan<byte> png)
    {
        uint width = BinaryPrimitives.ReadUInt32BigEndian(png[16..]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(png[20..]);
        if (width > int.MaxValue || height > int.MaxValue || png[26] != 0 || png[27] != 0 || png[28] > 1)
            throw new InvalidDataException("The PNG header specifies unsupported image data.");

        using var compressed = new MemoryStream();
        ReadOnlySpan<byte> remaining = png[8..];
        bool seenImageData = false;
        bool endedImageData = false;
        while (!remaining.IsEmpty)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(remaining);
            var typeAndData = remaining.Slice(4, 4 + length);
            var type = typeAndData[..4];
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(remaining[(8 + length)..]);
            if (ComputeCrc(typeAndData) != stored)
                throw new InvalidDataException("The PNG contains a damaged chunk.");
            if (type.SequenceEqual("IDAT"u8))
            {
                if (endedImageData)
                    throw new InvalidDataException("The PNG image data chunks must be consecutive.");
                seenImageData = true;
                compressed.Write(typeAndData[4..]);
            }
            else
            {
                if (type.SequenceEqual("IHDR"u8) && remaining.Length != png.Length - 8)
                    throw new InvalidDataException("The PNG contains more than one image header.");
                endedImageData |= seenImageData;
            }
            remaining = remaining[(length + 12)..];
        }

        if (compressed.Length < 6)
            throw new InvalidDataException("The PNG contains incomplete compressed image data.");
        uint checksum = BinaryPrimitives.ReadUInt32BigEndian(
            compressed.GetBuffer().AsSpan((int)compressed.Length - 4, 4));
        compressed.Position = 0;
        using var decoder = new ZLibStream(compressed, CompressionMode.Decompress);
        byte[] buffer = new byte[4096];
        uint a = 1, b = 0;
        try
        {
            if (png[28] == 0)
            {
                ReadScanlines(width, height);
            }
            else
            {
                // Adam7 uses up to seven smaller images, each with its own scanlines.
                ReadOnlySpan<byte> passes = [0, 0, 8, 8, 4, 0, 8, 8, 0, 4, 4, 8, 2, 0, 4, 4, 0, 2, 2, 4, 1, 0, 2, 2, 0, 1, 1, 2];
                for (int pass = 0; pass < passes.Length; pass += 4)
                {
                    if (width > passes[pass] && height > passes[pass + 1])
                        ReadScanlines((width - passes[pass] + passes[pass + 2] - 1) / passes[pass + 2],
                            (height - passes[pass + 1] + passes[pass + 3] - 1) / passes[pass + 3]);
                }
            }

            if (decoder.ReadByte() != -1 || checksum != ((b << 16) | a))
                throw new InvalidDataException("The PNG's image data or checksum does not match its header.");
        }
        catch (EndOfStreamException error)
        {
            throw new InvalidDataException("The PNG contains incomplete compressed image data.", error);
        }

        void ReadScanlines(uint columns, uint rows)
        {
            for (uint row = 0; row < rows; row++)
            {
                int filter = decoder.ReadByte();
                if (filter is < 0 or > 4)
                    throw new InvalidDataException("The PNG contains an invalid or missing scanline filter.");
                a += (uint)filter;
                b += a;

                for (long bytes = columns * 4L; bytes > 0;)
                {
                    var pixels = buffer.AsSpan(0, (int)Math.Min(bytes, buffer.Length));
                    decoder.ReadExactly(pixels);
                    foreach (byte value in pixels)
                    {
                        a += value;
                        b += a;
                    }
                    a %= 65521;
                    b %= 65521;
                    bytes -= pixels.Length;
                }
            }
        }
    }

    private static Span<byte> WriteChunk(Span<byte> output, ReadOnlySpan<byte> type, scoped ReadOnlySpan<byte> data)
    {
        BinaryPrimitives.WriteInt32BigEndian(output, data.Length);
        type.CopyTo(output[4..]);
        data.CopyTo(output[8..]);

        BinaryPrimitives.WriteUInt32BigEndian(output[(8 + data.Length)..], ComputeCrc(output.Slice(4, 4 + data.Length)));
        return output[(12 + data.Length)..];
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in data)
            crc = CrcTable[(byte)(crc ^ value)] ^ (crc >> 8);
        return ~crc;
    }

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
