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
    [InlineData((byte)42, "00000001000000002F", "")]
    [InlineData((byte)42, "00000005000000002F696D672F", "")]
    public void RejectsInvalidRequestsBeforeHardwareAccess(byte command, string arguments, string payload) =>
        Assert.Throws<FormatException>(() => PacketInput.Validate(command, false,
            Convert.FromHexString(arguments), Convert.FromHexString(payload)));

    [Fact]
    public void SmallFileHeaderAcceptsMatchingPathAndPayloadLengths() =>
        PacketInput.Validate(40, false, Convert.FromHexString("00000002000000012F61"), [0xAB]);

    [Theory]
    [InlineData((byte)42, "/img.jpg")]
    [InlineData((byte)99, "/")]
    public void ValidatesPathsForTheSelectedOperation(byte command, string path) =>
        PacketInput.Validate(command, false, PacketInput.Arguments(command, false, path, 2), []);

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
    [InlineData((byte)251, false)]
    [InlineData((byte)252, false)]
    [InlineData((byte)16, true)]
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

    [Fact]
    public void StartupConfigurationUsesTheSupportedCommandPageModel()
    {
        foreach (DisplayModel model in DisplayModel.Known)
            Assert.Equal(model.ProductId == 0x0046, DeviceCommands.CanSend(model.ProductId, 125, false));
    }

    [Theory]
    [InlineData("64020003000100", true)]
    [InlineData("65020003000100", false)]
    [InlineData("64030003000100", false)]
    [InlineData("64020103000100", false)]
    [InlineData("64020004000100", false)]
    [InlineData("64020003010100", false)]
    [InlineData("64020003000200", false)]
    [InlineData("64020003000101", false)]
    public void StartupConfigurationChecksEveryField(string hex, bool valid)
    {
        byte[] arguments = Convert.FromHexString(hex);
        if (valid)
            PacketInput.Validate(125, false, arguments, []);
        else
            Assert.Throws<FormatException>(() => PacketInput.Validate(125, false, arguments, []));
    }

    [Fact]
    public void GeneratedExamplesApplyTheSameTargetAndReplyRules()
    {
        string raw = Assert.IsType<string>(MainWindow.BuildPacketExample(16, true, [], [], false));
        Assert.DoesNotContain("0x0123", raw);
        Assert.Contains("session.ExchangeRawCommand(0x10", raw);
        Assert.Null(MainWindow.BuildPacketExample(61, false, [], [], false));

        string brightness = Assert.IsType<string>(MainWindow.BuildPacketExample(14, false, [81], [], false));
        Assert.DoesNotContain("0x0123", brightness);
        Assert.Contains("session.ExchangeMatchingCommand(14", brightness);
        Assert.Contains("0x0123", MainWindow.BuildPacketExample(14, false, [80], [], false));
        Assert.Contains("is not (0x0046)", MainWindow.BuildPacketExample(125, false, new byte[7], [], false));

        string upload = Assert.IsType<string>(MainWindow.BuildPacketExample(101, false, new byte[4], [], true));
        Assert.Contains("session.ExchangeMatchingCommand(101, arguments, payload, 2)", upload);
        Assert.True(upload.IndexOf("device.Model.ProductId", StringComparison.Ordinal)
            < upload.IndexOf("System.IO.File.OpenRead", StringComparison.Ordinal));
        Assert.Contains("session.ExchangeCommand(99", MainWindow.BuildPacketExample(99, false, [], [], false));
    }

    [Fact]
    public void GeneratedDeleteExampleShowsTheTargetAndConfirmsBeforeOpeningIt()
    {
        byte[] arguments = PacketInput.Arguments(42, false, "/images/test\".jpg", 2);
        string example = Assert.IsType<string>(MainWindow.BuildPacketExample(42, false, arguments, [], false));

        Assert.Contains("const string deviceFile = \"/images/test\\u0022.jpg\";", example);
        Assert.Contains("Device interface: {device.Path}", example);
        Assert.Contains("if (!string.Equals(Console.ReadLine(), \"DELETE\", StringComparison.Ordinal))", example);
        Assert.True(example.IndexOf("Console.ReadLine()", StringComparison.Ordinal)
            < example.IndexOf("DisplaySettings.ReadCommandKey()", StringComparison.Ordinal));
        Assert.Contains("session.ExchangeMatchingCommand(42", example);
    }

    [Fact]
    public void PngCommandsRejectNonRgbaDataAndGenerateTheSdkFormatCheck()
    {
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jZLoAAAAASUVORK5CYII=");
        byte[] arguments = PacketInput.Arguments(102, false, png.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), 1);
        var error = Assert.Throws<System.IO.InvalidDataException>(() => PacketInput.Validate(102, false, arguments, png));
        Assert.Equal("PacketInvalidPng", StudioLanguage.GetUiErrorKey(error));

        foreach (bool usesFile in new[] { false, true })
        {
            string example = Assert.IsType<string>(MainWindow.BuildPacketExample(102, false, arguments, png, usesFile));
            Assert.Contains("DisplaySession.ValidateOverlay(payload);", example);
            Assert.True(example.IndexOf("ValidateOverlay", StringComparison.Ordinal)
                < example.IndexOf("device.Open(key)", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void DisplayTargetsDoNotGetStandaloneCoolingAndLightingCommands()
    {
        foreach (var model in DisplayModel.Known)
        {
            Assert.False(DeviceCommands.CanSend(model.ProductId, 61, false));
            Assert.False(DeviceCommands.CanSend(model.ProductId, 62, false));
            foreach (byte raw in new byte[] { 0x11, 0x60, 0x61, 0x62 })
                Assert.False(DeviceCommands.CanSend(model.ProductId, raw, true));
        }
    }

    [Fact]
    public void DesktopModeIsUnavailableOnTheOriginalRoundPanel()
    {
        Assert.False(DeviceCommands.CanSend(0x0021, 150, false));
        Assert.True(DeviceCommands.CanSend(0xB021, 150, false));
    }

    [Theory]
    [InlineData((byte)0x61, true, "3500 1024", "0DAC0400")]
    [InlineData((byte)251, false, "3500 16 32 48", "FF0FA1000DAC10203000000000000000")]
    public void CoolingInputsEncodeTheControllerLayout(byte command, bool raw, string input, string expected)
    {
        byte[] arguments = PacketInput.Arguments(command, raw, input, 1);
        Assert.Equal(Convert.FromHexString(expected), arguments);
        PacketInput.Validate(command, raw, arguments, []);
    }

    [Fact]
    public void RgbAnimationLengthIncludesTheFrameHeader()
    {
        byte[] arguments = PacketInput.Arguments(252, false, "4", 1);
        Assert.Equal(Convert.FromHexString("0000000000000004"), arguments);
        PacketInput.Validate(252, false, arguments, [0, 1, 20, 0xAA]);
        Assert.Throws<FormatException>(() => PacketInput.Validate(252, false, arguments, [0, 1, 20]));
        Assert.Throws<FormatException>(() => PacketInput.Validate(252, false, arguments, [0, 0, 20, 0xAA]));
    }
}
