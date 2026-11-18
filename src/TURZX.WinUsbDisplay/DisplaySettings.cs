using System.Security.Cryptography;

namespace TURZX.WinUsbDisplay;

/// <summary>从当前进程的环境变量读取设备设置。</summary>
public static class DisplaySettings
{
    /// <summary>获取已配置的设备接口 GUID，未配置时使用默认 WinUSB GUID。</summary>
    public static Guid InterfaceGuid =>
        Environment.GetEnvironmentVariable("WINUSB_DISPLAY_INTERFACE_GUID") is string guid
            ? Guid.Parse(guid) : DisplayDevice.WinUsbDeviceInterface;

    /// <summary>从十六进制环境变量读取并验证八字节固件命令密钥。</summary>
    public static byte[] ReadCommandKey()
    {
        string value = Environment.GetEnvironmentVariable("WINUSB_DISPLAY_COMMAND_KEY") ?? "";
        if (value.Length != 16)
            throw new InvalidOperationException("Set WINUSB_DISPLAY_COMMAND_KEY to the firmware's 16 hexadecimal characters.");
        byte[] key = Convert.FromHexString(value);
        if (DES.IsWeakKey(key) || DES.IsSemiWeakKey(key))
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidOperationException("WINUSB_DISPLAY_COMMAND_KEY cannot be used for device communication.");
        }
        return key;
    }
}
