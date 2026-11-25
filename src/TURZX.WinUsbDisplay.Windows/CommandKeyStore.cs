namespace TURZX.WinUsbDisplay.Windows;

/// <summary>提供内置的固件命令密钥。</summary>
internal static class CommandKeyStore
{
    internal static byte[] Read() => DisplaySettings.ReadCommandKey();
}
