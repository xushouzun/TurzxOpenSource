namespace TURZX.WinUsbDisplay;

/// <summary>提供设备通信设置。</summary>
public static class DisplaySettings
{
    /// <summary>内置的八字节固件命令密钥。</summary>
    private static readonly byte[] BuiltInCommandKey = Convert.FromHexString("736c763374757a78");

    /// <summary>当前进程使用的 WinUSB 设备接口 GUID。</summary>
    private static Guid interfaceGuid = DisplayDevice.WinUsbDeviceInterface;

    /// <summary>获取或设置 WinUSB 设备接口 GUID，默认使用标准 WinUSB 接口。</summary>
    public static Guid InterfaceGuid
    {
        get => interfaceGuid;
        set => interfaceGuid = value;
    }

    /// <summary>获取内置的固件命令密钥。</summary>
    public static byte[] ReadCommandKey() => (byte[])BuiltInCommandKey.Clone();
}
