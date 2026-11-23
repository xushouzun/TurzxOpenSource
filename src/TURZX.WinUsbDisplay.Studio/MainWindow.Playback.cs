using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using static TURZX.WinUsbDisplay.Studio.StudioLanguage;

namespace TURZX.WinUsbDisplay.Studio;

// 协调多台设备的播放任务、重连续播及预览状态。
public partial class MainWindow
{
    /// <summary>记录单台设备的播放状态，以便切回该设备时恢复预览。</summary>
    internal sealed class DevicePlayback
    {
        /// <summary>正在播放的目标设备。</summary>
        internal required DisplayDevice Device { get; init; }
        /// <summary>控制当前播放任务生命周期的取消源。</summary>
        internal CancellationTokenSource Cancellation { get; } = new();
        /// <summary>传给当前播放各环节的取消令牌。</summary>
        internal CancellationToken Token => Cancellation.Token;
        /// <summary>指示播放期间是否已检测到设备断开。</summary>
        internal bool Disconnected { get; set; }
        /// <summary>指示前景帧是否已发送，或背景视频是否已开始播放。</summary>
        internal bool HasOutput => Frame is not null || VideoFps is not null;
        /// <summary>允许重连后重新启动播放的委托。</summary>
        internal Func<DevicePlayback, Task>? Resume { get; init; }
        /// <summary>最近一次播放状态消息。</summary>
        internal string? StatusText { get; set; }
        /// <summary>视频首帧前显示的启动阶段。</summary>
        internal string? StartupStageKey { get; set; }
        /// <summary>播放任务持有的本地视频预览路径。</summary>
        internal string? PreviewVideo { get; set; }
        /// <summary>最近一次发送到设备的已编码前景帧。</summary>
        internal byte[]? Frame { get; set; }
        /// <summary>最近一次确认帧对应的预览位图。</summary>
        internal BitmapSource? FramePreview { get; set; }
        /// <summary>指示已确认的前景帧是否按覆盖层显示。</summary>
        internal bool Overlay { get; set; }
        /// <summary>当前视频输出所配置的播放帧率。</summary>
        internal byte? VideoFps { get; set; }
        /// <summary>根据设备确认的前景帧计算的实际送屏帧率。</summary>
        internal double? SentFps { get; set; }
        /// <summary>指示前景是否为图片轮播。</summary>
        internal bool IsImageSlideshow { get; set; }
        /// <summary>实测的实时编码器输入帧率。</summary>
        internal double? EncoderInputFps { get; set; }
    }

    /// <summary>按设备接口路径跟踪正在运行的播放任务。</summary>
    private readonly Dictionary<string, DevicePlayback> devicePlaybacks = new(StringComparer.OrdinalIgnoreCase);
    // 保留原播放委托及重试进度，设备重新枚举后用新接口对象启动播放。
    /// <summary>保存待重连设备、原播放委托及重试进度。</summary>
    /// <param name="Device">原播放任务对应的设备。</param>
    /// <param name="Resume">在新播放任务中重新执行的操作。</param>
    private sealed record PendingResume(DisplayDevice Device, Func<DevicePlayback, Task> Resume)
    {
        /// <summary>已经开始的重连续播尝试次数。</summary>
        internal int Attempts { get; set; }
        /// <summary>允许下一次续播尝试的 TickCount64 时间。</summary>
        internal long NextAttempt { get; set; }
    }

    /// <summary>单台设备重连后允许的最大续播尝试次数。</summary>
    private const int MaxResumeAttempts = 5;
    /// <summary>按接口路径记录需要恢复的播放任务。</summary>
    private readonly Dictionary<string, PendingResume> pendingResumes = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>保存设备最近一次需要呈现给用户的播放异常。</summary>
    private readonly Dictionary<string, Exception> playbackErrors = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>指示首选设备是否仍有待恢复的播放。</summary>
    private bool HasPendingResume => preferredDevicePath is { } path && pendingResumes.ContainsKey(path);
    /// <summary>获取当前所选设备的播放任务。</summary>
    private DevicePlayback? CurrentPlayback => SelectedDevice is { } device
        ? devicePlaybacks.GetValueOrDefault(device.Path) : null;
    /// <summary>指示一个设备控制操作仍在执行。</summary>
    private bool controlBusy;
    /// <summary>指示当前界面是否应禁止普通操作。</summary>
    private bool busy => closing || controlBusy || nativeThemeBusy || CurrentPlayback is not null;

    // 每个设备路径只允许一个播放任务，切换所选设备后这一限制仍然有效。
    /// <summary>启动指定设备的播放，并在结束时恢复预览和释放资源。</summary>
    internal async Task RunPlaybackAsync(DisplayDevice device, Func<DevicePlayback, Task> operation, string progress,
        bool resumeOnReconnect = false)
    {
        if (closing)
            return;
        var active = new DevicePlayback { Device = device, Resume = resumeOnReconnect ? operation : null };
        if (!devicePlaybacks.TryAdd(device.Path, active))
        {
            active.Cancellation.Dispose();
            return;
        }

        playbackErrors.Remove(device.Path);
        if (ReferenceEquals(CurrentPlayback, active))
            StopLocalPreview();
        SetPlaybackStatus(active, progress);
        UpdateControls();
        UpdatePlaybackIndicators();
        try
        {
            // 先让界面呈现启动状态，再执行可能包含同步设备初始化的播放委托。
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            active.Token.ThrowIfCancellationRequested();
            await operation(active);
            active.Token.ThrowIfCancellationRequested();
            pendingResumes.Remove(device.Path);
        }
        catch (Exception error)
        {
            bool disconnected = IsDeviceDisconnected(error);
            if (!disconnected && !active.Token.IsCancellationRequested && active.HasOutput && active.Resume is not null
                && IsWinUsbTransferFailure(error))
                disconnected = await ConfirmPlaybackDisconnectAsync(device.Path, active.Token);

            if (active.Cancellation.IsCancellationRequested && (error is not PlaybackStopException || active.Disconnected))
                SetPlaybackStatus(active, T(active.Disconnected && pendingResumes.ContainsKey(device.Path)
                    ? "DisplayDisconnectedWaitingToResume" : "OutputStopped"));
            else if (pendingResumes.TryGetValue(device.Path, out var pending)
                && pending.Attempts < MaxResumeAttempts && IsTemporaryResumeFailure(error))
                SetPlaybackStatus(active, T("DisplayDisconnectedWaitingToResume"));
            else if (disconnected && QueuePlaybackResume(device, active))
                deviceTimer.Start();
            else
            {
                pendingResumes.Remove(device.Path);
                playbackErrors[device.Path] = error;
                SetPlaybackStatus(active, error is PlaybackStopException
                    ? DescribeUiError(error) : $"{active.StatusText} {DescribeUiError(error)}");
            }
        }
        finally
        {
            // 先记录是否仍选中该任务，再移除它，否则当前播放查询会立刻失去该任务。
            bool selected = ReferenceEquals(CurrentPlayback, active);
            devicePlaybacks.Remove(device.Path);
            if (selected)
            {
                if (active.PreviewVideo is not null)
                    HomeCanvas.StopVideo();
                if (!closing)
                {
                    ApplyVideoStartupStage(null);
                    RestartLocalPreview(keepForeground: active.Frame is not null);
                }
            }
            // 预览文件由播放任务持有，切换设备时可以继续复用。
            if (active.PreviewVideo is { } preview)
                PreviewCanvas.TryDelete(preview);

            active.Cancellation.Dispose();
            if (!closing)
            {
                UpdateControls();
                UpdatePlaybackIndicators();
            }
            if (pendingResumes.ContainsKey(device.Path) && !closing)
                deviceTimer.Start();
            CloseWhenIdle();
        }
    }

    /// <summary>保存设备播放状态，并在设备仍被选中时显示。</summary>
    private void SetPlaybackStatus(DevicePlayback active, string message)
    {
        active.StatusText = message;
        if (ReferenceEquals(CurrentPlayback, active))
            Status.Text = message;
    }

    /// <summary>更新设备的启动阶段和相应界面提示。</summary>
    private void SetVideoStartupStage(DevicePlayback active, string? stageKey)
    {
        active.StartupStageKey = stageKey;
        if (ReferenceEquals(CurrentPlayback, active))
            ApplyVideoStartupStage(stageKey);
    }

    /// <summary>切换到所选设备的最近播放画面，或启动本地预览。</summary>
    private void ShowSelectedPlayback()
    {
        var active = CurrentPlayback;
        ApplyVideoStartupStage(active?.StartupStageKey);
        if (active is null)
        {
            RestartLocalPreview();
            return;
        }

        StopLocalPreview();
        if (active.VideoFps is not null && active.PreviewVideo is { } preview)
            HomeCanvas.ShowVideo(preview, loop: true, ownsFile: false, prepared: true, background: true);
        if (active.FramePreview is { } bitmap)
            HomeCanvas.ShowFrame(bitmap, active.Overlay, prepared: true);
        else if (active.Frame is { } frame)
            HomeCanvas.ShowFrame(frame, active.Overlay, prepared: true);
        if (active.StatusText is { } message)
            Status.Text = message;
    }

    /// <summary>记录视频已开始播放并更新预览和帧率指标。</summary>
    private void SetPlaybackVideo(DevicePlayback active, byte fps)
    {
        if (active.Token.IsCancellationRequested)
            return;

        pendingResumes.Remove(active.Device.Path);
        active.VideoFps = fps;
        UpdatePlaybackIndicators();
        if (ReferenceEquals(CurrentPlayback, active) && active.PreviewVideo is { } preview)
            HomeCanvas.ShowVideo(preview, loop: true, ownsFile: false, prepared: true, background: true);
        SetVideoStartupStage(active, null);
        UpdateFrameRateSummary();
    }

    // 每台设备都保留最近确认的帧，只有当前选中的设备更新画布。
    /// <summary>记录设备确认的前景帧，并按所选设备决定是否更新预览。</summary>
    private void SetPlaybackFrame(DevicePlayback active, byte[] frame, bool overlay, BitmapSource? preview = null)
    {
        if (active.Token.IsCancellationRequested)
            return;
        bool first = !active.HasOutput;
        active.Frame = frame;
        active.FramePreview = preview;
        active.Overlay = overlay;
        if (first)
        {
            pendingResumes.Remove(active.Device.Path);
            UpdatePlaybackIndicators();
        }
        if (ReferenceEquals(CurrentPlayback, active))
        {
            if (preview is null)
                HomeCanvas.ShowFrame(frame, overlay, prepared: true);
            else
                HomeCanvas.ShowFrame(preview, overlay, prepared: true);
        }
    }

    /// <summary>为已断开的设备登记续播并取消当前任务。</summary>
    private void CancelDisconnectedPlaybacks(DisplayDevice[] connected)
    {
        foreach (var (path, active) in devicePlaybacks.ToArray())
        {
            if (active.Disconnected || connected.Any(device => string.Equals(device.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            QueuePlaybackResume(active.Device, active);
            active.Cancellation.Cancel();
        }
    }

    // 只有已输出过画面的任务才登记重连续播，避免重复启动尚未成功的播放。
    /// <summary>将曾成功输出的播放登记为待重连续播。</summary>
    private bool QueuePlaybackResume(DisplayDevice device, DevicePlayback active)
    {
        active.Disconnected = true;
        if (closing || active.Cancellation.IsCancellationRequested || !active.HasOutput || active.Resume is null)
            return false;

        pendingResumes[device.Path] = new PendingResume(device, active.Resume);
        UpdatePlaybackIndicators();
        SetPlaybackStatus(active, T("DisplayDisconnectedWaitingToResume"));
        return true;
    }

    /// <summary>识别设备断连对应的原生错误，包括内部异常。</summary>
    private static bool IsDeviceDisconnected(Exception error) =>
        MatchesPlaybackFailure(error, static cause => cause is System.ComponentModel.Win32Exception { NativeErrorCode: 6 or 1167 });

    /// <summary>WinUSB 在拔线时也可能返回通用错误 31，需用当前枚举结果确认。</summary>
    private static bool IsWinUsbTransferFailure(Exception error) =>
        MatchesPlaybackFailure(error, static cause =>
            cause is System.ComponentModel.Win32Exception { NativeErrorCode: 31 } native
            && (native.Message.StartsWith("WinUsb_ReadPipe:", StringComparison.Ordinal)
                || native.Message.StartsWith("WinUsb_WritePipe:", StringComparison.Ordinal)));

    private async Task<bool> ConfirmPlaybackDisconnectAsync(string path, CancellationToken cancellation)
    {
        try
        {
            // USB 传输失败后，设备接口可能要过片刻才从 Windows 枚举结果中消失。
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(deviceTimer.Interval, cancellation);
                var found = await Task.Run(discoverDisplays, cancellation);
                if (!found.Any(device => string.Equals(device.Path, path, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        catch (Exception error) when (error is OperationCanceledException or System.ComponentModel.Win32Exception
            or FormatException)
        {
            // 枚举结果不可用时，继续保留原播放故障供界面诊断。
        }
        return false;
    }

    /// <summary>判断续播故障是否属于可以重试的暂时错误。</summary>
    private static bool IsTemporaryResumeFailure(Exception error) =>
        IsWinUsbTransferFailure(error) || MatchesPlaybackFailure(error, static cause =>
            cause is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 or 6 or 21 or 32 or 121 or 1167 or 1460 }
                or DeviceResponseException { Failure: DeviceResponseFailure.NoData });

    private static bool MatchesPlaybackFailure(Exception error, Func<Exception, bool> matches) =>
        matches(error) || (error is AggregateException aggregate
            ? aggregate.InnerExceptions.Any(cause => MatchesPlaybackFailure(cause, matches))
            : error.InnerException is { } cause && MatchesPlaybackFailure(cause, matches));

    /// <summary>为已重新枚举且达到重试时间的设备重新启动播放。</summary>
    private void ResumeConnectedPlaybacks(DisplayDevice[] connected)
    {
        long now = Environment.TickCount64;
        foreach (var device in connected)
        {
            if (closing || devicePlaybacks.ContainsKey(device.Path) || !pendingResumes.TryGetValue(device.Path, out var resume)
                || now < resume.NextAttempt)
                continue;

            // 启动期间拔出设备时，先取消本次播放尝试，再进入错误处理。
            if (resume.Attempts >= MaxResumeAttempts)
            {
                pendingResumes.Remove(device.Path);
                playbackErrors[device.Path] = new TimeoutException(T("PlaybackResumeTimedOut"));
                continue;
            }

            resume.Attempts++;
            resume.NextAttempt = now + 1000;
            _ = RunPlaybackAsync(device, resume.Resume, T("DisplayReconnectedResuming"), resumeOnReconnect: true);
        }
        if (!closing && connected.Any(device => pendingResumes.ContainsKey(device.Path) && !devicePlaybacks.ContainsKey(device.Path)))
            deviceTimer.Start();
        UpdateMediaControls();
        UpdatePlaybackIndicators();
    }
}
