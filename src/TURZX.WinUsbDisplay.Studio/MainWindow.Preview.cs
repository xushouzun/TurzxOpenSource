using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TURZX.WinUsbDisplay.Media;
using static TURZX.WinUsbDisplay.Studio.StudioLanguage;

namespace TURZX.WinUsbDisplay.Studio;

// 管理本地媒体预览、旋转和图层布局交互。
public partial class MainWindow
{
    /// <summary>图片或视频文件来源的选择索引。</summary>
    private const int MediaSourceMode = 2;
    /// <summary>网页地址来源的选择索引。</summary>
    private const int WebSourceMode = 3;
    /// <summary>图片轮播来源的选择索引。</summary>
    private const int SlideshowSourceMode = 4;
    /// <summary>当前图片来源的本地预览缩略图。</summary>
    private ImageSource? mediaThumbnail;
    /// <summary>当前本地前景预览的取消源。</summary>
    private CancellationTokenSource? localPreview;
    /// <summary>保留当前及已取消但仍在清理的预览和帧率探测任务。</summary>
    private readonly HashSet<Task> localPreviewTasks = [];
    // 缓存原始 HTML 截图，以便播放时按当前布局编码匹配的首帧。
    /// <summary>可供当前网页来源和型号复用的最近一帧。</summary>
    private (string Source, DisplayModel Model, byte[] Frame)? localHtmlFrame;
    /// <summary>当前本地背景读取的取消源。</summary>
    private CancellationTokenSource? localBackgroundPreview;
    /// <summary>当前背景帧率探测的取消源。</summary>
    private CancellationTokenSource? backgroundFrameRateProbe;
    // 标记当前背景请求，异步读取或转封装结束时用它识别已过期的结果。
    /// <summary>用于拒绝过期异步结果的背景预览版本号。</summary>
    private int backgroundPreviewRevision;
    /// <summary>当前预览使用的背景视频源路径。</summary>
    private string? previewBackgroundVideoSource;
    /// <summary>首页预览关联的设备接口路径。</summary>
    private string? homePreviewDevicePath;
    /// <summary>首页预览关联的设备是否仍连接。</summary>
    private bool homePreviewDeviceConnected;
    /// <summary>前景相对于设备画布的缩放和偏移。</summary>
    private MediaLayout foregroundLayout = MediaLayout.Default;
    /// <summary>背景相对于设备画布的缩放和偏移。</summary>
    private MediaLayout backgroundLayout = MediaLayout.Default;
    /// <summary>同步布局控件期间用于屏蔽更改事件。</summary>
    private bool updatingLayoutControls;

    /// <summary>检查当前模式是否已选择可用的前景来源。</summary>
    private bool HasForegroundSource() => LiveSource.SelectedIndex switch
    {
        0 => !string.IsNullOrWhiteSpace(LivePath.Text),
        WebSourceMode => !string.IsNullOrWhiteSpace(LiveUrl.Text),
        1 => LiveWindow.SelectedItem is WindowCapture,
        MediaSourceMode => media is not null,
        SlideshowSourceMode => slideshowPaths.Length > 0,
        _ => false
    };

    /// <summary>获取指定图层的实际布局。</summary>
    private MediaLayout EffectiveLayout(bool background = false)
    {
        var layout = background ? backgroundLayout : foregroundLayout;
        var model = PreviewModel().ForRotation(PreviewQuarterTurns);
        return layout with
        {
            OffsetX = Math.Clamp(layout.OffsetX, -model.Width, model.Width),
            OffsetY = Math.Clamp(layout.OffsetY, -model.Height, model.Height)
        };
    }

    // 先按预览方向合成横向媒体，再旋转到设备原生的竖向画面。
    /// <summary>判断设备在所选方向下是否使用原生横向栅格。</summary>
    private static bool UsesNativeLandscapeRaster(DisplayModel model, byte rotation) =>
        (model.ProductId is 0x0046 or 0x0055 or 0x0070) && (rotation & 1) != 0;

    /// <summary>计算送屏前对媒体帧应用的旋转方向。</summary>
    internal static byte MediaFrameRotation(DisplayModel model, byte rotation) =>
        UsesNativeLandscapeRaster(model, rotation) ? (byte)(4 - rotation) : rotation;

    /// <summary>读取当前旋转选项对应的四分之一圈数。</summary>
    private int PreviewQuarterTurns => Rotation.SelectedIndex is int index and >= 0 ? index : 0;

    /// <summary>将所选屏幕方向同步到预览画布。</summary>
    private void ApplyRotationToTargets()
    {
        int quarterTurns = PreviewQuarterTurns;
        if (HomeCanvas is null)
            return;

        EndPreviewDrag();
        bool dimensionsChanged = HomeCanvas.SetRotation(quarterTurns);
        if (Send is null)
            return;

        ApplyLayoutToTargets();
        if (dimensionsChanged && LiveSource.SelectedIndex is 0 or 1 or WebSourceMode)
            RestartForegroundPreview();
    }

    /// <summary>将前景和背景布局应用到预览画布。</summary>
    private void ApplyLayoutToTargets()
    {
        HomeCanvas.SetLayouts(EffectiveLayout(), EffectiveLayout(background: true));
    }

    /// <summary>记录拖动起点、初始布局和选中层。</summary>
    private (Point Origin, MediaLayout Layout, int Layer)? previewDrag;

    /// <summary>指示预览画布当前是否允许拖动布局。</summary>
    private bool CanDragPreview => LayoutBar.IsEnabled
        && LayoutLayer.SelectedItem is ComboBoxItem { IsEnabled: true };

    /// <summary>指示布局工具条当前是否正在编辑背景。</summary>
    private bool EditingBackground => LayoutLayer.SelectedIndex == 1;

    /// <summary>获取布局工具条所选图层的布局。</summary>
    private MediaLayout EditingLayout => EditingBackground ? backgroundLayout : foregroundLayout;

    /// <summary>保存所选图层的新布局并更新预览。</summary>
    private void SetEditingLayout(MediaLayout layout)
    {
        if (EditingBackground)
            backgroundLayout = layout;
        else
            foregroundLayout = layout;
        RefreshLayoutControls();
        ApplyLayoutToTargets();
        if (EditingBackground && HomeCanvas.HasPreparedVideo)
            _ = TrackLocalPreviewAsync(RefreshLocalBackgroundAsync());
    }

    /// <summary>根据媒体来源和运行状态更新布局工具条。</summary>
    private void UpdateLayoutAvailability()
    {
        bool hasForeground = HasForegroundSource();
        bool hasBackground = !string.IsNullOrWhiteSpace(BackgroundPath.Text);
        ForegroundLayer.IsEnabled = hasForeground;
        BackgroundLayer.IsEnabled = hasBackground;
        LayoutBar.IsEnabled = !busy && !selectingCaptureRegion && (hasForeground || hasBackground);
        if (LayoutLayer.SelectedIndex < 0 || (EditingBackground && !hasBackground) || (!EditingBackground && !hasForeground))
            LayoutLayer.SelectedIndex = hasBackground && !hasForeground ? 1 : 0;
        RefreshLayoutControls();
        UpdatePreviewDragCursor();
    }

    /// <summary>用当前图层布局刷新布局控件。</summary>
    private void RefreshLayoutControls()
    {
        updatingLayoutControls = true;
        var layout = EditingLayout;
        LayoutMode.SelectedIndex = layout.Fill ? 1 : 0;
        ZoomValue.Text = $"{layout.ZoomPercent}%";
        ZoomOut.IsEnabled = layout.ZoomPercent > 25;
        ZoomIn.IsEnabled = layout.ZoomPercent < 400;
        updatingLayoutControls = false;
    }

    /// <summary>切换所编辑的图层，并刷新相关控件。</summary>
    private void OnLayoutLayerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ZoomValue is not null && LayoutMode is not null)
        {
            EndPreviewDrag();
            RefreshLayoutControls();
            UpdatePreviewDragCursor();
        }
    }

    /// <summary>根据可拖动状态更新画布光标。</summary>
    private void UpdatePreviewDragCursor()
    {
        bool canDrag = CanDragPreview;
        PreviewSurface.Cursor = selectingCaptureRegion ? Cursors.Cross : canDrag ? Cursors.SizeAll : null;
        if (!canDrag && !selectingCaptureRegion)
            EndPreviewDrag();
    }

    /// <summary>记录画布拖动或裁剪框选的起点。</summary>
    private void OnPreviewDragStart(object sender, MouseButtonEventArgs e)
    {
        if (selectingCaptureRegion)
        {
            if (HomeCanvas.IsOverRegionFrame(e) && PreviewSurface.CaptureMouse())
            {
                regionDragOrigin = HomeCanvas.GetRegionPoint(e);
                e.Handled = true;
            }
            return;
        }
        if (!CanDragPreview || !PreviewSurface.CaptureMouse())
            return;

        previewDrag = (HomeCanvas.GetScreenPosition(e), EffectiveLayout(EditingBackground), LayoutLayer.SelectedIndex);
        e.Handled = true;
    }

    /// <summary>随鼠标移动更新图层偏移或裁剪轮廓。</summary>
    private void OnPreviewDragMove(object sender, MouseEventArgs e)
    {
        if (regionDragOrigin is { } origin)
        {
            HomeCanvas.ShowRegionOutline(new Rect(origin, HomeCanvas.GetRegionPoint(e)));
            e.Handled = true;
            return;
        }
        if (previewDrag is not { } drag)
            return;
        if (!CanDragPreview || drag.Layer != LayoutLayer.SelectedIndex || e.LeftButton != MouseButtonState.Pressed)
        {
            EndPreviewDrag();
            return;
        }

        Vector delta = HomeCanvas.GetScreenPosition(e) - drag.Origin;
        var model = PreviewModel().ForRotation(PreviewQuarterTurns);
        int x = Math.Clamp(drag.Layout.OffsetX + (int)Math.Round(delta.X), -model.Width, model.Width);
        int y = Math.Clamp(drag.Layout.OffsetY + (int)Math.Round(delta.Y), -model.Height, model.Height);
        var layout = EditingLayout;
        if (layout.OffsetX != x || layout.OffsetY != y)
            SetEditingLayout(layout with { OffsetX = x, OffsetY = y });
        e.Handled = true;
    }

    /// <summary>完成拖动并应用最终布局或裁剪区域。</summary>
    private void OnPreviewDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (regionDragOrigin is { } origin)
        {
            var region = new Rect(origin, HomeCanvas.GetRegionPoint(e));
            if (region.Width >= .005 && region.Height >= .005)
                FinishCaptureRegionSelection(region);
            else
                EndPreviewDrag();
            e.Handled = true;
            return;
        }
        if (previewDrag is null)
            return;
        EndPreviewDrag();
        e.Handled = true;
    }

    /// <summary>失去鼠标捕获时清理预览拖动状态。</summary>
    private void OnPreviewDragLost(object sender, MouseEventArgs e) => ClearPreviewDrag();

    /// <summary>清除预览拖动的临时状态。</summary>
    private void ClearPreviewDrag()
    {
        previewDrag = null;
        if (regionDragOrigin.HasValue)
        {
            regionDragOrigin = null;
            HomeCanvas.ShowRegionOutline(null);
        }
    }

    /// <summary>释放拖动所占用的鼠标捕获。</summary>
    private void EndPreviewDrag()
    {
        ClearPreviewDrag();
        if (PreviewSurface.IsMouseCaptured)
            PreviewSurface.ReleaseMouseCapture();
    }

    /// <summary>按输入调整当前图层缩放。</summary>
    private void OnLayoutZoom(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string direction })
        {
            var layout = EditingLayout;
            int delta = direction == "In" ? 10 : -10;
            SetEditingLayout(layout with { ZoomPercent = Math.Clamp(layout.ZoomPercent + delta, 25, 400) });
        }
    }

    /// <summary>按选择更新当前图层的适应或铺满模式。</summary>
    private void OnLayoutModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingLayoutControls && LayoutLayer is not null && ZoomValue is not null && LayoutMode.SelectedIndex >= 0)
            SetEditingLayout(EditingLayout with { Fill = LayoutMode.SelectedIndex == 1 });
    }

    /// <summary>按方向按钮以设备像素为单位微调图层位置。</summary>
    private void OnLayoutNudge(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
            return;
        const int step = 8;
        var layout = EffectiveLayout(EditingBackground);
        SetEditingLayout(tag switch
        {
            "L" => layout with { OffsetX = layout.OffsetX - step },
            "R" => layout with { OffsetX = layout.OffsetX + step },
            "U" => layout with { OffsetY = layout.OffsetY - step },
            "D" => layout with { OffsetY = layout.OffsetY + step },
            _ => layout
        });
    }

    /// <summary>将当前图层的偏移重置到画布中心。</summary>
    private void OnLayoutCenter(object sender, RoutedEventArgs e)
    {
        SetEditingLayout(EditingLayout with { OffsetX = 0, OffsetY = 0 });
    }

    /// <summary>恢复首页预览关联设备的画面或本地来源。</summary>
    private void RestoreHomePreview()
    {
        int mode = LiveSource.SelectedIndex;
        if (mode == MediaSourceMode && mediaThumbnail is not null)
            HomeCanvas.ShowImage(mediaThumbnail);
        else
            HomeCanvas.ClearForeground();

        if (mode is 0 or 1 or WebSourceMode && !HasForegroundSource() && string.IsNullOrWhiteSpace(BackgroundPath.Text))
        {
            string hint = mode == 0 ? "SelectHtmlSource"
                : mode == WebSourceMode ? "InvalidWebUrl"
                : LiveWindow.Items.Count == 0 ? "NoCapturableWindows"
                : "SelectCaptureWindow";
            Status.SetResourceReference(TextBlock.TextProperty, ResourceKey(hint));
        }
    }

    /// <summary>前景输入变更时更新来源状态并安排预览。</summary>
    private void OnLocalPreviewInputChanged(object sender, RoutedEventArgs e)
    {
        if (restoringMediaPreferences || Send is null || LiveSource is null || BackgroundPath is null)
            return;

        if (ReferenceEquals(sender, LiveWindow) && LiveWindow.SelectedItem is WindowCapture window)
        {
            if (e is SelectionChangedEventArgs selection
                && selection.RemovedItems.OfType<WindowCapture>().Any(previous => previous.Handle != window.Handle))
                captureRegion = null;
            preferredCaptureWindowTitle = window.Title;
        }

        if (ReferenceEquals(sender, LivePath))
        {
            var scene = HtmlScenes.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is string file
                && string.Equals(LivePath.Text, Path.Combine(ScenesDirectory, file), StringComparison.OrdinalIgnoreCase));
            HtmlScenes.SelectedItem = scene ?? HtmlScenes.Items[0];
        }
        UpdateControls();
        int mode = LiveSource.SelectedIndex;
        bool keepForeground = HasHtmlPreview(mode, LiveSourceText(mode));
        if (!keepForeground)
            RestoreHomePreview();
        RestartForegroundPreview(keepForeground);
    }

    /// <summary>背景输入变更时安排背景预览和帧率探测。</summary>
    private void OnBackgroundPreviewInputChanged(object sender, RoutedEventArgs e)
    {
        if (restoringMediaPreferences || Send is null)
            return;

        _ = TrackLocalPreviewAsync(UpdateBackgroundFrameRateAsync());
        UpdateControls();
        _ = TrackLocalPreviewAsync(RefreshLocalBackgroundAsync());
    }

    /// <summary>探测所选背景视频的帧率并更新显示。</summary>
    private async Task UpdateBackgroundFrameRateAsync()
    {
        if (closing)
            return;

        backgroundFrameRateProbe?.Cancel();
        backgroundFrameRateProbe = null;
        sourceBackgroundFps = null;
        UpdateFrameRateSummary();
        string path = BackgroundPath.Text.Trim();
        if (!IsVideo(path))
            return;

        using var probe = new CancellationTokenSource();
        backgroundFrameRateProbe = probe;
        var token = probe.Token;
        try
        {
            await Task.Delay(250, token);
            if (!await ReadLocalMediaAsync(() => Task.FromResult(File.Exists(path)), token))
                return;
            double? detected = await Task.Run(() => VideoConverter.DetectSourceFrameRateAsync(path, token), token);
            token.ThrowIfCancellationRequested();
            sourceBackgroundFps = detected;
            UpdateFrameRateSummary();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // 用户开始播放该视频时，由播放流程报告解码错误。
        }
        finally
        {
            if (ReferenceEquals(backgroundFrameRateProbe, probe))
                backgroundFrameRateProbe = null;
        }
    }

    /// <summary>取消过期的本地背景请求并启动新的读取。</summary>
    private async Task RefreshLocalBackgroundAsync()
    {
        if (busy)
            return;

        int revision = ++backgroundPreviewRevision;
        localBackgroundPreview?.Cancel();
        using var lifetime = new CancellationTokenSource();
        localBackgroundPreview = lifetime;
        var token = lifetime.Token;
        try
        {
            await Task.Delay(250, token);
            await ApplyLocalBackgroundAsync(revision, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                Status.Text = T("PreviewFailed", DescribeUiError(ex));
        }
        finally
        {
            if (ReferenceEquals(localBackgroundPreview, lifetime))
                localBackgroundPreview = null;
        }
    }

    // 读取或转码完成后再次检查版本，防止旧请求覆盖更新的背景。
    /// <summary>读取或封装背景媒体，并在版本仍匹配时更新画布。</summary>
    private async Task ApplyLocalBackgroundAsync(int revision, CancellationToken token, Action? beforeApply = null)
    {
        if (revision != backgroundPreviewRevision)
            return;

        string background = BackgroundPath.Text.Trim();
        bool exists = (IsImage(background) || IsVideo(background))
            && await ReadLocalMediaAsync(() => Task.FromResult(File.Exists(background)), token);
        byte[]? image = exists && IsImage(background)
            ? await ReadLocalMediaAsync(() => File.ReadAllBytesAsync(background, token), token) : null;
        string? video = exists && IsVideo(background) ? Path.GetFullPath(background) : null;
        bool ownsVideo = video is not null && Path.GetExtension(video).ToLowerInvariant() is ".h264" or ".264";
        if (ownsVideo)
        {
            byte fps = await Task.Run(() => VideoConverter.DetectPlaybackFrameRateAsync(video!, token), token);
            video = await PreparePreviewVideoAsync(video!, fps, token);
        }

        if (token.IsCancellationRequested || revision != backgroundPreviewRevision)
        {
            if (ownsVideo && video is not null)
                PreviewCanvas.TryDelete(video);
            token.ThrowIfCancellationRequested();
            return;
        }

        beforeApply?.Invoke();
        if (image is not null)
            HomeCanvas.ShowBackgroundImage(image);
        else if (video is not null)
            HomeCanvas.ShowVideo(video, loop: true, ownsFile: ownsVideo, background: true);
        else
            HomeCanvas.ClearBackground();
        previewBackgroundVideoSource = video is null ? null : background;
    }

    /// <summary>返回预览所用的设备型号，未选设备时使用默认画布。</summary>
    private DisplayModel PreviewModel() => SelectedDeviceChoice?.Device.Model ?? new DisplayModel(T("Preview"), 0, 480, 480);

    /// <summary>同步预览画布关联的设备及连接状态。</summary>
    private void UpdatePreviewDevice()
    {
        string? path = SelectedDeviceChoice?.Device.Path;
        bool connected = SelectedDevice is not null;
        if (homePreviewDevicePath != path || homePreviewDeviceConnected != connected)
        {
            homePreviewDevicePath = path;
            homePreviewDeviceConnected = connected;
            HomeCanvas.SetModel(PreviewModel());
            ShowSelectedPlayback();
        }
    }

    /// <summary>取消正在运行的本地前景和背景预览。</summary>
    private void StopLocalPreview()
    {
        regionPreviewFrame = null;
        localHtmlFrame = null;
        FinishCaptureRegionSelection();
        UpdateCaptureRegionControls();
        var current = localPreview;
        localPreview = null;
        current?.Cancel();
        localBackgroundPreview?.Cancel();
    }

    /// <summary>判断指定模式及来源是否可以启动网页预览。</summary>
    private static bool HasHtmlPreview(int mode, string source) =>
        (mode == 0 && source.Length > 0) || (mode == WebSourceMode && IsWebUrl(source));

    /// <summary>保留当前背景并重新启动前景预览。</summary>
    private void RestartForegroundPreview(bool keepForeground = false) =>
        RestartLocalPreview(keepBackground: HomeCanvas.HasBackground && localBackgroundPreview is null,
            keepForeground: keepForeground);

    /// <summary>重建本地媒体预览，可保留前景或背景画面。</summary>
    private void RestartLocalPreview(bool keepBackground = false, bool keepForeground = false)
    {
        if (busy)
            return;

        StopLocalPreview();
        var cancellation = new CancellationTokenSource();
        localPreview = cancellation;
        _ = TrackLocalPreviewAsync(RunLocalPreviewAsync(cancellation, backgroundPreviewRevision, keepBackground, keepForeground));
    }

    /// <summary>在允许本地预览更新时显示捕捉状态。</summary>
    private void ReportPreviewStatus(string message)
    {
        if (SelectedDeviceChoice is not { Device: var device } || !playbackErrors.ContainsKey(device.Path))
            Status.Text = message;
    }

    /// <summary>跟踪预览任务直至资源清理完成，并在退出时通知关闭流程。</summary>
    private async Task TrackLocalPreviewAsync(Task work)
    {
        localPreviewTasks.Add(work);
        try
        {
            await work;
        }
        finally
        {
            localPreviewTasks.Remove(work);
            CloseWhenIdle();
        }
    }

    // 保留前景画面，直到替代帧到达或加载失败。
    // 稍等输入稳定后再启动预览，避免连续编辑路径时反复创建捕捉任务。
    /// <summary>按所选媒体来源构建本地预览并管理其取消。</summary>
    private async Task RunLocalPreviewAsync(CancellationTokenSource lifetime, int backgroundRevision,
        bool keepBackground, bool keepForeground)
    {
        CancellationToken token = lifetime.Token;
        try
        {
            await Task.Delay(250, token);
            int mode = LiveSource.SelectedIndex;
            string source = mode == MediaSourceMode ? media ?? "" : LiveSourceText(mode);
            var captureWindow = LiveWindow.SelectedItem as WindowCapture;
            DisplayModel nativeModel = PreviewModel();
            DisplayModel model = nativeModel.ForRotation(PreviewQuarterTurns);
            byte captureFps = byte.TryParse(CaptureRate.Text, out byte parsed) && parsed is >= 1 and <= 60 ? parsed : DefaultCaptureFps;
            bool foregroundFile = mode == MediaSourceMode && IsImage(source);
            bool htmlPreview = HasHtmlPreview(mode, source);
            bool windowPreview = mode == 1 && captureWindow is not null;
            // 只有存在能够接替当前画面的来源时，才暂时保留旧前景。
            if (keepForeground && !(foregroundFile || htmlPreview || windowPreview))
            {
                HomeCanvas.ClearForeground();
                keepForeground = false;
            }

            if (!keepBackground)
            {
                HomeCanvas.SetModel(nativeModel, keepForeground: keepForeground);
                HomeCanvas.SetLayouts(EffectiveLayout(), EffectiveLayout(background: true));
                try
                {
                    await ApplyLocalBackgroundAsync(backgroundRevision, token);
                }
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    ReportPreviewStatus(T("PreviewFailed", DescribeUiError(error)));
                }
            }
            token.ThrowIfCancellationRequested();

            if (htmlPreview)
            {
                if (MediaPage.IsVisible)
                    ReportPreviewStatus(T("LoadingHTMLPreview"));
                // 浏览器连接或取帧出错时重建一次实例，丢弃上一轮缓存的截图。
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        await using var html = await Task.Run(
                            () => HtmlCapture.OpenAsync(source, model, token, fastCapture: captureFps >= 30), token);
                        await ShowLiveFramesAsync(html.ReadFrameAsync, () =>
                        {
                            if (MediaPage.IsVisible)
                                ReportPreviewStatus(T("HTMLPreviewIsReady"));
                        });
                        break;
                    }
                    catch (Exception error) when (error is IOException or TimeoutException
                        or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException)
                    {
                        token.ThrowIfCancellationRequested();
                        localHtmlFrame = null;
                        if (attempt != 0)
                        {
                            if (MediaPage.IsVisible)
                                ReportPreviewStatus(T("HtmlCaptureFailed", DescribeUiError(error)));
                            break;
                        }
                        await Task.Delay(300, token);
                    }
                }
            }
            else if (mode == 1 && captureWindow is not null)
            {
                using var surface = new WindowCapture.CaptureSurface();
                await ShowLiveFramesAsync(async cancellation => await captureWindow.CaptureAsync(model, surface, cancellation),
                    () => ReportPreviewStatus(T("MediaIsReady")));
            }
            else if (mode == SlideshowSourceMode && TryGetSlideshowInterval(out var interval))
            {
                string[] selectedPaths = slideshowPaths;
                string[] paths = await ReadLocalMediaAsync(() => Task.FromResult(selectedPaths.Where(path =>
                {
                    token.ThrowIfCancellationRequested();
                    return File.Exists(path);
                }).ToArray()), token);
                int index = 0;
                if (paths.Length > 0)
                    await ShowLiveFramesAsync(async cancellation =>
                    {
                        byte[] frame = await ReadLocalMediaAsync(() => File.ReadAllBytesAsync(paths[index], cancellation), cancellation);
                        index = (index + 1) % paths.Length;
                        return frame;
                    }, interval: interval);
            }
            else
            {
                if (foregroundFile)
                {
                    var thumbnail = mediaThumbnail ?? await LoadForegroundThumbnailAsync(source, token);
                    token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(localPreview, lifetime) || LiveSource.SelectedIndex != MediaSourceMode
                        || !string.Equals(media, source, StringComparison.Ordinal))
                        return;
                    mediaThumbnail = thumbnail;
                    HomeCanvas.ShowImage(thumbnail);
                    UpdateMediaControls();
                    keepForeground = false;
                }
            }

            // 持续读取本地实时帧并更新预览画布。
            async Task ShowLiveFramesAsync(Func<CancellationToken, ValueTask<byte[]?>> capture, Action? onFirstFrame = null, TimeSpan? interval = null)
            {
                using var timer = new PeriodicTimer(interval ?? TimeSpan.FromSeconds(1d / captureFps));
                bool firstFrame = true;
                do
                {
                    byte[]? frame = await capture(token);
                    token.ThrowIfCancellationRequested();
                    if (frame is null)
                        return;
                    if (mode is 0 or WebSourceMode)
                        localHtmlFrame = (source, model, frame);
                    if (selectingCaptureRegion)
                        continue;

                    HomeCanvas.ShowFrame(frame, overlay: true, crop: CurrentCaptureRegion);
                    keepForeground = false;
                    if (mode is 0 or 1 or WebSourceMode)
                    {
                        regionPreviewFrame = frame;
                        if (firstFrame)
                            UpdateCaptureRegionControls();
                    }
                    firstFrame = false;
                    onFirstFrame?.Invoke();
                    onFirstFrame = null;
                } while (await timer.WaitForNextTickAsync(token));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                localHtmlFrame = null;
                ReportPreviewStatus(T("PreviewFailed", DescribeUiError(ex)));
            }
        }
        finally
        {
            if (ReferenceEquals(localPreview, lifetime))
            {
                if (keepForeground && !token.IsCancellationRequested)
                    HomeCanvas.ClearForeground();
                localPreview = null;
            }
            lifetime.Dispose();
        }
    }
}
