using System.IO;
using System.Windows;
using System.Windows.Controls;
using static TURZX.WinUsbDisplay.Studio.StudioLanguage;

namespace TURZX.WinUsbDisplay.Studio;

// 管理媒体来源、帧率和清除操作的界面状态。
public partial class MainWindow
{
    /// <summary>未指定取帧速率时使用的默认帧率。</summary>
    private const byte DefaultCaptureFps = 20;
    /// <summary>用于填充帧率输入框的默认文本。</summary>
    public static string DefaultCaptureRateText => DefaultCaptureFps.ToString();

    /// <summary>当前轮播使用的有序图片路径。</summary>
    private string[] slideshowPaths = [];
    /// <summary>从源文件探测到的可选背景视频帧率。</summary>
    private double? sourceBackgroundFps;

    /// <summary>显示捕捉、编码、发送及背景播放的帧率信息。</summary>
    private void UpdateFrameRateSummary()
    {
        if (BackgroundFrameRateSummary is null)
            return;

        var active = CurrentPlayback;
        string? backgroundRate = null;
        if (active is null && sourceBackgroundFps is double source)
            backgroundRate = T("SourceVideoFPS", source);
        else if (active?.VideoFps is byte configured)
            backgroundRate = T("DeviceBackgroundRate", configured);

        BackgroundFrameRateSummary.Text = backgroundRate ?? "";
        BackgroundFrameRateSummary.Visibility = backgroundRate is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>根据来源、设备和播放状态更新媒体相关控件。</summary>
    private void UpdateMediaControls()
    {
        if (OutputHint is null || Send is null)
            return;

        if (SelectedNativeTheme is not null)
        {
            bool ready = File.Exists(nativeThemePath);
            Send.IsEnabled = !busy && !selectingCaptureRegion && ready && !nativeThemePlaying;
            if (Stop is not null)
                Stop.IsEnabled = !closing && nativeThemePlaying;
            OutputHint.Text = ready ? string.Empty : T("SelectNativeTheme");
            OutputHint.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        int mode = LiveSource.SelectedIndex;
        string background = BackgroundPath.Text.Trim();
        bool hasBackground = background.Length > 0;
        bool backgroundVideo = IsVideo(background);
        bool hasForeground = HasForegroundSource();
        bool invalidCaptureRate = mode is 0 or 1 or WebSourceMode && hasForeground && !ValidRate(CaptureRate.Text);

        ForegroundImagePath.Text = media ?? "";
        ClearHtmlSource.Visibility = hasForeground && mode == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearWebSource.Visibility = hasForeground && mode == WebSourceMode ? Visibility.Visible : Visibility.Collapsed;
        ClearCaptureWindow.Visibility = mode == 1 && preferredCaptureWindowTitle is { Length: > 0 }
            ? Visibility.Visible : Visibility.Collapsed;
        ClearForegroundImage.Visibility = hasForeground && mode == MediaSourceMode ? Visibility.Visible : Visibility.Collapsed;
        SlideshowSelection.Text = slideshowPaths.Length == 0 ? "" : T("ImagesSelected", slideshowPaths.Length);
        SlideshowSelection.ToolTip = string.Join(Environment.NewLine, slideshowPaths.Select(Path.GetFileName));
        ClearSlideshow.Visibility = mode == SlideshowSourceMode && hasForeground ? Visibility.Visible : Visibility.Collapsed;
        ClearBackground.Visibility = hasBackground ? Visibility.Visible : Visibility.Collapsed;
        UpdateCaptureRegionControls();
        UpdateLayoutAvailability();
        UpdateFrameRateSummary();

        string? hint = null;
        if (SelectedDevice is null)
            hint = T("SelectTargetDisplay");
        else if (!hasForeground && !hasBackground)
            hint = T(mode switch
            {
                0 => "SelectHtmlSource",
                1 => "SelectCaptureWindow",
                SlideshowSourceMode => "ChooseSlideshowImages",
                WebSourceMode => "InvalidWebUrl",
                _ => "SelectForegroundOrBackground"
            });
        else if (mode == MediaSourceMode && media is not null && !IsImage(media))
            hint = T("ErrorMediaFileNotFound");
        else if (mode == WebSourceMode && hasForeground && !IsWebUrl(LiveSourceText(mode)))
            hint = T("InvalidWebUrl");
        else if (mode == SlideshowSourceMode && hasForeground && !TryGetSlideshowInterval(out _))
            hint = T("InvalidSlideshowInterval");
        else if (hasBackground && !IsImage(background) && !backgroundVideo)
            hint = T("BackgroundFileNotFound");
        else if (invalidCaptureRate && !string.IsNullOrWhiteSpace(CaptureRate.Text))
            hint = T("InvalidFrameRate");

        bool foregroundImageReady = mode != MediaSourceMode || media is null || mediaThumbnail is not null;
        Send.IsEnabled = !busy && !selectingCaptureRegion && hint is null && !invalidCaptureRate && foregroundImageReady;
        if (Stop is not null)
            Stop.IsEnabled = !closing && (HasPendingResume || CurrentPlayback?.Cancellation.IsCancellationRequested == false
                || (!busy && SelectedDevice is not null && (hasForeground || hasBackground)));
        OutputHint.Text = hint ?? string.Empty;
        OutputHint.Visibility = hint is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>读取指定来源模式对应的路径或网址输入。</summary>
    private string LiveSourceText(int mode) => (mode == WebSourceMode ? LiveUrl.Text : LivePath.Text).Trim();

    /// <summary>在送屏前检查快照中的本地媒体来源。</summary>
    private static Task ValidateLocalSourcesAsync(string background, string? html, string[] slides,
        CancellationToken cancellation) => ReadLocalMediaAsync(() =>
    {
        if (background.Length > 0 && !File.Exists(background))
            throw UiError(new FileNotFoundException("Background file not found.", background), "BackgroundFileNotFound");
        if (html is not null && !IsWebUrl(html) && !File.Exists(html))
            throw UiError(new FileNotFoundException("HTML file not found.", html), "SelectHtmlSource");
        foreach (string slide in slides)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!File.Exists(slide))
                throw UiError(new FileNotFoundException("Slideshow image not found.", slide), "ErrorMediaFileNotFound");
        }
        return Task.FromResult(true);
    }, cancellation);

    /// <summary>判断来源是否为绝对 HTTP 或 HTTPS 网址。</summary>
    private static bool IsWebUrl(string source) => Uri.TryCreate(source, UriKind.Absolute, out var url)
        && url.Scheme is "http" or "https" && url.Host.Length > 0;

    /// <summary>检查帧率文本是否为 1 至 60 的整数。</summary>
    private static bool ValidRate(string text) => byte.TryParse(text, out byte fps) && fps is >= 1 and <= 60;

    /// <summary>读取并校验轮播的图片切换间隔。</summary>
    private bool TryGetSlideshowInterval(out TimeSpan interval)
    {
        bool valid = double.TryParse(SlideshowInterval.Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.CurrentCulture, out double seconds) && seconds is >= 0.03 and <= 3600;
        interval = valid ? TimeSpan.FromSeconds(seconds) : default;
        return valid;
    }

    /// <summary>选择多张轮播图片并更新前景。</summary>
    private void OnBrowseSlideshow(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Filter = T("SelectImages") + "|*.jpg;*.jpeg;*.png",
            Multiselect = true
        };
        if (picker.ShowDialog(this) != true)
            return;

        slideshowPaths = picker.FileNames;
        OnLocalPreviewInputChanged(sender, e);
    }

    /// <summary>清除当前前景来源并刷新预览。</summary>
    private void OnClearForeground(object sender, RoutedEventArgs e)
    {
        if (busy)
            return;
        foregroundBrowse?.Cancel();
        switch (LiveSource.SelectedIndex)
        {
            case 0:
                LivePath.Clear();
                break;
            case WebSourceMode:
                LiveUrl.Clear();
                break;
            case 1:
                preferredCaptureWindowTitle = null;
                captureRegion = null;
                LiveWindow.SelectedIndex = -1;
                break;
            case SlideshowSourceMode:
                slideshowPaths = [];
                break;
            case MediaSourceMode:
                media = null;
                mediaThumbnail = null;
                break;
        }
        RestoreHomePreview();
        RestartForegroundPreview();
        UpdateMediaControls();
        Status.Text = T("ForegroundCleared");
    }

    /// <summary>移除当前背景媒体并恢复预览状态。</summary>
    private void OnClearBackground(object sender, RoutedEventArgs e)
    {
        if (!busy)
        {
            BackgroundPath.Clear();
            Status.Text = T("BackgroundCleared");
        }
    }
}
