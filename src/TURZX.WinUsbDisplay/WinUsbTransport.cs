using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TURZX.WinUsbDisplay;

// 管理设备句柄和 WinUSB 端点，一次交换包含请求写入和应答读取。
/// <summary>通过 WinUSB 枚举并独占打开设备，执行同步请求和应答交换。</summary>
internal sealed class WinUsbTransport : IDisposable
{
    /// <summary>设备的 USB OUT 端点地址。</summary>
    private const byte Output = 0x01;
    /// <summary>设备的 USB IN 端点地址。</summary>
    private const byte Input = 0x81;
    /// <summary>为当前接口保留的跨进程独占句柄。</summary>
    private readonly FileStream ownership;
    /// <summary>打开设备接口所得的 Windows 文件句柄。</summary>
    private readonly SafeFileHandle file;
    /// <summary>由设备文件句柄初始化的 WinUSB 接口。</summary>
    private readonly InterfaceHandle usb;

    /// <summary>枚举指定接口类 GUID 对应的设备路径。</summary>
    internal static IReadOnlyList<string> Enumerate(Guid guid)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WinUSB requires Windows.");

        using var set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (set.IsInvalid)
            throw new Win32Exception();

        var paths = new List<string>();
        for (uint index = 0; ; index++)
        {
            var data = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 259)
                    return paths;
                throw new Win32Exception(error);
            }

            SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
            if (Marshal.GetLastWin32Error() != 122 || size < 8)
                throw new Win32Exception();

            IntPtr detail = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, size, out _, IntPtr.Zero))
                    throw new Win32Exception();
                paths.Add(Marshal.PtrToStringUni(detail + 4)!);
            }
            finally
            {
                Marshal.FreeHGlobal(detail);
            }
        }
    }

    /// <summary>取得设备所有权、打开接口并配置端点超时。</summary>
    internal WinUsbTransport(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WinUSB requires Windows.");

        ownership = AcquireOwnership(path);
        try
        {
            file = CreateFileW(path, 0xc0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (file.IsInvalid)
                throw DisplayOpenException.Classify(LastError("CreateFile"));
            if (!WinUsb_Initialize(file, out usb))
                throw DisplayOpenException.Classify(LastError("WinUsb_Initialize"));
            // 固件解码 H.264 背景并合成 PNG 前景时，实时媒体传输可能暂时停顿。
            // 超时过短可能中断大块写入并卡住管道，也可能错过较慢的应答。
            // 因此为读写都留出余量，控制命令仍可快速收到应答。
            uint writeTimeout = 10000;
            uint readTimeout = 5000;
            if (!WinUsb_SetPipePolicy(usb, Output, 3, sizeof(uint), ref writeTimeout))
                throw LastError($"WinUsb_SetPipePolicy(0x{Output:X2})");
            if (!WinUsb_SetPipePolicy(usb, Input, 3, sizeof(uint), ref readTimeout))
                throw LastError($"WinUsb_SetPipePolicy(0x{Input:X2})");
        }
        catch
        {
            usb?.Dispose();
            file?.Dispose();
            ownership.Dispose();
            throw;
        }
    }

    // 用独占文件句柄协调不同进程对同一设备接口的访问。
    /// <summary>按设备接口路径取得跨进程独占句柄。</summary>
    internal static FileStream AcquireOwnership(string path)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUsbDisplay", "SessionLocks");
        Directory.CreateDirectory(directory);
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        try
        {
            // 保留锁文件，关闭句柄即可原子地释放设备所有权。
            return new FileStream(Path.Combine(directory, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) when (error.HResult == unchecked((int)0x80070020))
        {
            throw DisplayOpenException.Classify(new Win32Exception(32, "Another WinUsbDisplay session owns this display."));
        }
    }

    /// <summary>发送完整请求缓冲区并返回设备应答。</summary>
    internal byte[] Exchange(byte[] request) => Exchange(request, request.Length);

    /// <summary>用于读取无需返回给调用方的帧应答的缓冲区。</summary>
    private readonly byte[] frameAcknowledgement = new byte[512];

    /// <summary>写入指定长度的请求并读取应答，可记录读写耗时。</summary>
    internal byte[] Exchange(byte[] request, int length, bool discardReply = false,
        Action<TimeSpan, TimeSpan>? onTransfer = null)
    {
        long started = onTransfer is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
        if (!WinUsb_WritePipe(usb, Output, request, length, out int written, IntPtr.Zero))
            throw LastError("WinUsb_WritePipe");
        if (written != length)
            throw new IOException($"WinUSB short write ({written}/{length} bytes).");
        long writeCompleted = onTransfer is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();

        // 逐帧发送仍等待设备应答，但调用方无需持有应答内容。
        var response = discardReply ? frameAcknowledgement : new byte[512];
        // 应答前可能收到一个零长度 USB 包。此时继续读取即可，无需重发请求。
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (!WinUsb_ReadPipe(usb, Input, response, response.Length, out int received, IntPtr.Zero))
            {
                var error = LastError("WinUsb_ReadPipe");
                if (error.NativeErrorCode is 121 or 1460)
                    throw new DeviceResponseException(DeviceResponseFailure.NoData, "Timed out waiting for the device response.", error);
                throw error;
            }

            if (received > 0)
            {
                onTransfer?.Invoke(System.Diagnostics.Stopwatch.GetElapsedTime(started, writeCompleted),
                    System.Diagnostics.Stopwatch.GetElapsedTime(writeCompleted));
                if (discardReply)
                    return [];
                Array.Resize(ref response, received);
                return response;
            }
        }
        throw new DeviceResponseException(DeviceResponseFailure.NoData, "The display returned only zero-length packets.");
    }

    /// <summary>尝试清除 USB OUT 端点的管道错误。</summary>
    internal bool TryResetOutputPipe() => WinUsb_ResetPipe(usb, Output);

    /// <summary>将最近一次 Windows 原生错误转换为异常。</summary>
    private static Win32Exception LastError(string operation)
    {
        int code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, $"{operation}: {new Win32Exception(code).Message} ({code})");
    }

    /// <summary>释放 WinUSB 接口、设备文件和跨进程独占句柄。</summary>
    public void Dispose()
    {
        usb.Dispose();
        file.Dispose();
        ownership.Dispose();
    }

    /// <summary>与 SetupAPI 的设备接口数据结构对应。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceData
    {
        /// <summary>结构体大小，调用 SetupAPI 前须初始化。</summary>
        internal int Size;
        /// <summary>接口所属设备接口类的 GUID。</summary>
        internal Guid Guid;
        /// <summary>SetupAPI 返回的接口状态标志。</summary>
        internal uint Flags;
        /// <summary>SetupAPI 保留的内部字段。</summary>
        internal IntPtr Reserved;
    }

    /// <summary>管理 SetupAPI 返回的设备信息集句柄。</summary>
    private sealed class DeviceInfoSet : SafeHandleZeroOrMinusOneIsInvalid
    {
        /// <summary>包装原生设备信息集句柄。</summary>
        private DeviceInfoSet() : base(true) { }
        /// <summary>销毁 SetupAPI 分配的设备信息集。</summary>
        protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle);
    }

    /// <summary>管理 WinUSB 初始化后返回的接口句柄。</summary>
    private sealed class InterfaceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        /// <summary>包装原生 WinUSB 接口句柄。</summary>
        private InterfaceHandle() : base(true) { }
        /// <summary>释放 WinUSB 接口句柄。</summary>
        protected override bool ReleaseHandle() => WinUsb_Free(handle);
    }

    /// <summary>获取符合指定设备接口类的设备信息集。</summary>
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern DeviceInfoSet SetupDiGetClassDevsW(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);

    /// <summary>按索引读取设备信息集中的接口。</summary>
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(DeviceInfoSet set, IntPtr info, ref Guid guid, uint index, ref InterfaceData data);

    /// <summary>读取设备接口详细信息及路径。</summary>
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(DeviceInfoSet set, ref InterfaceData data, IntPtr detail, int size, out int needed, IntPtr info);

    /// <summary>销毁 SetupAPI 设备信息集。</summary>
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    /// <summary>用 Windows 文件 API 打开设备接口。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    /// <summary>为打开的设备句柄初始化 WinUSB。</summary>
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Initialize(SafeFileHandle file, out InterfaceHandle usb);

    /// <summary>释放已初始化的 WinUSB 接口。</summary>
    [DllImport("winusb.dll")]
    private static extern bool WinUsb_Free(IntPtr usb);

    /// <summary>配置指定 USB 管道的传输策略。</summary>
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_SetPipePolicy(InterfaceHandle usb, byte endpoint, uint policy, uint size, ref uint value);

    /// <summary>复位指定 USB 管道。</summary>
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ResetPipe(InterfaceHandle usb, byte endpoint);

    /// <summary>向指定 USB 管道写入请求。</summary>
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_WritePipe(InterfaceHandle usb, byte endpoint, byte[] data, int size, out int transferred, IntPtr overlapped);

    /// <summary>从指定 USB 管道读取数据。</summary>
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ReadPipe(InterfaceHandle usb, byte endpoint, [Out] byte[] data, int size, out int transferred, IntPtr overlapped);
}
