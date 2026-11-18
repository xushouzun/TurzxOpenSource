using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using static TURZX.WinUsbDisplay.Studio.StudioLanguage;

namespace TURZX.WinUsbDisplay.Studio;

// 保存并恢复各设备使用的媒体来源和布局。
public partial class MainWindow
{
    /// <summary>记录初始媒体首选项是否已经恢复。</summary>
    private bool mediaPreferencesRestored;
    /// <summary>禁止恢复控件快照时触发预览或保存。</summary>
    private bool restoringMediaPreferences;
    /// <summary>当前媒体控件所关联的设备接口路径。</summary>
    private string? mediaDevicePath;
    /// <summary>用于重新选择捕捉窗口的窗口标题。</summary>
    private string? preferredCaptureWindowTitle;
    private CancellationTokenSource? foregroundBrowse;

    /// <summary>启动时恢复此前选中的设备和媒体配置。</summary>
    private void RestoreMediaPreferences()
    {
        mediaPreferencesRestored = true;
        if (preferences.Media is { } saved)
        {
            preferredDevicePath = mediaDevicePath = saved.DevicePath;
            ApplyMediaPreferences(saved);
            OnLocalPreviewInputChanged(LivePath, new RoutedEventArgs());
            OnBackgroundPreviewInputChanged(BackgroundPath, new RoutedEventArgs());
        }
    }

    /// <summary>保存原设备设置并切换到目标设备的配置。</summary>
    private void SwitchDeviceMedia(string? devicePath)
    {
        // 所选设备断开后，继续显示尚未保存的设置。
        if (devicePath is null || string.Equals(mediaDevicePath, devicePath, StringComparison.OrdinalIgnoreCase))
            return;

        foregroundBrowse?.Cancel();
        if (mediaDevicePath is null && preferences.GetDeviceMedia(devicePath) is null)
        {
            mediaDevicePath = devicePath;
            return;
        }

        SaveMediaPreferences();
        mediaDevicePath = devicePath;
        StopLocalPreview();
        ApplyMediaPreferences(preferences.GetDeviceMedia(devicePath) ?? new MediaPreferences());
        _ = TrackLocalPreviewAsync(UpdateBackgroundFrameRateAsync());
    }

    /// <summary>将媒体配置应用到控件和内部状态。</summary>
    private void ApplyMediaPreferences(MediaPreferences saved)
    {
        // 设置控件会触发事件，恢复期间暂停预览更新和设置回写。
        restoringMediaPreferences = true;
        try
        {
            mediaThumbnail = null;
            captureRegion = null;
            media = saved.ImagePath;

            htmlSourcePath = IsBuiltInScene(saved.BuiltInScene)
                ? Path.Combine(ScenesDirectory, saved.BuiltInScene!)
                : saved.HtmlPath ?? "";
            slideshowPaths = saved.SlideshowPaths?.Where(path => !string.IsNullOrWhiteSpace(path) && IsImage(path)).ToArray() ?? [];
            SlideshowInterval.Text = saved.SlideshowInterval ?? "5";
            LiveUrl.Text = saved.WebUrl ?? "";
            CaptureRate.Text = saved.CaptureRate ?? DefaultCaptureRateText;
            BackgroundPath.Text = saved.BackgroundPath ?? "";
            foregroundLayout = saved.ForegroundLayout ?? MediaLayout.Default;
            backgroundLayout = saved.BackgroundLayout ?? MediaLayout.Default;
            LiveSource.SelectedIndex = saved.SourceMode is 0 or 1 or MediaSourceMode or WebSourceMode or SlideshowSourceMode ? saved.SourceMode : MediaSourceMode;
            LivePath.Text = LiveSource.SelectedIndex == 0 ? htmlSourcePath : "";
            preferredCaptureWindowTitle = saved.WindowTitle;
            LiveWindow.SelectedIndex = -1;
            if (LiveSource.SelectedIndex == 1 || preferredCaptureWindowTitle is { Length: > 0 })
                OnRefreshWindows(this, new RoutedEventArgs());
            // 先恢复捕捉源，再把归一化裁剪区域绑定到该来源。
            if (saved.CaptureBounds is [var x, var y, var width, var height] && x is >= 0 and < 1 && y is >= 0 and < 1
                && width > 0 && height > 0 && x + width <= 1.000000001 && y + height <= 1.000000001
                && CaptureRegionSource is { } source)
                captureRegion = (source, new Rect(x, y, Math.Min(width, 1 - x), Math.Min(height, 1 - y)));

            var scene = HtmlScenes.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is string file
                && string.Equals(LivePath.Text, Path.Combine(ScenesDirectory, file), StringComparison.OrdinalIgnoreCase));
            HtmlScenes.SelectedItem = scene ?? HtmlScenes.Items[0];
            LayoutLayer.SelectedIndex = saved.LayoutLayer == 1 ? 1 : 0;
            ApplyLayoutToTargets();
        }
        finally
        {
            restoringMediaPreferences = false;
        }

    }

    /// <summary>为当前设备记录媒体来源、布局和裁剪数据。</summary>
    private void SaveMediaPreferences()
    {
        string html = (LiveSource.SelectedIndex == 0 ? LivePath.Text : htmlSourcePath).Trim();
        // 内置场景只保存文件名，恢复时用当前安装目录解析。
        string scene = Path.GetFileName(html);
        bool builtIn = IsBuiltInScene(scene)
            && string.Equals(html, Path.Combine(ScenesDirectory, scene), StringComparison.OrdinalIgnoreCase);

        try
        {
            preferences.SetMedia(new MediaPreferences
            {
                SourceMode = LiveSource.SelectedIndex,
                DevicePath = mediaDevicePath,
                ImagePath = media,
                HtmlPath = builtIn ? null : html,
                SlideshowPaths = slideshowPaths,
                SlideshowInterval = SlideshowInterval.Text.Trim(),
                WebUrl = LiveUrl.Text.Trim(),
                BuiltInScene = builtIn ? scene : null,
                WindowTitle = preferredCaptureWindowTitle,
                BackgroundPath = BackgroundPath.Text.Trim(),
                CaptureRate = CaptureRate.Text.Trim(),
                ForegroundLayout = foregroundLayout,
                BackgroundLayout = backgroundLayout,
                CaptureBounds = CurrentCaptureRegion is { } region ? [region.X, region.Y, region.Width, region.Height] : null,
                LayoutLayer = LayoutLayer.SelectedIndex
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string message = T("CouldNotSaveSettings", DescribeUiError(ex));
            Status.Text = message;
            System.Diagnostics.Trace.TraceWarning(message);
        }
    }

    /// <summary>判断文件名是否属于随 Studio 分发的内置场景。</summary>
    private static bool IsBuiltInScene(string? file) => file is "default.html" or "clock.html" or "stars.html" or "pong.html" or "race.html" or "fireworks.html";

    /// <summary>以有限解码宽度读取图片缩略图。</summary>
    private static Task<BitmapImage> LoadForegroundThumbnailAsync(string path, CancellationToken cancellation) =>
        ReadLocalMediaAsync(() => Task.FromResult(LoadForegroundThumbnail(path, cancellation)), cancellation);

    private static async Task<T> ReadLocalMediaAsync<T>(Func<Task<T>> operation, CancellationToken cancellation,
        Action<T>? discardResult = null)
    {
        Task<T> load = Task.Run(operation, cancellation);
        try
        {
            return await load.WaitAsync(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 网络文件打开可能无法中断。取消等待后仍观察后台任务的结果。
            _ = ObserveLoadAsync();
            throw;
        }

        async Task ObserveLoadAsync()
        {
            await ((Task)load).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (load.IsCompletedSuccessfully)
                discardResult?.Invoke(load.Result);
        }
    }

    private static BitmapImage LoadForegroundThumbnail(string path, CancellationToken cancellation)
    {
        using var stream = File.OpenRead(path);
        cancellation.ThrowIfCancellationRequested();
        var preview = new BitmapImage();
        preview.BeginInit();
        // 在关闭文件流前完成解码，避免缩略图继续占用源文件。
        preview.CacheOption = BitmapCacheOption.OnLoad;
        preview.DecodePixelWidth = 1200;
        preview.StreamSource = stream;
        preview.EndInit();
        preview.Freeze();
        return preview;
    }
}
