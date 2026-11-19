using TURZX.WinUsbDisplay.Studio;
using Xunit;

namespace TURZX.WinUsbDisplay.Windows.Tests;

public class PacketInputTests
{
    [Fact]
    public void DevicePathIncludesLengthAndReservedBytes() =>
        Assert.Equal(Convert.FromHexString("00000006000000002F612E6A7067"), PacketInput.Arguments(113, false, "/a.jpg", 2));

    [Theory]
    [InlineData((byte)101, "1024", "00000400")]
    [InlineData((byte)102, "4294967295", "FFFFFFFF")]
    [InlineData((byte)121, "1024 1", "0000040001")]
    public void DecimalLengthsRespectProtocolByteOrder(byte command, string input, string hex) =>
        Assert.Equal(Convert.FromHexString(hex), PacketInput.Arguments(command, false, input, 1));

    [Fact]
    public void RawChannelDoesNotInheritEncryptedLengthSchema() =>
        Assert.Equal(new byte[] { 1, 2 }, PacketInput.Arguments(101, true, "1 2", 1));

    [Fact]
    public void DecimalAndHexBytesProduceIdenticalRequests() =>
        Assert.Equal(PacketInput.Bytes("FF 00 80", false), PacketInput.Bytes("255 0 128", true));

    [Theory]
    [InlineData((byte)121, "1024 2", 1)]
    [InlineData((byte)101, "4294967296", 1)]
    [InlineData((byte)14, "256", 1)]
    [InlineData((byte)14, "-1", 1)]
    [InlineData((byte)113, "C:\\test.jpg", 2)]
    [InlineData((byte)113, "/中文.jpg", 2)]
    public void InvalidInputIsRejectedBeforeSending(byte command, string text, int mode) =>
        Assert.Throws<FormatException>(() => PacketInput.Arguments(command, false, text, mode));
}
