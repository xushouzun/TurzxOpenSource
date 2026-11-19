using System.Buffers.Binary;
using System.Globalization;

namespace TURZX.WinUsbDisplay.Studio;

/// <summary>将界面的十进制、十六进制和路径输入转换为协议字节。</summary>
internal static class PacketInput
{
    internal static bool HasPath(byte command, bool raw) => !raw && command is 38 or 42 or 98 or 99 or 110 or 113;

    internal static byte[] Bytes(string text, bool decimalInput) => decimalInput
        ? text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => byte.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out byte number)
                ? number : throw new FormatException()).ToArray()
        : Convert.FromHexString(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));

    internal static byte[] Arguments(byte command, bool raw, string text, int mode)
    {
        if (mode == 2)
        {
            if (!HasPath(command, raw)) throw new FormatException();
            try { return DisplaySession.EncodeDevicePath(text.Trim()); }
            catch (ArgumentException error) { throw new FormatException("Invalid device path.", error); }
        }
        if (mode == 1 && !raw && command == 51)
        {
            string[] fields = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 7 || !ushort.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out ushort year))
                throw new FormatException();
            byte[] clock = new byte[8];
            BinaryPrimitives.WriteUInt16BigEndian(clock, year);
            Bytes(string.Join(' ', fields.Skip(1)), true).CopyTo(clock, 2);
            return clock;
        }
        if (mode == 1 && (raw && command == 0x61 || !raw && command == 251))
            return EncodeCoolingArguments(text, raw);
        if (mode != 1 || raw || command is not (101 or 102 or 121 or 39 or 252))
            return Bytes(text, mode == 1);

        string[] values = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (values.Length != (command == 39 ? 3 : command == 121 ? 2 : 1)
            || !uint.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint length))
            throw new FormatException();
        byte[] arguments = new byte[command switch { 39 => 9, 121 => 5, 252 => 8, _ => 4 }];
        BinaryPrimitives.WriteUInt32BigEndian(arguments.AsSpan(command == 252 ? 4 : 0), length);
        if (command == 121)
        {
            if (!byte.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out byte final) || final > 1)
                throw new FormatException();
            arguments[4] = final;
        }
        if (command == 39)
        {
            if (!uint.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint effective)
                || effective > length || !byte.TryParse(values[2], out byte final) || final > 1)
                throw new FormatException();
            BinaryPrimitives.WriteUInt32BigEndian(arguments.AsSpan(4), effective);
            arguments[8] = final;
        }
        return arguments;
    }

    private static byte[] EncodeCoolingArguments(string text, bool raw)
    {
        string[] values = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (values.Length != (raw ? 2 : 4)
            || !ushort.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out ushort pump))
            throw new FormatException();

        if (raw)
        {
            if (!ushort.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out ushort fan))
                throw new FormatException();
            byte[] result = new byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(result, pump);
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), fan);
            return result;
        }

        byte[] synchronized = [0xFF, 0x0F, 0xA1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt16BigEndian(synchronized.AsSpan(4), pump);
        Bytes(string.Join(' ', values.Skip(1)), true).CopyTo(synchronized, 6);
        return synchronized;
    }

    internal static void Validate(byte command, bool raw, byte[] arguments, byte[] payload)
    {
        if (raw)
        {
            if (command == 0x61 && (arguments.Length is < 4 or > 63
                || arguments.AsSpan(4).ContainsAnyExcept((byte)0)))
                throw new FormatException();
            return;
        }
        bool valid = command switch
        {
            13 => arguments.Length == 1 && arguments[0] <= 3,
            14 => arguments.Length == 1 && arguments[0] <= 100,
            15 => arguments.Length == 1 && arguments[0] is >= 1 and <= 60,
            49 => arguments.Length == 1 && arguments[0] <= 1,
            51 => IsClock(arguments),
            125 => arguments is [<= 100, <= 2, 0, <= 3, 0, <= 1, 0],
            101 or 102 => arguments.Length == 4 && payload.Length is > 0 and <= DisplayFrameProcessor.MaxEncodedFrameBytes
                && BinaryPrimitives.ReadUInt32BigEndian(arguments) == payload.Length,
            121 => arguments.Length == 5 && arguments[4] <= 1 && payload.Length is > 0 and <= 4 * 1024 * 1024
                && BinaryPrimitives.ReadUInt32BigEndian(arguments) == payload.Length,
            39 => IsFileBlock(arguments, payload.Length),
            40 => payload.Length is > 0 and < 100 * 1024
                && arguments.Length >= 9
                && BinaryPrimitives.ReadUInt32BigEndian(arguments.AsSpan(4)) == payload.Length,
            251 => arguments.Length == 16 && arguments.AsSpan().StartsWith<byte>([0xFF, 0x0F, 0xA1, 0])
                && !arguments.AsSpan(9).ContainsAnyExcept((byte)0),
            252 => arguments.Length == 8 && BinaryPrimitives.ReadUInt32BigEndian(arguments) == 0
                && payload.Length > 3 && BinaryPrimitives.ReadUInt32BigEndian(arguments.AsSpan(4)) == payload.Length
                && BinaryPrimitives.ReadUInt16BigEndian(payload) != 0,
            _ => true
        };
        if (!valid) throw new FormatException();
        if (command == 102)
            ValidatePng(payload);
        if (HasPath(command, raw) || command == 40)
        {
            if (arguments.Length < 9 || BinaryPrimitives.ReadUInt32BigEndian(arguments) != arguments.Length - 8
                || (command != 40 && BinaryPrimitives.ReadUInt32BigEndian(arguments.AsSpan(4)) != 0))
                throw new FormatException();
            string path = System.Text.Encoding.ASCII.GetString(arguments.AsSpan(8));
            try
            {
                if (arguments.AsSpan(8).ContainsAnyInRange((byte)128, byte.MaxValue)
                    || (command is 40 or 42 && path.EndsWith('/'))) throw new FormatException();
                _ = DisplaySession.EncodeDevicePath(path);
            }
            catch (ArgumentException error) { throw new FormatException("Invalid device path.", error); }
        }
    }

    internal static void ValidatePng(ReadOnlySpan<byte> payload)
    {
        try { DisplaySession.ValidateOverlay(payload); }
        catch (System.IO.InvalidDataException error)
        {
            throw StudioLanguage.UiError(error, "PacketInvalidPng");
        }
    }

    private static bool IsFileBlock(byte[] arguments, int payloadLength)
    {
        if (arguments.Length != 9 || arguments[8] > 1 || payloadLength is <= 0 or > 1024 * 1024
            || BinaryPrimitives.ReadUInt32BigEndian(arguments) != payloadLength)
            return false;

        uint effective = BinaryPrimitives.ReadUInt32BigEndian(arguments.AsSpan(4));
        return effective > 0 && effective <= payloadLength;
    }

    private static bool IsClock(byte[] fields)
    {
        if (fields.Length != 8 || fields[7] > 2) return false;
        try
        {
            _ = new DateTime(BinaryPrimitives.ReadUInt16BigEndian(fields), fields[2], fields[3], fields[4], fields[5], fields[6]);
            return true;
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    internal static string Hint(byte command, bool raw, int mode) => mode switch
    {
        2 => "PacketPathInputHint",
        1 when !raw && command is 101 or 102 => "PacketDecimalLengthHint",
        1 when !raw && command == 121 => "PacketDecimalBlockHint",
        1 when !raw && command == 39 => "PacketDecimalBufferHint",
        1 when !raw && command == 51 => "PacketDecimalClockHint",
        1 when !raw && command == 13 => "PacketDecimalOrientationHint",
        1 when !raw && command == 14 => "PacketDecimalBrightnessHint",
        1 when !raw && command == 15 => "PacketDecimalFrameRateHint",
        1 when !raw && command == 49 => "PacketDecimalSyncHint",
        1 when !raw && command == 125 => "StartupConfigurationArgumentsHint",
        1 when !raw && command == 251 => "PacketDecimalPumpFansHint",
        1 when !raw && command == 252 => "PacketDecimalLengthHint",
        1 when raw && command == 0x61 => "PacketDecimalCoolingHint",
        _ => "PacketDecimalBytesHint"
    };
}
