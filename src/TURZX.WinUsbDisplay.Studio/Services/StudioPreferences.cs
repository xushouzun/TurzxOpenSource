using System.IO;
using System.Text.Json;

namespace TURZX.WinUsbDisplay.Studio;

/// <summary>保存单台设备的显示和启动首选项。</summary>
/// <param name="Brightness">以百分比表示的亮度。</param>
/// <param name="Rotation">以四分之一圈表示的旋转方向。</param>
internal sealed record ScreenPreferences(byte Brightness = 66, byte Rotation = 0)
{
    /// <summary>设备重启后应进入的模式。</summary>
    public byte StartupMode { get; init; }
    /// <summary>控制设备离线时是否保持唤醒。</summary>
    public bool KeepAwakeOffline { get; init; }
    /// <summary>设备离线时钟所使用的显示方向。</summary>
    public byte OfflineClockRotation { get; init; }
}

/// <summary>保存工作区的媒体来源、布局和捕捉配置。</summary>
internal sealed record MediaPreferences
{
    /// <summary>保存的媒体来源模式。</summary>
    public int SourceMode { get; init; } = 2;
    /// <summary>媒体配置所属的设备接口路径。</summary>
    public string? DevicePath { get; init; }
    /// <summary>本地图片或视频文件的路径。</summary>
    public string? ImagePath { get; init; }
    /// <summary>用户选择的本地 HTML 文件路径。</summary>
    public string? HtmlPath { get; init; }
    /// <summary>网页捕捉来源地址。</summary>
    public string? WebUrl { get; init; }
    /// <summary>轮播图片的有序路径列表。</summary>
    public string[]? SlideshowPaths { get; init; }
    /// <summary>保存在轮播设置中的切换间隔文本。</summary>
    public string? SlideshowInterval { get; init; }
    /// <summary>内置网页场景的文件名。</summary>
    public string? BuiltInScene { get; init; }
    /// <summary>用于恢复捕捉来源的窗口标题。</summary>
    public string? WindowTitle { get; init; }
    /// <summary>背景图片或视频的本地路径。</summary>
    public string? BackgroundPath { get; init; }
    /// <summary>设置中保存的目标捕捉帧率文本。</summary>
    public string? CaptureRate { get; init; }
    /// <summary>前景图层的布局设置。</summary>
    public MediaLayout? ForegroundLayout { get; init; }
    /// <summary>背景图层的布局设置。</summary>
    public MediaLayout? BackgroundLayout { get; init; }
    /// <summary>当前编辑的媒体图层索引。</summary>
    public int LayoutLayer { get; init; }
    /// <summary>归一化裁剪区域，依次保存横坐标、纵坐标、宽和高。</summary>
    public double[]? CaptureBounds { get; init; }
}

/// <summary>负责读写 Studio 的本机与逐设备首选项。</summary>
internal sealed class StudioPreferences
{
    /// <summary>与磁盘 JSON 对应的设置结构。</summary>
    private sealed class SettingsData
    {
        /// <summary>用默认值构造可反序列化的设置。</summary>
        public SettingsData() { }
        /// <summary>当前或最近一次保存的工作区媒体设置。</summary>
        public MediaPreferences? Media { get; set; }
        /// <summary>按设备接口路径保存的媒体设置。</summary>
        public Dictionary<string, MediaPreferences> DeviceMedia { get; set; } = new();
        /// <summary>持久化的界面语言代码。</summary>
        public string Language { get; set; } = StudioLanguage.DefaultLanguage;
        /// <summary>按设备接口路径保存的显示设置。</summary>
        public Dictionary<string, ScreenPreferences> Screens { get; set; } = new();
        /// <summary>自定义 WinUSB 接口 GUID，为空时使用标准接口。</summary>
        public string? InterfaceGuid { get; set; }
    }

    /// <summary>Studio 首选项文件路径。</summary>
    private readonly string path;
    /// <summary>当前实例持有的首选项快照。</summary>
    private SettingsData settings;

    /// <summary>绑定设置文件路径及已加载的首选项。</summary>
    internal StudioPreferences(string path)
    {
        this.path = Path.GetFullPath(path);
        try
        {
            settings = ReadSettings(out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            settings = new SettingsData();
        }
    }

    /// <summary>从本机设置文件加载 Studio 首选项。</summary>
    internal static StudioPreferences Load() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinUsbDisplay", "studio-settings.json"));

    /// <summary>获取保存的界面语言代码。</summary>
    internal string Language => settings.Language;

    /// <summary>获取自定义 WinUSB 接口 GUID。</summary>
    internal string? InterfaceGuid => settings.InterfaceGuid;

    /// <summary>获取设备对应的显示首选项。</summary>
    internal ScreenPreferences GetScreen(string devicePath) => GetScreen(settings, devicePath.ToUpperInvariant());

    /// <summary>按接口路径获取显示首选项，缺省时读取默认值。</summary>
    private static ScreenPreferences GetScreen(SettingsData data, string key)
    {
        var defaults = new ScreenPreferences();
        if (!data.Screens.TryGetValue(key, out var screen) || screen is null)
            return defaults;

        return screen with
        {
            Brightness = screen.Brightness <= 100 ? screen.Brightness : defaults.Brightness,
            Rotation = screen.Rotation <= 3 ? screen.Rotation : defaults.Rotation,
            StartupMode = screen.StartupMode <= 2 ? screen.StartupMode : defaults.StartupMode,
            OfflineClockRotation = screen.OfflineClockRotation <= 3 ? screen.OfflineClockRotation : defaults.OfflineClockRotation
        };
    }

    /// <summary>修改并保存界面语言。</summary>
    internal void SetLanguage(string language) => Save(data => data.Language = language);

    /// <summary>更新指定设备的显示首选项。</summary>
    internal void UpdateScreen(string devicePath, Func<ScreenPreferences, ScreenPreferences> update)
    {
        string key = devicePath.ToUpperInvariant();
        Save(data => data.Screens[key] = update(GetScreen(data, key)));
    }

    /// <summary>获取最近保存的工作区媒体设置。</summary>
    internal MediaPreferences? Media => settings.Media;

    /// <summary>读取指定接口路径对应的媒体设置。</summary>
    internal MediaPreferences? GetDeviceMedia(string devicePath) =>
        settings.DeviceMedia.GetValueOrDefault(devicePath.ToUpperInvariant());

    /// <summary>保存当前工作区媒体设置及对应设备副本。</summary>
    internal void SetMedia(MediaPreferences media) => Save(data =>
    {
        data.Media = media;
        if (media.DevicePath is { } devicePath)
            data.DeviceMedia[devicePath.ToUpperInvariant()] = media;
    });

    /// <summary>读取并反序列化磁盘中的设置快照。</summary>
    private SettingsData ReadSettings(out bool invalid)
    {
        invalid = false;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var data = JsonSerializer.Deserialize<SettingsData>(file);
            if (data is null)
            {
                invalid = true;
                return new SettingsData();
            }
            data.DeviceMedia ??= new();
            data.Screens ??= new();
            return data;
        }
        catch (FileNotFoundException) { return new SettingsData(); }
        catch (DirectoryNotFoundException) { return new SettingsData(); }
        catch (JsonException)
        {
            invalid = true;
            return new SettingsData();
        }
    }

    /// <summary>合并本次更新并以替换方式写回设置文件。</summary>
    private void Save(Action<SettingsData> update)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var ownership = AcquireWriteLock();
        // 将本次修改的字段合并到各 Studio 进程共用的最新设置中。
        SettingsData latest = ReadSettings(out bool invalid);
        update(latest);
        string pending = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(pending, JsonSerializer.Serialize(latest));
            if (invalid)
            {
                // 允许用户继续保存设置，同时保留损坏文件供手动恢复。
                string backup = path + $".corrupt-{Guid.NewGuid():N}";
                File.Replace(pending, path, backup);
            }
            else
                File.Move(pending, path, overwrite: true);
            settings = latest;
        }
        finally
        {
            File.Delete(pending);
        }
    }

    /// <summary>取得跨进程写锁以串行化设置文件更新。</summary>
    private FileStream AcquireWriteLock()
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when (attempt < 10 && error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
            {
                Thread.Sleep(15);
            }
        }
    }
}
