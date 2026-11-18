using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TURZX.WinUsbDisplay.Windows;

/// <summary>从环境或 Windows 用户级加密配置读取固件命令密钥。</summary>
internal static class CommandKeyStore
{
    /// <summary>当前用户加密密钥文件的路径。</summary>
    private static string KeyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TURZX.WinUsbDisplay", "command-key.dpapi");

    /// <summary>按配置优先级读取命令密钥。</summary>
    internal static byte[] Read()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WINUSB_DISPLAY_COMMAND_KEY")))
        {
            try
            {
                return DisplaySettings.ReadCommandKey();
            }
            catch (Exception error) when (error is InvalidOperationException or FormatException)
            {
                throw LocalizableError.Mark(error, "ErrorCommandKeyUnusable");
            }
        }

        byte[] key;
        try
        {
            key = Unprotect(File.ReadAllBytes(KeyPath));
        }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw LocalizableError.Mark(new InvalidOperationException("Set WINUSB_DISPLAY_COMMAND_KEY to the firmware's 16 hexadecimal characters before launching.", error), "ErrorCommandKeyMissing");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw LocalizableError.Mark(error, "ErrorStoredCommandKeyInvalid");
        }
        if (IsUsable(key))
            return key;
        CryptographicOperations.ZeroMemory(key);
        throw LocalizableError.Mark(new InvalidDataException("The stored command key is invalid. Set WINUSB_DISPLAY_COMMAND_KEY before launching Studio."), "ErrorStoredCommandKeyInvalid");
    }

    /// <summary>检查密钥长度和内容是否可用于设备命令。</summary>
    private static bool IsUsable(byte[] key) =>
        key.Length == 8 && !DES.IsWeakKey(key) && !DES.IsSemiWeakKey(key);

    /// <summary>调用 Windows 数据保护 API 读取旧版 Studio 保存的密钥。</summary>
    private static byte[] Unprotect(byte[] data)
    {
        var source = new Blob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        Blob result = default;
        try
        {
            Marshal.Copy(data, 0, source.Data, data.Length);
            if (!CryptUnprotectData(ref source, 0, 0, 0, 0, 1, out result))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var bytes = new byte[result.Length];
            Marshal.Copy(result.Data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            for (int index = 0; index < source.Length; index++)
                Marshal.WriteByte(source.Data, index, 0);
            Marshal.FreeHGlobal(source.Data);
            if (result.Data != 0)
            {
                for (int index = 0; index < result.Length; index++)
                    Marshal.WriteByte(result.Data, index, 0);
                LocalFree(result.Data);
            }
        }
    }

    /// <summary>与 Windows 数据保护 API 的 DATA_BLOB 匹配。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        /// <summary>原生缓冲区中的有效字节数。</summary>
        internal int Length;
        /// <summary>原生数据缓冲区的指针。</summary>
        internal nint Data;
    }

    /// <summary>用 Windows 数据保护 API 解密输入缓冲区。</summary>
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved,
        nint prompt, uint flags, out Blob output);

    /// <summary>释放 Windows 数据保护 API 分配的内存。</summary>
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
