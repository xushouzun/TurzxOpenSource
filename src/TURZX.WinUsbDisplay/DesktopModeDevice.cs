using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TURZX.WinUsbDisplay;

/// <summary>通过原厂桌面模式 HID 通道恢复 USB 副屏模式。</summary>
public sealed class DesktopModeDevice
{
    /// <summary>桌面模式 HID 接口路径。</summary>
    public string Path { get; }
    /// <summary>设备 USB 序列号，用于明确选择回切目标。</summary>
    public string Serial { get; }
    private readonly int reportLength;

    private DesktopModeDevice(string path, string serial, int length) => (Path, Serial, reportLength) = (path, serial, length);

    /// <summary>枚举 1A86:AD11 桌面模式接口，仅读取描述符，不发送控制报告。</summary>
    public static IReadOnlyList<DesktopModeDevice> Discover()
    {
        if (!OperatingSystem.IsWindows()) return [];
        HidD_GetHidGuid(out Guid guid);
        var devices = new List<DesktopModeDevice>();
        foreach (string path in WinUsbTransport.Enumerate(guid))
        {
            if (!path.Contains("vid_1a86&pid_ad11", StringComparison.OrdinalIgnoreCase)) continue;
            using var handle = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid || !HidD_GetPreparsedData(handle, out IntPtr data)) continue;
            try
            {
                if (HidP_GetCaps(data, out Caps caps) != 0x00110000 || caps.OutputLength is < 9 or > 4096) continue;
                string serial = ReadSerial(handle);
                if (serial.Length != 0) devices.Add(new DesktopModeDevice(path, serial, caps.OutputLength));
            }
            finally { HidD_FreePreparsedData(data); }
        }
        // A serial identifies the panel. Do not pick an arbitrary output collection for it.
        return devices.GroupBy(device => device.Serial, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single()).ToArray();
    }

    /// <summary>发送一份原厂 SwitchToMonitor 报告。设备重新枚举需由调用方另行确认。</summary>
    public async Task SwitchToMonitorAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        using var handle = CreateFileW(Path, 0x40000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (ReadSerial(handle) != Serial) throw new IOException("The selected desktop-mode device identity changed.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var stream = new FileStream(handle, FileAccess.Write, 1, isAsync: true);
        try
        {
            await stream.WriteAsync(BuildMonitorReport(reportLength), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("The desktop-mode HID report write timed out.");
        }
    }

    internal static byte[] BuildMonitorReport(int length)
    {
        if (length is < 9 or > 4096) throw new ArgumentOutOfRangeException(nameof(length));
        byte[] report = new byte[length];
        // Report ID 0 + 原项目 HIDUsb.SwitchToMonitor 的第一份数据；不追加兼容变体。
        new byte[] { 0x35, 0x66, 0x33, 0x37, 0x35, 0x39, 0x64, 0x66 }.CopyTo(report, 1);
        return report;
    }

    private static string ReadSerial(SafeFileHandle handle)
    {
        byte[] bytes = new byte[512];
        return HidD_GetSerialNumberString(handle, bytes, bytes.Length)
            ? Encoding.Unicode.GetString(bytes).TrimEnd('\0') : "";
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct Caps { [FieldOffset(6)] internal ushort OutputLength; }
    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, byte[] buffer, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
}
