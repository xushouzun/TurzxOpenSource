using TURZX.WinUsbDisplay.Studio;
using System.Globalization;
using System.IO;
using Xunit;

namespace TURZX.WinUsbDisplay.Windows.Tests;

public class PacketCommandValidationTests
{
    [Fact]
    public void ParameterlessQueriesHaveNoArgumentOrPayloadEditor()
    {
        foreach (var command in DeviceCommands.All.Where(command => command.Query))
            Assert.Equal((false, false), DeviceCommands.GetRequestFields((byte)command.Code, command.Raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".en")]
    [InlineData(".ja")]
    [InlineData(".ko")]
    public void BundledProtocolDescribesEachCommandPreset(string languageSuffix)
    {
        var rows = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "docs",
            $"win207-protocol{languageSuffix}.md")).ToArray();
        foreach (var command in DeviceCommands.All)
        {
            string id = command.Raw ? $"0x{command.Code:X2}" : command.Code.ToString(CultureInfo.InvariantCulture);
            string prefix = $"| {id} | {command.ProtocolName} | ";
            Assert.Single(rows, row => row.StartsWith(prefix, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData((byte)13, "", "")]
    [InlineData((byte)13, "04", "")]
    [InlineData((byte)14, "", "")]
    [InlineData((byte)14, "65", "")]
    [InlineData((byte)15, "00", "")]
    [InlineData((byte)15, "3D", "")]
    [InlineData((byte)49, "02", "")]
    [InlineData((byte)101, "00000002", "FF")]
    [InlineData((byte)121, "0000000102", "FF")]
    [InlineData((byte)39, "000000010000000201", "FF")]
    [InlineData((byte)113, "00000007000000002F612E6A7067", "")]
    [InlineData((byte)113, "00000006000000002F2E2E2F6162", "")]
    [InlineData((byte)39, "000000000000000001", "")]
    [InlineData((byte)39, "000000010000000001", "AB")]
    [InlineData((byte)40, "00000002000000022F61", "AB")]
    [InlineData((byte)40, "00000003000000012F61", "AB")]
    [InlineData((byte)40, "00000002000000012F2F", "AB")]
    public void RejectsInvalidRequestsBeforeHardwareAccess(byte command, string arguments, string payload) =>
        Assert.Throws<FormatException>(() => PacketInput.Validate(command, false,
            Convert.FromHexString(arguments), Convert.FromHexString(payload)));

    [Fact]
    public void SmallFileHeaderAcceptsMatchingPathAndPayloadLengths() =>
        PacketInput.Validate(40, false, Convert.FromHexString("00000002000000012F61"), [0xAB]);

    [Fact]
    public void ClockDecimalInputEncodesYearAsTwoBytes()
    {
        byte[] args = PacketInput.Arguments(51, false, "2026 11 19 10 30 0 1", 1);
        Assert.Equal(Convert.FromHexString("07EA0B130A1E0001"), args);
        PacketInput.Validate(51, false, args, []);
    }

    [Fact]
    public void InvalidCalendarDateCannotBeSent()
    {
        byte[] args = PacketInput.Arguments(51, false, "2026 2 30 10 30 0 1", 1);
        Assert.Throws<FormatException>(() => PacketInput.Validate(51, false, args, []));
    }

    [Fact]
    public void FileBlockDecimalFieldsUseBigEndian()
    {
        Assert.Equal(Convert.FromHexString("001000000000040001"),
            PacketInput.Arguments(39, false, "1048576 1024 1", 1));
    }

    [Theory]
    [InlineData((byte)12, false)]
    [InlineData((byte)51, false)]
    [InlineData((byte)52, false)]
    [InlineData((byte)61, false)]
    [InlineData((byte)62, false)]
    [InlineData((byte)125, false)]
    [InlineData((byte)251, false)]
    [InlineData((byte)252, false)]
    [InlineData((byte)96, true)]
    [InlineData((byte)98, true)]
    [InlineData((byte)17, true)]
    [InlineData((byte)97, true)]
    public void UnverifiedOrBroken0123CommandsCannotBeSent(byte command, bool raw)
    {
        Assert.False(DeviceCommands.CanSend(0x0123, command, raw));
        Assert.True(DeviceCommands.CanSend(0x0070, command, raw));
    }

    [Fact]
    public void Working0123CommandsRemainAvailable()
    {
        foreach (byte command in new byte[] { 10,11,13,14,15,17,38,39,40,42,49,50,60,98,99,100,101,102,110,111,112,113,114,121,122,123,150,201 })
            Assert.True(DeviceCommands.CanSend(0x0123, command, false));
    }
}
