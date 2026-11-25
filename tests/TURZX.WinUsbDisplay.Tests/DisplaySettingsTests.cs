using TURZX.WinUsbDisplay;
using Xunit;

namespace TURZX.WinUsbDisplay.Tests;

/// <summary>验证 SDK 设备设置。</summary>
public class DisplaySettingsTests
{
    /// <summary>验证默认接口 GUID。</summary>
    [Fact]
    public void InterfaceGuidFallsBackToDefault()
    {
        DisplaySettings.InterfaceGuid = DisplayDevice.WinUsbDeviceInterface;
        Assert.Equal(DisplayDevice.WinUsbDeviceInterface, DisplaySettings.InterfaceGuid);
    }

    /// <summary>验证可以覆盖接口 GUID。</summary>
    [Fact]
    public void InterfaceGuidHonorsOverride()
    {
        var guid = Guid.NewGuid();
        try
        {
            DisplaySettings.InterfaceGuid = guid;
            Assert.Equal(guid, DisplaySettings.InterfaceGuid);
        }
        finally
        {
            DisplaySettings.InterfaceGuid = DisplayDevice.WinUsbDeviceInterface;
        }
    }

    /// <summary>验证返回内置的八字节命令密钥。</summary>
    [Fact]
    public void ReadCommandKeyReturnsBuiltInKey()
    {
        Assert.Equal(new byte[] { 0x73, 0x6C, 0x76, 0x33, 0x74, 0x75, 0x7A, 0x78 },
            DisplaySettings.ReadCommandKey());
    }

    /// <summary>验证每次调用返回独立副本。</summary>
    [Fact]
    public void ReadCommandKeyReturnsCopy()
    {
        byte[] first = DisplaySettings.ReadCommandKey();
        byte[] second = DisplaySettings.ReadCommandKey();
        Assert.NotSame(first, second);
    }
}
