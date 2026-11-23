using static TURZX.WinUsbDisplay.Studio.StudioLanguage;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using TURZX.WinUsbDisplay.Media;

namespace TURZX.WinUsbDisplay.Studio;

/// <summary>承载 Studio 主界面并协调设备、媒体和协议工具。</summary>
public partial class MainWindow : Window
{
    /// <summary>可选作图片来源的文件扩展名。</summary>
    private static readonly string[] Images = [".jpg", ".jpeg", ".png"];
    /// <summary>视频启动提示和播放状态共用的翻译键。</summary>
    private const string SendingVideoToDisplayKey = "SendingVideoToDisplay";
    /// <summary>可选作视频来源的文件扩展名。</summary>
    private static readonly string[] Videos = [".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".h264", ".264"];
    /// <summary>当前选择的本地媒体文件。</summary>
    private string? media;
    /// <summary>指示窗口已开始执行关闭流程。</summary>
    private bool closing;
    /// <summary>等待调度器完成窗口关闭的操作。</summary>
    private DispatcherOperation? pendingClose;
    /// <summary>窗口关闭时取消设备控制操作。</summary>
    private readonly CancellationTokenSource deviceControlShutdown = new();
    /// <summary>指示一次设备枚举仍在执行。</summary>
    private bool refreshing;
    /// <summary>记录控制操作结束后是否需要刷新设备列表。</summary>
    private bool refreshAfterOperation;
    /// <summary>屏蔽重建选择列表时触发的设备切换事件。</summary>
    private bool updatingDeviceChoices;
    /// <summary>等待用户回应的确认操作。</summary>
    private TaskCompletionSource<bool>? confirmation;
    /// <summary>快捷控件当前对应的设备接口路径。</summary>
    private string? quickControlDevicePath;
    /// <summary>主窗口使用的持久化设置。</summary>
    private readonly StudioPreferences preferences;
    /// <summary>阻止加载快捷控件值时写回设置。</summary>
    private bool loadingQuickControls = true;
    /// <summary>指示语言选择器已完成初始化。</summary>
    private bool languageReady;
    /// <summary>合并短时间内连续产生的亮度修改。</summary>
    private readonly DispatcherTimer quickControlTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    /// <summary>按设备路径保存尚待发送的亮度值。</summary>
    private readonly Dictionary<string, byte> pendingBrightness = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>最近一次应用到窗口的已连接设备快照。</summary>
    private DisplayDevice[] devices = [];
    /// <summary>枚举原项目 3.5 寸原生主题串口设备。</summary>
    private readonly Func<NativeThemeDevice[]> discoverNativeThemes;
    /// <summary>调用原项目播放 3.5 寸原生主题。</summary>
    private readonly NativeThemePlayer nativeThemePlayer = new();
    /// <summary>指示原生主题播放是否由本窗口启动。</summary>
    private bool nativeThemePlaying;
    /// <summary>指示正在启动或停止原生主题播放。</summary>
    private bool nativeThemeBusy;
    /// <summary>取消正在进行中的原生主题预览渲染。</summary>
    private CancellationTokenSource? nativePreviewCancellation;
    /// <summary>优先恢复选择的设备接口路径。</summary>
    private string? preferredDevicePath;
    /// <summary>为设备枚举和待重连续播安排后续检查。</summary>
    private readonly DispatcherTimer deviceTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Func<DisplayDevice[]> discoverDisplays;

    /// <summary>初始化主窗口及其控件。</summary>
    public MainWindow() : this(StudioPreferences.Load()) { }

    /// <summary>使用指定的持久化设置初始化主窗口。</summary>
    internal MainWindow(StudioPreferences preferences, Func<DisplayDevice[]>? discoverDisplays = null,
        Func<NativeThemeDevice[]>? discoverNativeThemes = null)
    {
        this.preferences = preferences;
        this.discoverDisplays = discoverDisplays ??
            (static () => DisplayDevice.Discover(DisplaySettings.InterfaceGuid).ToArray());
        this.discoverNativeThemes = discoverNativeThemes
            ?? new Func<NativeThemeDevice[]>(static () => NativeThemeDevice.Discover().ToArray());
        StudioLanguage.Set(preferences.Language);
        InitializeComponent();
        LanguageSelector.ItemsSource = StudioLanguage.Options;
        LanguageSelector.SelectedValue = StudioLanguage.Current;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(StudioLanguage.Current);
        languageReady = true;
        loadingQuickControls = false;
        quickControlTimer.Tick += async (_, _) => await SendPendingSettingsAsync();
        CommandList.ItemsSource = CommandEntries;
        CommandList.SelectedItem = DeviceCommands.All.First();
        LiveSource.SelectedIndex = MediaSourceMode;
        LayoutMode.SelectedIndex = 0;
        MediaNav.IsChecked = true;
        DeviceMediaFolder.SelectedIndex = 0;
        UpdateAdvancedControls();
        Loaded += async (_, _) =>
        {
            if (!mediaPreferencesRestored)
                RestoreMediaPreferences();
            await RefreshDevicesAsync();
        };
        deviceTimer.Tick += async (_, _) =>
        {
            deviceTimer.Stop();
            await RefreshDevicesAsync();
        };
        Closed += (_, _) =>
        {
            pendingClose?.Abort();
            deviceControlShutdown.Dispose();
            HomeCanvas.StopVideo();
        };
    }

    /// <summary>表示设备列表中的一个选项及其播放状态。</summary>
    /// <param name="device">列表项对应的设备。</param>
    /// <param name="Connected">设备是否仍连接。</param>
    private sealed class DeviceChoice(DisplayDevice device, bool Connected = true) : INotifyPropertyChanged
    {
        /// <summary>选项代表的设备及型号信息。</summary>
        public DisplayDevice Device { get; } = device;
        /// <summary>3.5 寸原生主题设备信息；普通 WinUSB 屏幕为空。</summary>
        public NativeThemeDevice? NativeTheme { get; } = NativeThemeDevice.FromPath(device.Path);
        /// <summary>指示列表项是否为 3.5 寸原生主题设备。</summary>
        public bool IsNativeTheme => NativeTheme is not null;
        /// <summary>指示对应设备接口当前是否在线。</summary>
        public bool Connected { get; } = Connected;
        /// <summary>设备选项的绑定属性发生变化时触发。</summary>
        public event PropertyChangedEventHandler? PropertyChanged;
        /// <summary>缓存设备列表中显示的播放状态。</summary>
        private string? playbackLabel;
        /// <summary>显示在设备选项旁的播放状态。</summary>
        public string PlaybackLabel => playbackLabel ?? T("Idle");
        /// <summary>设置播放状态，并通知绑定更新。</summary>
        internal void SetPlaybackLabel(string label)
        {
            if (playbackLabel == label)
                return;
            playbackLabel = label;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlaybackLabel)));
        }

        /// <summary>获取设备选项的显示名称。</summary>
        public string Label
        {
            get
            {
                if (NativeTheme is { } native)
                    return $"{Device.Model.Name} · {native.PortName}";

                string productId = $"{Device.Model.ProductId:X4}";
                string name = Device.Model.Name;
                string suffix = $" ({productId})";
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    name = name[..^suffix.Length];

                return $"{name} · PID {productId}" + (InstanceSuffix is { } instance ? $" · {instance}" : "");
            }
        }

        /// <summary>获取区分同型号设备的实例标识。</summary>
        private string? InstanceSuffix
        {
            get
            {
                if (Device.Path.Split('#') is not [_, _, { Length: > 0 } instance, ..])
                    return null;

                // 含分隔符的 Windows 实例 ID 由系统生成，不能作为设备序列号使用。
                if (!instance.All(char.IsAsciiLetterOrDigit))
                {
                    byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(instance.ToUpperInvariant()));
                    return Convert.ToHexStringLower(hash.AsSpan(0, 3));
                }

                return instance[^Math.Min(6, instance.Length)..].ToLowerInvariant();
            }
        }
        /// <summary>获取用于区分设备的补充详情。</summary>
        public string Details => NativeTheme is { } native
            ? $"{Device.Model.Width} × {Device.Model.Height}   ·   USB35INCH   ·   {native.PortName}"
            : $"{Device.Model.Width} × {Device.Model.Height}   ·   VID {DisplayModel.VendorId:X4}   ·   PID {Device.Model.ProductId:X4}"
                + (InstanceSuffix is { } suffix ? $"   ·   {suffix}" : "");
    }

    /// <summary>安装窗口消息钩子，接收设备变化通知。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(OnDeviceMessage);
    }

    /// <summary>接收设备树变化消息并安排重新枚举。</summary>
    private nint OnDeviceMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int deviceChange = 0x0219; // WM_DEVICECHANGE，设备变化消息。
        const int deviceNodesChanged = 0x0007; // DBT_DEVNODES_CHANGED，设备节点变化通知。
        if (!closing && message == deviceChange && wParam == deviceNodesChanged)
        {
            deviceTimer.Stop();
            deviceTimer.Start();
        }
        return 0;
    }

    /// <summary>异步枚举设备，并处理断开和恢复播放。</summary>
    internal async Task RefreshDevicesAsync()
    {
        if (closing)
            return;
        if (refreshing)
        {
            deviceTimer.Start();
            return;
        }
        if (controlBusy)
        {
            refreshAfterOperation = true;
            return;
        }

        refreshing = true;
        try
        {
            var found = await Task.Run(discoverDisplays);
            var native = await Task.Run(discoverNativeThemes);
            var all = found.Concat(native.Select(device => device.ToDisplayDevice())).ToArray();
            if (closing)
                return;
            if (nativeThemePlaying && SelectedDeviceChoice is { IsNativeTheme: true } selectedNative
                && all.All(device => !string.Equals(device.Path, selectedNative.Device.Path, StringComparison.OrdinalIgnoreCase)))
            {
                nativeThemePlayer.Stop();
                nativeThemePlaying = false;
                Status.Text = T("NativeThemeStopped");
            }
            // 先处理断开并登记续播，再用枚举结果重建设备列表。
            CancelDisconnectedPlaybacks(all);

            if (controlBusy)
            {
                refreshAfterOperation = true;
                return;
            }

            ApplyDeviceSnapshot(all);
            ResumeConnectedPlaybacks(all);
        }
        catch (Exception ex)
        {
            if (!closing && devices.Length == 0)
                WorkspaceDevice.ToolTip = DescribeUiError(ex);
        }
        finally
        {
            refreshing = false;
        }
    }

    /// <summary>用最新的设备快照重建列表并保留可恢复的选择。</summary>
    internal void ApplyDeviceSnapshot(DisplayDevice[] found, bool refreshChoices = false, string? selectDevicePath = null)
    {
        if (!refreshChoices && selectDevicePath is null && devices.SequenceEqual(found))
        {
            UpdateDeviceSummary();
            return;
        }

        bool wasSelected = SelectedDeviceChoice is { Connected: true };
        string? preferred = selectDevicePath ?? preferredDevicePath;
        devices = found;
        // 等待重连续播的设备保留在列表中，并标记为已断开。
        var choices = found.Select(device => new DeviceChoice(device))
            .Concat(pendingResumes.Values.Where(entry => !found.Any(device =>
                string.Equals(device.Path, entry.Device.Path, StringComparison.OrdinalIgnoreCase)))
                .Select(entry => new DeviceChoice(entry.Device, Connected: false))).ToArray();
        updatingDeviceChoices = true;
        try
        {
            WorkspaceDevice.ItemsSource = choices;
            WorkspaceDevice.SelectedItem = preferred is null ? choices.FirstOrDefault()
                : choices.FirstOrDefault(choice => string.Equals(choice.Device.Path, preferred, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            updatingDeviceChoices = false;
        }
        UpdateSelectedDevice();

        if (CurrentPlayback is not null)
            return;

        if (preferred is not null && SelectedDeviceChoice is not { Connected: true })
        {
            quickControlDevicePath = null;
            Status.Text = T(HasPendingResume ? "DisplayDisconnectedWaitingToResume" : "WaitingForTheSelectedDisplayToReconnect");
        }
        else if (preferred is not null && !wasSelected)
        {
            Status.Text = T("TheSelectedDisplayHasReconnected");
        }
        else
        {
            Status.Text = found.Length == 0 ? T("NoDisplaysConnected") : T("DisplaysFound", found.Length);
        }

        UpdateDeviceSummary();
    }

    /// <summary>返回当前选择的设备列表项，包括断开的项。</summary>
    private DeviceChoice? SelectedDeviceChoice => WorkspaceDevice.SelectedItem as DeviceChoice;
    /// <summary>返回当前选择且仍在线的 WinUSB 设备。</summary>
    private DisplayDevice? SelectedDevice => SelectedDeviceChoice is { Connected: true, IsNativeTheme: false } choice
        ? choice.Device : null;
    /// <summary>返回当前选择且仍在线的 3.5 寸原生主题设备。</summary>
    private NativeThemeDevice? SelectedNativeTheme => SelectedDeviceChoice is { Connected: true, IsNativeTheme: true } choice
        ? choice.NativeTheme : null;

    /// <summary>在设备切换后同步媒体、控制面板和预览。</summary>
    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingDeviceChoices)
            return;

        UpdateSelectedDevice();
        if (SelectedNativeTheme is not null)
        {
            Status.Text = nativeThemePlaying
                ? T("NativeThemePlaying", Path.GetFileNameWithoutExtension(NativeThemePath.Text))
                : T("NativeThemeHint");
            return;
        }
        if (CurrentPlayback is null)
        {
            Status.Text = HasPendingResume ? T("WaitingToReconnect")
                : SelectedDeviceChoice is { Device: var device } && playbackErrors.TryGetValue(device.Path, out var error)
                    ? T("PlaybackFailureDetails", DescribeUiError(error))
                    : T("Idle");
        }
    }

    /// <summary>前一次关联到媒体预览的设备接口路径。</summary>
    private string? previewDevicePath;

    /// <summary>将所选设备状态应用到各工作区控件。</summary>
    private void UpdateSelectedDevice()
    {
        string? path = SelectedDevice?.Path;
        Volatile.Write(ref previewDevicePath, path);
        if (!string.Equals(packetDevicePath, path, StringComparison.OrdinalIgnoreCase))
        {
            packetDevicePath = path;
            lastPacketError = null;
            lastPacketReply = null;
            SetPacketReplyStatus("WaitingForARequest");
        }


        if (SelectedDeviceChoice is { Device: var device } choice)
        {
            preferredDevicePath = device.Path;
            if (choice.IsNativeTheme)
            {
                SetNativeThemeMode(true);
            }
            else
            {
                SetNativeThemeMode(false);
                SwitchDeviceMedia(device.Path);
            }
        }
        else
        {
            SetNativeThemeMode(false);
        }
        UpdateDeviceSummary();
        UpdateQuickControls();
        UpdatePreviewDevice();
        UpdateControls();
        UpdatePlaybackIndicators();
    }

    /// <summary>切换 3.5 寸原生主题与普通 WinUSB 前景面板。</summary>
    private void SetNativeThemeMode(bool enabled)
    {
        if (!enabled && nativeThemePlaying)
        {
            nativeThemePlayer.Stop();
            nativeThemePlaying = false;
        }
        LiveSettings.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        NativeThemeSettings.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        LayoutBar.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        HomeCanvas.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        if (!enabled)
        {
            nativePreviewCancellation?.Cancel();
            NativeThemeImage.Source = null;
            NativeThemeImage.Visibility = Visibility.Collapsed;
        }
        else
        {
            if (NativeThemePath.Text.Length == 0)
                NativeThemePath.Text = NativeThemePlayer.DiscoverFactoryThemes().FirstOrDefault() ?? "";
            _ = UpdateNativeThemePreviewAsync();
            Status.Text = T("NativeThemeHint");
        }
    }

    /// <summary>渲染所选原生主题并显示在预览区。</summary>
    private async Task UpdateNativeThemePreviewAsync()
    {
        nativePreviewCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        nativePreviewCancellation = cancellation;
        string theme = NativeThemePath.Text.Trim();
        if (SelectedNativeTheme is null || !File.Exists(theme))
        {
            NativeThemeImage.Source = null;
            NativeThemeImage.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var image = await Task.Run(() => NativeThemePreview.Render(theme), cancellation.Token);
            if (cancellation.IsCancellationRequested || SelectedNativeTheme is null)
                return;
            NativeThemeImage.Source = image;
            NativeThemeImage.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
        {
            NativeThemeImage.Source = null;
            NativeThemeImage.Visibility = Visibility.Collapsed;
            Status.Text = T("NativeThemeStartFailed", DescribeUiError(error));
        }
        finally
        {
            if (ReferenceEquals(nativePreviewCancellation, cancellation))
                nativePreviewCancellation = null;
            cancellation.Dispose();
        }
    }

    /// <summary>根据所选设备刷新亮度和旋转快捷操作。</summary>
    private void UpdateQuickControls()
    {
        if (SelectedDeviceChoice is not { Device: var device } || quickControlDevicePath == device.Path)
            return;

        quickControlDevicePath = device.Path;
        LoadQuickControlValues(preferences.GetScreen(device.Path));
        UpdateQuickControlPrompts(device.Model);
    }

    /// <summary>按设备型号刷新快捷设置的提示。</summary>
    private void UpdateQuickControlPrompts(DisplayModel model)
    {
        BrightnessValue.ToolTip = model.ProductId == 0x0123
            ? T("BrightnessLimitHint")
            : T("AutoSendBrightnessHint");
    }

    /// <summary>将已保存的亮度和旋转值填入快捷控件。</summary>
    private void LoadQuickControlValues(ScreenPreferences screen)
    {
        loadingQuickControls = true;
        BrightnessValue.Value = screen.Brightness;
        Rotation.SelectedIndex = screen.Rotation;
        loadingQuickControls = false;
    }

    /// <summary>显示所选设备的连接状态和型号摘要。</summary>
    private void UpdateDeviceSummary()
    {
        string summary = devices.Length == 0 ? T("ConnectDisplayHint")
            : SelectedDeviceChoice is not { Connected: true } ? T("ConnectedSelectADisplay", devices.Length)
            : T("ConnectedAutoRefresh", devices.Length);
        WorkspaceDevice.ToolTip = WorkspaceDevice.SelectedItem is DeviceChoice choice
            ? $"{choice.Details}\n{summary}" : summary;
    }

    /// <summary>更新应用语言、当前界面和持久化首选项。</summary>
    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!languageReady || LanguageSelector.SelectedItem is not StudioLanguage.Option { Code: var language }
            || StudioLanguage.Current == language)
            return;

        StudioLanguage.Set(language);
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(language);
        UpdatePlaybackIndicators();
        ResetAdvancedStatus();
        UpdateMediaControls();
        UpdatePacketFields();
        RenderPacketReply();
        UpdateDeviceSummary();
        if (SelectedDevice is { Model: var model })
            UpdateQuickControlPrompts(model);

        var command = CommandList.SelectedItem;
        synchronizingCommand = true;
        try
        {
            CommandList.ItemsSource = CommandEntries;
            CommandList.SelectedItem = command;
        }
        finally { synchronizingCommand = false; }
        if (!IsDesktopRecoverySelected)
            UpdateCommandDescription();
        UpdateCommandControls();

        try
        {
            preferences.SetLanguage(language);
            if (CurrentPlayback is null)
                Status.Text = T("LanguageChanged");
            if (ProtocolPage.IsVisible)
                ShowSelectedDocumentation();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text = T("CouldNotSaveSettings", DescribeUiError(ex));
        }
    }

    /// <summary>应用所选内置网页场景。</summary>
    private void OnPreset(object sender, SelectionChangedEventArgs e)
    {
        if (busy || restoringMediaPreferences || LiveSource.SelectedIndex != 0
            || sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string file } })
            return;

        string path = Path.Combine(ScenesDirectory, file);
        if (string.Equals(LivePath.Text, path, StringComparison.OrdinalIgnoreCase))
            return;

        SelectHtmlSource(path);
        Status.Text = SelectedDevice is null ? T("SceneLoadedSelectTargetDisplay") : T("SceneLoadedStartOrAddBackground");
    }

    /// <summary>打开文件选择器以选取本地媒体。</summary>
    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Filter = T("Images") + "|*.jpg;*.jpeg;*.png"
        };
        if (picker.ShowDialog(this) == true)
            await SelectMediaAsync(picker.FileName);
    }

    /// <summary>根据扩展名检查是否为受支持的图片。</summary>
    private static bool IsImage(string path) => Images.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>根据扩展名检查是否为受支持的视频。</summary>
    private static bool IsVideo(string path) => Videos.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>设置当前媒体文件并刷新来源和预览。</summary>
    private async Task SelectMediaAsync(string path)
    {
        if (busy)
            return;

        foregroundBrowse?.Cancel();
        using var selection = CancellationTokenSource.CreateLinkedTokenSource(deviceControlShutdown.Token);
        foregroundBrowse = selection;
        try
        {
            if (!IsImage(path))
                throw UiError(new InvalidDataException("Use a JPEG or PNG image for the foreground."), "ForegroundImageMustBeJpegOrPng");
            var preview = await LoadForegroundThumbnailAsync(path, selection.Token);
            if (selection.IsCancellationRequested || busy)
                return;
            foregroundBrowse = null;
            media = path;
            mediaThumbnail = preview;
            LiveSource.SelectedIndex = MediaSourceMode;
            RestoreHomePreview();
            RestartForegroundPreview();
            Status.Text = T("MediaIsReady");
            UpdateControls();
        }
        catch (OperationCanceledException) when (selection.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!selection.IsCancellationRequested)
                Status.Text = DescribeUiError(ex);
        }
        finally
        {
            if (ReferenceEquals(foregroundBrowse, selection))
                foregroundBrowse = null;
        }
    }

    /// <summary>根据选定媒体和设备启动发送任务。</summary>
    private async void OnSend(object sender, RoutedEventArgs e)
    {
        UpdateMediaControls();
        if (busy)
            return;
        if (SelectedNativeTheme is { } nativeTheme)
        {
            await StartNativeThemeAsync(nativeTheme);
            return;
        }
        if (!Send.IsEnabled || SelectedDevice is not { } device)
            return;

        pendingResumes.Remove(device.Path);
        SaveMediaPreferences();
        string background = BackgroundPath.Text.Trim();
        int mode = LiveSource.SelectedIndex;
        if (mode is 0 or 1 or WebSourceMode && HasForegroundSource())
        {
            await StartLiveAsync(mode == WebSourceMode ? 0 : mode, LiveSourceText(mode));
            return;
        }

        if (mode == SlideshowSourceMode && HasForegroundSource())
        {
            await StartLiveAsync(mode, "");
            return;
        }

        string? foreground = mode == MediaSourceMode ? media : null;
        bool backgroundVideo = IsVideo(background);
        if (backgroundVideo && foreground is not null)
        {
            await StartLiveAsync(MediaSourceMode, foreground);
            return;
        }

        byte rotation = (byte)PreviewQuarterTurns;
        var frontLayout = EffectiveLayout();
        var backLayout = EffectiveLayout(background: true);
        await RunPlaybackAsync(device, async active =>
        {
            byte[] key = CommandKeyStore.Read();
            var token = active.Token;
            try
            {
                await ValidateLocalSourcesAsync(background, html: null, slides: [], token);
                if (backgroundVideo)
                {
                    await SendVideoAsync(active, device, background, key, rotation, backLayout);
                    return;
                }

                byte[]? front = foreground is null ? null
                    : await ReadLocalMediaAsync(() => File.ReadAllBytesAsync(foreground, token), token);
                byte[]? back = background.Length == 0 ? null
                    : await ReadLocalMediaAsync(() => File.ReadAllBytesAsync(background, token), token);
                byte[] data = await Task.Run(() =>
                    DisplayFrameProcessor.PreparePlaybackImage(front, frontLayout, back, backLayout, device.Model,
                        rotation: MediaFrameRotation(device.Model, rotation)), token);
                token.ThrowIfCancellationRequested();
                SetPlaybackStatus(active, T("SendingImage"));
                await Task.Run(async () =>
                {
                    using var display = OpenPlaybackDevice(device, key, rotation, token,
                        clearLegacyOverlay: !data.AsSpan().StartsWith(DisplaySession.JpegSignature));
                    token.ThrowIfCancellationRequested();
                    using var imagePlaybackTimer = new PlaybackTimer();
                    display.PlayImage(data, DefaultCaptureFps, token, () => Dispatcher.Invoke(() =>
                    {
                        SetPlaybackFrame(active, data, overlay: false);
                        if (!token.IsCancellationRequested)
                            SetPlaybackStatus(active, T("PlayingImage"));
                    }));
                }, token);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }, backgroundVideo ? T("PreparingVideo") : T("PreparingImage"), resumeOnReconnect: true);
    }

    /// <summary>选择任意位置的原生主题 JSON 文件。</summary>
    private void OnBrowseNativeTheme(object sender, RoutedEventArgs e)
    {
        string? initial = NativeThemePlayer.DiscoverFactoryThemes().FirstOrDefault();
        var picker = new OpenFileDialog
        {
            Filter = $"{T("NativeTheme")} (*.data)|*.data",
            InitialDirectory = initial is null ? "" : Path.GetDirectoryName(initial)!,
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true)
            return;
        NativeThemePath.Text = picker.FileName;
        _ = UpdateNativeThemePreviewAsync();
        UpdateMediaControls();
    }

    /// <summary>调用原项目播放 3.5 寸原生主题。</summary>
    private async Task StartNativeThemeAsync(NativeThemeDevice nativeTheme)
    {
        string theme = NativeThemePath.Text.Trim();
        if (!File.Exists(theme))
        {
            Status.Text = T("SelectNativeTheme");
            return;
        }

        nativeThemeBusy = true;
        UpdateControls();
        Status.Text = T("SendingLiveOutput");
        try
        {
            await nativeThemePlayer.StartAsync(nativeTheme, theme, deviceControlShutdown.Token);
            nativeThemePlaying = true;
            SelectedDeviceChoice?.SetPlaybackLabel(T("Playing"));
            Status.Text = T("NativeThemePlaying", Path.GetFileNameWithoutExtension(theme));
        }
        catch (NativeThemePlayerException error)
        {
            Status.Text = T(error.StatusKey);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            Status.Text = T("NativeThemeStartFailed", DescribeUiError(error));
        }
        finally
        {
            nativeThemeBusy = false;
            UpdateControls();
            UpdateMediaControls();
        }
    }

    /// <summary>显示或清除当前视频启动阶段提示。</summary>
    private void ApplyVideoStartupStage(string? stageKey)
    {
        if (stageKey is not null)
            VideoStartupStage.SetResourceReference(TextBlock.TextProperty, ResourceKey(stageKey));
        bool opening = stageKey is not null && VideoStartupProgress.Visibility != Visibility.Visible;
        VideoStartupProgress.Visibility = stageKey is null ? Visibility.Collapsed : Visibility.Visible;
        if (opening)
            VideoStartupCancel.Focus();
    }

    /// <summary>读取设备视频播放使用的帧率。</summary>
    private static async Task<byte> ReadDeviceVideoFrameRateAsync(string path, CancellationToken token)
    {
        double? source = await Task.Run(() => VideoConverter.DetectSourceFrameRateAsync(path, token), token);
        if (source is not >= 1)
            throw UiError(new InvalidDataException("The source video frame rate is unavailable or below 1 FPS."), "ErrorSourceFrameRateUnavailable");
        return (byte)Math.Min(60, Math.Floor(source.Value));
    }

    /// <summary>查找或创建与设备输出参数匹配的视频缓存。</summary>
    private async Task<DeviceVideoCache.Entry> PrepareDeviceVideoAsync(DevicePlayback active, string path, DisplayModel model, byte fps,
        MediaLayout layout, byte rotation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string source = Path.GetFullPath(path);
        // 从查询缓存到发布编码文件，保持源文件稳定。
        var input = await ReadLocalMediaAsync(() =>
        {
            var file = File.OpenRead(source);
            try
            {
                token.ThrowIfCancellationRequested();
                string executable = VideoConverter.ResolveExecutable();
                FileInfo? encoder = Path.IsPathFullyQualified(executable) ? new FileInfo(executable) : null;
                string encoderIdentity = encoder is { Exists: true }
                    ? FormattableString.Invariant($"{encoder.FullName}|{encoder.Length}|{encoder.LastWriteTimeUtc.Ticks}")
                    : FormattableString.Invariant($"{executable}|{Environment.GetEnvironmentVariable("PATH")}");
                return Task.FromResult((File: file, Executable: executable, EncoderIdentity: encoderIdentity,
                    Length: file.Length, LastWrite: File.GetLastWriteTimeUtc(file.SafeFileHandle)));
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }, token, discarded => discarded.File.Dispose());
        using var sourceFile = input.File;
        token.ThrowIfCancellationRequested();
        // 缓存键纳入输入文件、编码器版本和输出参数，避免复用旧配置生成的视频。
        string identity = FormattableString.Invariant(
            $"{typeof(VideoConverter).Module.ModuleVersionId}|{input.EncoderIdentity}|{input.Length}|{input.LastWrite.Ticks}|{model.ProductId}|{model.Width}|{model.Height}|{fps}|{rotation}|{layout.Fill}|{layout.OffsetX}|{layout.OffsetY}|{layout.ZoomPercent}|{source}");
        string hash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
        string directory = Path.Combine(Path.GetTempPath(), "TURZX.WinUsbDisplay", "VideoCache");
        string cached = Path.Combine(directory, hash + ".h264");
        if (await DeviceVideoCache.OpenAsync(cached, prepared: null, token) is { } existing)
            return existing;

        string pending = Path.Combine(directory, $"{Guid.NewGuid():N}{DeviceVideoCache.PartialVideoSuffix}");
        try
        {
            SetVideoStartupStage(active, "ConvertingBackgroundVideo");
            await Task.Run(() => VideoConverter.ConvertAsync(source, pending, model, fps, token, layout, rotation, input.Executable), token);
            token.ThrowIfCancellationRequested();
            return (await DeviceVideoCache.OpenAsync(cached, pending, token))!;
        }
        finally
        {
            PreviewCanvas.TryDelete(pending);
        }

    }

    /// <summary>准备并向指定设备发送视频文件。</summary>
    private async Task SendVideoAsync(DevicePlayback active, DisplayDevice device, string path, byte[] key,
        byte rotation, MediaLayout layout)
    {
        var token = active.Token;
        SetVideoStartupStage(active, "ReadingVideoFrameRate");
        byte fps = await ReadDeviceVideoFrameRateAsync(path, token);
        using var cached = await PrepareDeviceVideoAsync(active, path, device.Model, fps, layout,
            MediaFrameRotation(device.Model, rotation), token);
        FileStream stream = cached.Stream;
        SetVideoStartupStage(active, "PreparingVideoPreview");
        active.PreviewVideo = await PreparePreviewVideoAsync(stream.Name, fps, token);
        token.ThrowIfCancellationRequested();
        SetVideoStartupStage(active, SendingVideoToDisplayKey);
        SetPlaybackStatus(active, T(SendingVideoToDisplayKey));
        await Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                using var display = OpenPlaybackDevice(device, key, rotation, token, hasVideo: true);
                if (device.Model.ProductId != 0x0123)
                    display.ShowOverlay(DisplayFrameProcessor.CreateBlankOverlay(device.Model));
                display.PlayVideo(stream, fps, true, token, () => Dispatcher.Invoke(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        SetPlaybackVideo(active, fps);
                        SetPlaybackStatus(active, T("PlayingVideo"));
                    }
                }), resetPlayback: false);
                return Task.CompletedTask;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 设备停播和会话清理完成后，把正常取消交给界面更新状态。
                return Task.FromCanceled(token);
            }
        }, token);
    }

    /// <summary>取消当前播放，或停止设备端正在播放的内容。</summary>
    private async void OnStop(object sender, RoutedEventArgs e)
    {
        bool canceledResume = preferredDevicePath is { } path && pendingResumes.Remove(path);
        if (canceledResume)
        {
            ApplyDeviceSnapshot(devices, refreshChoices: true);
            Status.Text = T("ResumeOnReconnectionCanceled");
            UpdateControls();
        }
        if (SelectedNativeTheme is not null && nativeThemePlaying)
        {
            nativeThemeBusy = true;
            UpdateControls();
            try
            {
                nativeThemePlayer.Stop();
                nativeThemePlaying = false;
                SelectedDeviceChoice?.SetPlaybackLabel(T("Idle"));
                Status.Text = T("NativeThemeStopped");
            }
            finally
            {
                nativeThemeBusy = false;
                UpdateControls();
                UpdateMediaControls();
            }
            return;
        }
        if (CurrentPlayback is { } active)
        {
            if (active.Cancellation.IsCancellationRequested)
                return;
            active.Cancellation.Cancel();
            SetPlaybackStatus(active, T("Stopping"));
            if (active.StartupStageKey is not null)
                SetVideoStartupStage(active, "Stopping");
            UpdateControls();
        }
        else if (!canceledResume)
        {
            await RunDeviceControlAsync(T("StoppingPlayback"), session =>
            {
                session.StopPlayback();
                return T("PlaybackStopCommandsSent");
            });
        }
    }

    /// <summary>将裸 H.264 封装成本地播放器可读取的临时视频。</summary>
    private static async Task<string?> PreparePreviewVideoAsync(string h264, byte fps,
        CancellationToken cancellation)
    {
        string path = Path.Combine(Path.GetTempPath(), $"WinUsbDisplay-preview-{Guid.NewGuid():N}.mp4");
        try
        {
            await Task.Run(() => VideoConverter.CreatePreviewAsync(h264, path, fps, cancellation), cancellation);
            return path;
        }
        catch (OperationCanceledException)
        {
            PreviewCanvas.TryDelete(path);
            throw;
        }
        catch (Exception)
        {
            PreviewCanvas.TryDelete(path);
            return null;
        }
    }

    /// <summary>切换工作区时刷新相关面板状态。</summary>
    private void OnWorkspaceChanged(object sender, RoutedEventArgs e)
    {
        MediaPage.Visibility = MediaNav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPage.Visibility = AdvancedNav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CommandsPage.Visibility = CommandsNav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ProtocolPage.Visibility = DocsNav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (IsLoaded && !busy && MediaNav.IsChecked == true)
            Status.Text = SelectedDeviceChoice is { Device: var selected }
                ? T("OutputTargetDetails", selected.Model.Name)
                : devices.Length == 0 ? T("ConnectDisplayHint") : T("ConnectedSelectADisplay", devices.Length);

        if (DocsNav.IsChecked == true && renderedMarkdown.Length == 0)
            ShowSelectedDocumentation();
        if (IsLoaded && CommandsNav.IsChecked == true && IsDesktopRecoverySelected)
            _ = RefreshDesktopDevicesAsync();
    }

    /// <summary>亮度滑块变化时保存并安排发送最新值。</summary>
    private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!loadingQuickControls)
            SaveQuickControl(brightness: (byte)e.NewValue);
    }

    /// <summary>方向选项变化时更新配置和预览方向。</summary>
    private void OnRotationChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyRotationToTargets();
        if (!loadingQuickControls && Rotation.SelectedIndex >= 0)
            SaveQuickControl(rotation: (byte)Rotation.SelectedIndex);
    }

    /// <summary>把快捷设置的变更写入当前设备的首选项。</summary>
    private void SaveQuickControl(byte? brightness = null, byte? rotation = null)
    {
        if (busy || SelectedDevice is not { } device)
            return;

        var previous = preferences.GetScreen(device.Path);
        var current = previous with
        {
            Brightness = brightness ?? previous.Brightness,
            Rotation = rotation ?? previous.Rotation
        };
        if (previous == current)
            return;

        try
        {
            preferences.UpdateScreen(device.Path, latest => latest with
            {
                Brightness = brightness ?? latest.Brightness,
                Rotation = rotation ?? latest.Rotation
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text = T("CouldNotSaveSettings", DescribeUiError(ex));
            return;
        }

        if (previous.Brightness == current.Brightness)
        {
            Status.Text = T("OrientationSaved");
            return;
        }

        pendingBrightness[device.Path] = current.Brightness;
        quickControlTimer.Stop();
        quickControlTimer.Start();
        Status.Text = T("DisplaySettingsPending");
    }

    // 合并滑块产生的亮度更新，等对应设备的播放会话释放后再发送。
    /// <summary>为有待更新且未被播放占用的设备发送亮度。</summary>
    private async Task SendPendingSettingsAsync()
    {
        if (closing || controlBusy || confirmation is not null)
            return;

        var device = devices.FirstOrDefault(candidate => pendingBrightness.ContainsKey(candidate.Path)
            && !devicePlaybacks.ContainsKey(candidate.Path));
        if (device is null)
        {
            if (pendingBrightness.Count == 0)
                quickControlTimer.Stop();
            return;
        }

        pendingBrightness.Remove(device.Path, out byte brightness);
        if (pendingBrightness.Count == 0)
            quickControlTimer.Stop();

        await RunDeviceControlAsync(device, T("SendingDisplaySettings"), session =>
        {
            session.SetBrightness(brightness);
            return T("DisplaySettingsSavedAndSent");
        }, failurePrefix: T("DisplaySettingsSendFailed"), onFailure: error =>
        {
            if (!IsDeviceDisconnected(error))
                return;

            pendingBrightness.TryAdd(device.Path, brightness);
            refreshAfterOperation = true;
            quickControlTimer.Start();
        });
    }

    /// <summary>对当前所选设备执行同步控制命令。</summary>
    private async Task RunDeviceControlAsync(string progress, Func<DisplaySession, string> control,
        Func<string, string>? complete = null, string? failurePrefix = null, Action<Exception>? onFailure = null,
        Func<CancellationToken, Task>? prepare = null)
    {
        if (busy || SelectedDevice is not { } device)
            return;

        await RunDeviceControlAsync(device, progress, control, complete, failurePrefix, onFailure, prepare);
    }

    /// <summary>对指定设备执行控制操作，并回到界面更新状态。</summary>
    private Task RunDeviceControlAsync(DisplayDevice device, string progress, Func<DisplaySession, string> control,
        Func<string, string>? complete = null, string? failurePrefix = null, Action<Exception>? onFailure = null,
        Func<CancellationToken, Task>? prepare = null) =>
        RunOperationAsync(async () =>
        {
            CancellationToken cancellation = deviceControlShutdown.Token;
            if (prepare is not null)
            {
                // 打开网络文件可能阻塞。关闭时只放弃准备任务，设备命令由后续步骤单独启动。
                Task preparation = Task.Run(() => prepare(cancellation), cancellation);
                try
                {
                    await preparation.WaitAsync(cancellation);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    _ = ObservePreparationAsync(preparation);
                    throw;
                }
            }

            string result = await Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                byte[] key = CommandKeyStore.Read();
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    using var session = device.Open(key);
                    cancellation.ThrowIfCancellationRequested();
                    return control(session);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }, cancellation);
            Status.Text = complete is null ? result : complete(result);

            static async Task ObservePreparationAsync(Task preparation) =>
                await preparation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }, progress, failurePrefix, onFailure);

    /// <summary>打开设备并为指定方向的播放初始化会话。</summary>
    private static DisplaySession OpenPlaybackDevice(DisplayDevice device, byte[] key, byte rotation, CancellationToken token,
        bool hasVideo = false, bool clearLegacyOverlay = true)
    {
        var session = OpenOutputDevice(device, key, token);
        try
        {
            token.ThrowIfCancellationRequested();
            session.StopPlayback();
            token.ThrowIfCancellationRequested();
            // 此面板的前景已旋转到设备原生画面方向。
            byte firmwareRotation = FirmwareRotation(device.Model, rotation);
            session.SetRotation(firmwareRotation);
            if (token.WaitHandle.WaitOne(50))
                token.ThrowIfCancellationRequested();
            if (device.Model.ProductId == 0x0046)
                session.SynchronizeClock();
            session.ShowDefaultClock(false);
            token.ThrowIfCancellationRequested();
            // 0123 的 PNG 清层只写入，不应等待不存在的应答。旧 PNG 图层会遮住后续 JPEG。
            if (clearLegacyOverlay && device.Model.ProductId == 0x0123)
                session.ClearOverlayWithoutResponse(DisplayFrameProcessor.CreateBlankOverlay(device.Model), token);
            // Studio 发送普通 H.264 数据块，省略旧版同步启动时间戳。
            if (hasVideo && (device.Model.ProductId is 0x0046 or 0x0055))
                session.SetVideoClockSync(false);
            token.ThrowIfCancellationRequested();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>打开设备输出会话并检查取消状态。</summary>
    private static DisplaySession OpenOutputDevice(DisplayDevice device, byte[] key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            return device.Open(key);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
        {
            throw UiError(new IOException("Could not open the display.", ex), "ErrorOpenDisplay");
        }
    }

    /// <summary>按当前语言生成设备或媒体异常的界面文案。</summary>
    private static string DescribeUiError(Exception error, bool includeFullDetails = false)
    {
        if (error is AggregateException { InnerExceptions.Count: > 0 } aggregate)
        {
            if (includeFullDetails)
                return string.Join(Environment.NewLine, aggregate.InnerExceptions.Select(cause => DescribeUiError(cause, true)));
            return DescribeUiError(aggregate.InnerExceptions[0]);
        }
        if (error is PlaybackStopException { InnerException: { } stopFailure })
            return T("ErrorPlaybackStopUnconfirmed", DescribeUiError(stopFailure, includeFullDetails));
        if (StudioLanguage.GetUiErrorKey(error) is { } key)
            return error.InnerException is { } inner ? T(key, DescribeUiError(inner, includeFullDetails)) : T(key);
        if (error is FfmpegNotFoundException)
            return T("ErrorFfmpegNotFound");
        if (error is FfmpegProcessException ffmpeg)
        {
            string summary = T("ErrorFfmpegExit", ffmpeg.ExitCode);
            string details = ffmpeg.Diagnostics;
            if (details.Length == 0)
                return summary;
            if (includeFullDetails)
                return summary + Environment.NewLine + details[..Math.Min(details.Length, 4096)];
            int lineEnd = details.AsSpan().IndexOfAny('\r', '\n');
            int length = lineEnd < 0 ? details.Length : lineEnd;
            return summary + " " + details[..Math.Min(length, 160)] + (length > 160 ? "…" : "");
        }
        if (error is DeviceResponseException response)
            return response.Failure switch
            {
                DeviceResponseFailure.DirectoryCreationFailed => T("ErrorDeviceDirectoryCreationFailed"),
                DeviceResponseFailure.UnexpectedReply => T("ErrorUnexpectedDeviceReply"),
                _ => T("ErrorDeviceReturnedNoData")
            };
        if (error is Win32Exception native)
            return native.NativeErrorCode switch
            {
                32 => T("ErrorDisplayInUse"),
                5 => T("ErrorDisplayAccessDenied"),
                6 or 1167 => T("ErrorDeviceDisconnected"),
                121 or 1460 when native.Message.StartsWith("WinUsb_WritePipe:", StringComparison.Ordinal)
                    => T("ErrorUsbWriteTimeout"),
                121 or 1460 => T("ErrorDeviceTimeout"),
                _ => T("ErrorWindowsDeviceOperation", native.NativeErrorCode)
            };

        if (StudioLanguage.Current == StudioLanguage.EnglishLanguage)
            return error.Message;

        System.Diagnostics.Trace.TraceError(error.ToString());
        return error switch
        {
            FileNotFoundException or DirectoryNotFoundException => T("ErrorRequiredFileMissing"),
            UnauthorizedAccessException => T("ErrorAccessDenied"),
            TimeoutException => T("ErrorDeviceTimeout"),
            FormatException => T("ErrorInvalidInput"),
            IOException => T("ErrorIoOperation"),
            _ => T("ErrorOperationFailed", error.GetType().Name)
        };
    }

    /// <summary>运行一项界面操作并统一处理忙碌状态和失败。</summary>
    private async Task RunOperationAsync(Func<Task> operation, string? progress = null, string? failurePrefix = null, Action<Exception>? onFailure = null)
    {
        if (closing || controlBusy)
            return;
        controlBusy = true;
        Status.Text = progress ?? T("Stopping");
        UpdateControls();
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (deviceControlShutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            string message = DescribeUiError(ex);
            string? stage = failurePrefix;
            Status.Text = stage is null ? message : $"{stage} {message}";
            onFailure?.Invoke(ex);
        }
        finally
        {
            controlBusy = false;
            bool refreshDevices = refreshAfterOperation;
            refreshAfterOperation = false;
            if (refreshDevices && !closing)
                await RefreshDevicesAsync();
            UpdateControls();
            CloseWhenIdle();
        }
    }

    /// <summary>按工作区、设备和运行状态刷新界面控件。</summary>
    private void UpdateControls()
    {
        Browse.IsEnabled = !busy;
        UpdateMediaControls();
        WorkspaceDevice.IsEnabled = !closing && !controlBusy;
        LiveSettings.IsEnabled = !busy;
        UpdateCommandControls();
        bool canControl = !busy && SelectedDevice is not null;
        LanguageSelector.IsEnabled = !closing && !controlBusy;
        LiveSettings.IsEnabled = !busy && SelectedNativeTheme is null;
        NativeThemeSettings.IsEnabled = !busy;
        BrightnessValue.IsEnabled = Rotation.IsEnabled = canControl;
        UpdateAdvancedControls();
    }

    /// <summary>显示确认提示并等待用户选择。</summary>
    private async Task<bool> ConfirmAsync(string title, string message)
    {
        if (confirmation is not null || closing)
            return false;

        var previousFocus = Keyboard.FocusedElement;
        confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfirmationTitle.Text = title;
        ConfirmationMessage.Text = message;
        ConfirmationOverlay.Visibility = Visibility.Visible;
        ConfirmationCancel.Focus();
        try
        {
            return await confirmation.Task && !closing;
        }
        finally
        {
            confirmation = null;
            ConfirmationOverlay.Visibility = Visibility.Collapsed;
            if (!closing && previousFocus is not null)
                Keyboard.Focus(previousFocus);
        }
    }

    /// <summary>将确认按钮的选择传给正在等待的操作。</summary>
    private void OnConfirmationChoice(object sender, RoutedEventArgs e) =>
        confirmation?.TrySetResult(ReferenceEquals(sender, ConfirmationAccept));

    /// <summary>处理确认弹层中的键盘操作。</summary>
    private void OnConfirmationKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            confirmation?.TrySetResult(false);
            e.Handled = true;
        }
    }

    /// <summary>指示是否有操作仍在使用设备、浏览器或临时媒体。</summary>
    private bool HasPendingWindowWork => controlBusy || devicePlaybacks.Count > 0 || localPreviewTasks.Count > 0;

    /// <summary>工作任务收尾后，在当前界面回调返回之后重新请求关闭。</summary>
    private void CloseWhenIdle()
    {
        if (closing && !HasPendingWindowWork && pendingClose is null)
            pendingClose = Dispatcher.InvokeAsync(Close);
    }

    /// <summary>取消仍在运行的操作，等设备和本地预览释放资源后关闭。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!closing)
        {
            closing = true;
            deviceTimer.Stop();
            quickControlTimer.Stop();
            confirmation?.TrySetResult(false);
            deviceControlShutdown.Cancel();
            nativeThemePlayer.Stop();
            nativeThemePlaying = false;
            StopLocalPreview();
            backgroundFrameRateProbe?.Cancel();
            foreach (var active in devicePlaybacks.Values.ToArray())
                active.Cancellation.Cancel();
            UpdateControls();
        }

        // 浏览器和 FFmpeg 的异步清理需要界面调度器继续运行。
        e.Cancel |= HasPendingWindowWork;
        base.OnClosing(e);
        if (!e.Cancel && mediaPreferencesRestored)
            SaveMediaPreferences();
    }
}
