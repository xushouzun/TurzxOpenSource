using System.IO;
using Microsoft.Win32;
using TURZX.WinUsbDisplay.Studio.NativeTheme;

namespace TURZX.WinUsbDisplay.Studio;

/// <summary>原生主题下拉框的选项。</summary>
internal sealed record NativeThemeOption(string Name, string Path);

/// <summary>表示 3.5 寸 CH340 原生主题设备。</summary>
/// <param name="PortName">CH340 串口名，例如 COM5。</param>
/// <param name="InstanceId">Windows USB 实例 ID。</param>
/// <param name="Serial">USB 序列号或型号标记。</param>
internal sealed record NativeThemeDevice(string PortName, string InstanceId, string Serial)
{
    /// <summary>识别原生主题设备所用的合成设备路径前缀。</summary>
    internal const string PathPrefix = "serial:";
    /// <summary>设备显示名称。</summary>
    internal const string DisplayName = "3.5 inch";
    /// <summary>与原项目 3.5 寸设备一致的 USB 产品 ID。</summary>
    internal const ushort ProductId = 0x5722;
    /// <summary>3.5 寸横向画布宽度。</summary>
    internal const int Width = 480;
    /// <summary>3.5 寸横向画布高度。</summary>
    internal const int Height = 320;

    /// <summary>转换为 Studio 设备列表使用的显示设备。</summary>
    internal DisplayDevice ToDisplayDevice() =>
        new(PathPrefix + PortName, new DisplayModel(DisplayName, ProductId, Width, Height));

    /// <summary>从合成设备路径恢复串口信息。</summary>
    internal static NativeThemeDevice? FromPath(string path) =>
        path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase) && path.Length > PathPrefix.Length
            ? new NativeThemeDevice(path[PathPrefix.Length..], "", "")
            : null;

    /// <summary>枚举原项目支持的 VID_1A86:PID_5722 / USB35INCH 串口设备。</summary>
    internal static IReadOnlyList<NativeThemeDevice> Discover()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        try
        {
            using var serial = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            var activePorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (serial is not null)
                foreach (string valueName in serial.GetValueNames())
                    if (serial.GetValue(valueName) is string activePort && !string.IsNullOrWhiteSpace(activePort))
                        activePorts.Add(activePort);

            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            using var root = usb?.OpenSubKey("VID_1A86&PID_5722");
            if (root is null)
                return [];

            var devices = new List<NativeThemeDevice>();
            foreach (string instance in root.GetSubKeyNames())
            {
                // 同 VID/PID 还可能是数码管等其它形态，原项目以 USB35INCH 型号标记区分。
                if (!instance.Contains("USB35INCH", StringComparison.OrdinalIgnoreCase))
                    continue;

                using var key = root.OpenSubKey(instance);
                using var parameters = key?.OpenSubKey("Device Parameters");
                string? port = parameters?.GetValue("PortName") as string;
                if (string.IsNullOrWhiteSpace(port) || !activePorts.Contains(port))
                    continue;

                string serialNumber = key?.GetValue("SerialNumber") as string ?? instance;
                devices.Add(new NativeThemeDevice(port, instance, serialNumber));
            }
            return devices;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }
}

/// <summary>在当前项目中渲染并发送 3.5 寸原生主题。</summary>
internal sealed class NativeThemePlayer : IDisposable
{
    private NativeThemeSerialSession? session;
    private System.Drawing.Bitmap? lastFrame;
    private byte brightness = 168;
    private bool invert;
    private bool landscape;
    private int width;
    private int height;

    /// <summary>指示原生主题播放是否仍在运行。</summary>
    internal bool IsRunning => session is not null;

    /// <summary>随 Studio 发布的三个默认主题目录。</summary>
    internal static string ThemeDirectory => Path.Combine(AppContext.BaseDirectory, "NativeThemes");

    /// <summary>列出随 Studio 发布的默认主题。</summary>
    internal static IReadOnlyList<string> DiscoverFactoryThemes()
    {
        try
        {
            return Directory.Exists(ThemeDirectory)
                ? Directory.GetFiles(ThemeDirectory, "*.data")
                    .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
                    .ToArray()
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>读取主题、渲染一帧并发送到 3.5 寸屏幕。</summary>
    internal async Task StartAsync(NativeThemeDevice device, string themePath, byte brightness, bool invert,
        CancellationToken cancellation)
    {
        if (!File.Exists(themePath))
            throw new NativeThemePlayerException("The selected native theme was not found.", "SelectNativeTheme");

        await Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            using NativeThemeDocument document = NativeThemeDocument.Load(themePath);
            System.Drawing.Bitmap frame = NativeThemeRenderer.Render(document);

            NativeThemeSerialSession next;
            try
            {
                next = new NativeThemeSerialSession(device.PortName);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                frame.Dispose();
                throw new NativeThemePlayerException(error.Message, "NativeThemeSerialFailed");
            }

            try
            {
                // 原项目顺序：连接探测 -> 唤醒 -> 镜像/方向 -> 亮度 -> 完整图像。
                // 端口打开后先留出和原项目 TestConnect 相同的稳定时间，再发探测包。
                Thread.Sleep(100);
                next.TestConnection();
                Thread.Sleep(100);
                next.Wake();
                Thread.Sleep(100);
                next.SetMirrorMode(0);
                next.SetRotation(RotationFor(document.IsLandscape, invert), frame.Width, frame.Height);
                Thread.Sleep(500);
                next.SetBrightness(brightness);
                next.SendFrame(frame);
            }
            catch
            {
                frame.Dispose();
                next.Dispose();
                throw;
            }

            NativeThemeSerialSession? previous = session;
            System.Drawing.Bitmap? previousFrame = lastFrame;
            session = next;
            lastFrame = frame;
            previousFrame?.Dispose();
            this.brightness = brightness;
            this.invert = invert;
            landscape = document.IsLandscape;
            width = frame.Width;
            height = frame.Height;
            previous?.Dispose();
        }, cancellation).ConfigureAwait(false);
    }

    /// <summary>在播放期间调整背光亮度。</summary>
    internal void SetBrightness(byte value)
    {
        if (brightness == value)
            return;
        brightness = value;
        session?.SetBrightness(value);
    }

    /// <summary>在播放期间调整显示方向。</summary>
    internal void SetInvert(bool value)
    {
        if (invert == value)
            return;
        invert = value;
        if (session is not null)
        {
            session.SetRotation(RotationFor(landscape, invert), width, height);
            if (lastFrame is not null)
                session.SendFrame(lastFrame);
        }
    }

    /// <summary>按原项目规则计算方向：横屏默认 2，竖屏默认 0，反向再加 1。</summary>
    private static int RotationFor(bool landscape, bool invert) =>
        ((landscape ? 2 : 0) + (invert ? 1 : 0)) % 4;

    /// <summary>关闭串口，停止当前播放。</summary>
    internal void Stop()
    {
        NativeThemeSerialSession? active = session;
        System.Drawing.Bitmap? frame = lastFrame;
        session = null;
        lastFrame = null;
        active?.Dispose();
        frame?.Dispose();
    }

    /// <summary>停止当前播放。</summary>
    public void Dispose() => Stop();
}

/// <summary>将原生主题渲染为 WPF 可显示的预览图。</summary>
internal static class NativeThemePreview
{
    /// <summary>读取主题并渲染一张预览图。</summary>
    internal static System.Windows.Media.Imaging.BitmapImage Render(string themePath)
    {
        using NativeThemeDocument document = NativeThemeDocument.Load(themePath);
        using System.Drawing.Bitmap bitmap = NativeThemeRenderer.Render(document);
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

/// <summary>带本地化状态键的原生主题启动异常。</summary>
internal sealed class NativeThemePlayerException(string message, string statusKey) : Exception(message)
{
    /// <summary>对应的界面状态翻译键。</summary>
    internal string StatusKey { get; } = statusKey;
}
