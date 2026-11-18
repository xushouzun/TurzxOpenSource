using TURZX.WinUsbDisplay;
using Xunit;

namespace TURZX.WinUsbDisplay.Tests;

/// <summary>验证从进程环境变量读取 SDK 设置。</summary>
public class DisplaySettingsTests
{
    /// <summary>验证未配置时返回默认接口 GUID。</summary>
    [Fact]
    public void InterfaceGuidFallsBackToDefault()
    {
        Environment.SetEnvironmentVariable("WINUSB_DISPLAY_INTERFACE_GUID", null);
        Assert.Equal(DisplayDevice.WinUsbDeviceInterface, DisplaySettings.InterfaceGuid);
    }

    /// <summary>验证环境变量可以覆盖默认接口 GUID。</summary>
    [Fact]
    public void InterfaceGuidHonorsOverride()
    {
        var guid = Guid.NewGuid();
        Environment.SetEnvironmentVariable("WINUSB_DISPLAY_INTERFACE_GUID", guid.ToString());
        try
        {
            Assert.Equal(guid, DisplaySettings.InterfaceGuid);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINUSB_DISPLAY_INTERFACE_GUID", null);
        }
    }

    /// <summary>验证 16 位十六进制文本可解析为命令密钥。</summary>
    [Fact]
    public void ReadCommandKeyParsesSixteenHex()
    {
        Environment.SetEnvironmentVariable("WINUSB_DISPLAY_COMMAND_KEY", "0011223344556677");
        try
        {
            byte[] key = DisplaySettings.ReadCommandKey();
            Assert.Equal(new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77 }, key);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINUSB_DISPLAY_COMMAND_KEY", null);
        }
    }

    /// <summary>验证拒绝长度不符和不可用的命令密钥。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("00112233445566")]
    [InlineData("001122334455667788")]
    [InlineData("0101010101010101")]
    [InlineData("01FE01FE01FE01FE")]
    public void ReadCommandKeyRejectsInvalidValues(string value)
    {
        Environment.SetEnvironmentVariable("WINUSB_DISPLAY_COMMAND_KEY", value);
        try
        {
            Assert.Throws<InvalidOperationException>(() => DisplaySettings.ReadCommandKey());
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINUSB_DISPLAY_COMMAND_KEY", null);
        }
    }
}
