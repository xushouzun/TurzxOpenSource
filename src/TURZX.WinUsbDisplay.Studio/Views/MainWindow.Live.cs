using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TURZX.WinUsbDisplay.Media;
using static TURZX.WinUsbDisplay.Studio.StudioLanguage;

namespace TURZX.WinUsbDisplay.Studio;

// 实时来源、捕捉、编码和送屏。
public partial class MainWindow
{
    /// <summary>切换来源模式时保留的本地 HTML 文件路径。</summary>
    private string htmlSourcePath = "";
    /// <summary>最近一次选中的实时来源模式。</summary>
    private int lastSourceMode = -1;

    /// <summary>切换来源模式并同步其输入和预览。</summary>
    private void OnLiveSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        foregroundBrowse?.Cancel();
        if (lastSourceMode == 0 && !restoringMediaPreferences)
            htmlSourcePath = LivePath.Text;
        lastSourceMode = LiveSource.SelectedIndex;
        if (lastSourceMode == 0 && htmlSourcePath.Length == 0 && !restoringMediaPreferences)
        {
            htmlSourcePath = Path.Combine(ScenesDirectory, "default.html");
        }
        LivePath.Text = lastSourceMode == 0 ? htmlSourcePath : "";
        ForegroundImageRow.Visibility = lastSourceMode == MediaSourceMode ? Visibility.Visible : Visibility.Collapsed;
        HtmlScenes.Visibility = LiveFileRow.Visibility = lastSourceMode == 0 ? Visibility.Visible : Visibility.Collapsed;
        LiveWindowRow.Visibility = LiveSource.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        SlideshowSettings.Visibility = lastSourceMode == SlideshowSourceMode ? Visibility.Visible : Visibility.Collapsed;
        LiveUrlRow.Visibility = lastSourceMode == WebSourceMode ? Visibility.Visible : Visibility.Collapsed;
        CaptureRateRow.Visibility = LiveSource.SelectedIndex is 0 or 1 or WebSourceMode ? Visibility.Visible : Visibility.Collapsed;
        if (LiveSource.SelectedIndex == 1 && !restoringMediaPreferences)
            OnRefreshWindows(sender, e);
        if (restoringMediaPreferences)
            return;

        RestoreHomePreview();
        RestartForegroundPreview();
        UpdateControls();
    }

    /// <summary>重新枚举可捕捉的窗口，尝试恢复原选择。</summary>
    private void OnRefreshWindows(object sender, RoutedEventArgs e)
    {
        var previous = (LiveWindow.SelectedItem as WindowCapture)?.Handle;
        var windows = WindowCapture.List(new WindowInteropHelper(this).Handle);
        LiveWindow.ItemsSource = windows;
        var selected = windows.FirstOrDefault(window => window.Handle == previous);
        if (selected is null && preferredCaptureWindowTitle is { Length: > 0 } title)
        {
            var matches = windows.Where(window => window.Title == title).Take(2).ToArray();
            if (matches.Length == 1)
                selected = matches[0];
        }
        LiveWindow.SelectedItem = selected;
        if (windows.Count == 0)
            Status.Text = T("NoCapturableWindows");
    }

    /// <summary>随 Studio 安装的内置 HTML 场景目录。</summary>
    private static string ScenesDirectory => Path.Combine(AppContext.BaseDirectory, "Scenes");

    /// <summary>打开文件选择器以选取 HTML 来源。</summary>
    private void OnBrowseLiveSource(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Filter = T("HTMLPages") + "|*.html;*.htm",
            InitialDirectory = ScenesDirectory,
            FileName = "default.html"
        };
        if (picker.ShowDialog(this) == true)
            SelectHtmlSource(picker.FileName);
    }

    /// <summary>设置 HTML 文件来源并刷新本地预览。</summary>
    private void SelectHtmlSource(string path)
    {
        if (string.Equals(LivePath.Text, path, StringComparison.Ordinal))
            RestartForegroundPreview(keepForeground: true);
        else
            LivePath.Text = path;
    }

    /// <summary>选择并保存供媒体操作使用的 FFmpeg 可执行文件。</summary>
    private void OnConfigureFfmpeg(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = T("ChooseTheFFmpegExecutable"),
            Filter = "FFmpeg|ffmpeg.exe",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true)
            return;

        try
        {
            VideoConverter.ConfigureExecutable(picker.FileName);
            _ = TrackLocalPreviewAsync(UpdateBackgroundFrameRateAsync());
            Status.Text = T("FfmpegConfigured");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status.Text = T("CouldNotSaveSettings", DescribeUiError(ex));
        }
    }

    /// <summary>打开文件选择器以选取背景媒体。</summary>
    private void OnBrowseBackground(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Filter = T("ImagesAndVideos") + "|*.jpg;*.jpeg;*.png;*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v;*.h264;*.264"
        };
        if (picker.ShowDialog(this) == true)
            BackgroundPath.Text = picker.FileName;
    }

    /// <summary>按选择组装实时捕捉、编码、背景和设备输出。</summary>
    private async Task StartLiveAsync(int mode, string source)
    {
        if (busy || SelectedDevice is not { } device)
            return;

        string background = BackgroundPath.Text.Trim();
        bool backgroundVideo = IsVideo(background);
        var window = LiveWindow.SelectedItem as WindowCapture;
        if ((mode == 0 && source.Length == 0) || (mode == 1 && window is null)
            || (mode == MediaSourceMode && mediaThumbnail is null))
        {
            Status.Text = T("SelectValidLiveSource");
            return;
        }
        TimeSpan slideshowInterval = default;
        string[] slides = slideshowPaths;
        if (mode == SlideshowSourceMode && !TryGetSlideshowInterval(out slideshowInterval))
            return;

        byte captureFps = DefaultCaptureFps;
        byte rotation = (byte)PreviewQuarterTurns;
        byte frameRotation = MediaFrameRotation(device.Model, rotation);
        if (mode is 0 or 1 && (!byte.TryParse(CaptureRate.Text, out captureFps) || captureFps is < 1 or > 60))
        {
            Status.Text = T("InvalidFrameRate");
            return;
        }

        var canvas = device.Model.ForRotation(rotation);
        byte[]? cachedHtmlFrame = mode == 0 && localHtmlFrame is { } preview
            && preview.Model == canvas && string.Equals(preview.Source, source, StringComparison.Ordinal)
            ? preview.Frame : null;
        Rect? crop = CurrentCaptureRegion;
        var foreground = EffectiveLayout();
        var backgroundPlacement = EffectiveLayout(background: true);
        await RunPlaybackAsync(device, async active =>
        {
            // 设备重连时重新捕捉。仅第一次尝试可使用本地预览帧。
            bool fastSlideshow = mode == SlideshowSourceMode && !backgroundVideo
                && slideshowInterval <= TimeSpan.FromMilliseconds(50)
                && device.Model.ProductId is 0x0021 or 0x0046 or 0x0055 or 0x0070 or 0x0123;
            active.IsImageSlideshow = mode == SlideshowSourceMode && !backgroundVideo
                && (device.Model.ProductId == 0x0046 || fastSlideshow);
            byte[]? startupHtmlFrame = cachedHtmlFrame;
            cachedHtmlFrame = null;
            byte[] key = DisplaySettings.ReadCommandKey();
            DeviceVideoCache.Entry? prepared = null;
            byte videoFps = 0;
            try
            {
                await ValidateLocalSourcesAsync(background, mode == 0 ? source : null,
                    mode == SlideshowSourceMode ? slides : [], active.Token);
                // 0123 的实时 H.264 虽有应答，实屏仍出现局部马赛克；先统一使用 JPEG 路径。
                if (mode is 0 or 1 && captureFps == 20 && background.Length == 0 && frameRotation == 0 && crop is null
                    && foreground is { ZoomPercent: 100, OffsetX: 0, OffsetY: 0 }
                    && UsesBufferedForegroundVideo(device.Model, mode)
                    && await TryPlayBufferedForegroundAsync(active, source, mode == 1 ? window : null, key, rotation, captureFps))
                {
                    active.Token.ThrowIfCancellationRequested();
                    SetPlaybackStatus(active, T("OutputComplete"));
                    return;
                }

                // 2.1 寸 0021 型号在主机端合成前景和视频背景，设备只接收一路 H.264。
                // 在显示方向上合成，再转回编码器使用的设备原生方向。
                if (backgroundVideo && (mode is 0 or 1 or MediaSourceMode or SlideshowSourceMode)
                    && device.Model.ProductId == 0x0021 && captureFps is 20 or 30)
                {
                    await Task.Run(async () =>
                    {
                        var token = active.Token;
                        token.ThrowIfCancellationRequested();
                        var output = device.Model;
                        const double browserScale = 0.5;
                        var layoutCanvas = output.ForRotation(frameRotation);
                        var nativeLayoutCanvas = device.Model.ForRotation(frameRotation);
                        // 背景偏移以设备像素保存，缩小编码尺寸时也要按比例换算偏移。
                        var scaledBackground = backgroundPlacement with
                        {
                            OffsetX = (int)Math.Round(backgroundPlacement.OffsetX * (double)layoutCanvas.Width / nativeLayoutCanvas.Width),
                            OffsetY = (int)Math.Round(backgroundPlacement.OffsetY * (double)layoutCanvas.Height / nativeLayoutCanvas.Height)
                        };
                        await using var decoder = new LoopingVideoFrames(background, layoutCanvas, captureFps, scaledBackground);
                        // 编码器最多持有正在写入、排队和正在读取的三个背景帧。
                        int backgroundBytes = checked(layoutCanvas.Width * layoutCanvas.Height * 4);
                        byte[][] backgroundBuffers = [new byte[backgroundBytes], new byte[backgroundBytes], new byte[backgroundBytes]];
                        int nextBackground = 0;
                        using var surface = mode == 1 ? new WindowCapture.CaptureSurface() : null;
                        byte[]? still = mode == MediaSourceMode ? await File.ReadAllBytesAsync(source, token) : null;
                        await using var slideshow = new SlideshowCapture(slides, slideshowInterval, static bytes => bytes);
                        var browser = mode == 0 ? await HtmlCapture.OpenAsync(source, canvas, token,
                            fastCapture: false, pixelScale: browserScale) : null;
                        bool push = true;
                        byte[]? previousImage = null;
                        BitmapSource? decodedImage = null;
                        BitmapSource? lastWindowFrame = null;
                        BitmapSource? renderedImage = null;
                        byte[]? imageOverlay = null;
                        try
                        {
                            using var session = OpenPlaybackDevice(device, key, rotation, token);
                            using var playbackTimer = new PlaybackTimer();
                            await PlayForegroundVideoAsync<Tuple<object, byte[]>>(active, session, async cancellation =>
                            {
                                if (mode == MediaSourceMode)
                                    return await ComposeAsync(still, cancellation);
                                if (mode == SlideshowSourceMode)
                                    return await ComposeAsync(await slideshow.ReadFrameAsync(cancellation), cancellation);
                                if (surface is not null)
                                {
                                    lastWindowFrame = await window!.CaptureNextBitmapAsync(canvas, surface, cancellation,
                                        nativeSize: true, previousFrame: lastWindowFrame);
                                    return await ComposeAsync(lastWindowFrame, cancellation);
                                }
                                byte[]? frame = null;
                                if (push)
                                {
                                    try
                                    {
                                        frame = await browser!.ReadPngScreencastAsync(cancellation, repeatAfter: TimeSpan.Zero);
                                    }
                                    catch (Exception error) when (!cancellation.IsCancellationRequested
                                        && (error is TimeoutException or IOException))
                                    {
                                        push = false;
                                        (browser, frame) = await UseScreenshotFallbackAsync(browser!, source, canvas, cancellation,
                                            fastCapture: true, pixelScale: browserScale);
                                    }
                                }
                                else
                                    frame = await browser!.ReadFrameAsync(cancellation);
                                return await ComposeAsync(frame, cancellation);
                            }, frame =>
                            {
                                BitmapSource snapshot = frame.Item1 is BitmapSource bitmap ? bitmap : DecodeImage((byte[])frame.Item1);
                                Rect? region = mode is 0 or 1 ? crop : null;
                                if (frameRotation == 0)
                                {
                                    if (mode is 0 or MediaSourceMode or SlideshowSourceMode)
                                    {
                                        imageOverlay ??= new byte[frame.Item2.Length];
                                        // 静态图片和重复截图复用已经渲染的前景层，每帧只与新的背景像素混合。
                                        if (!ReferenceEquals(renderedImage, snapshot))
                                        {
                                            DisplayFrameProcessor.RenderVideoOverlay(snapshot, foreground, device.Model,
                                                output, region, imageOverlay);
                                            renderedImage = snapshot;
                                        }
                                        return DisplayFrameProcessor.BlendVideoOverlay(imageOverlay, frame.Item2);
                                    }
                                    return DisplayFrameProcessor.CompositeVideoPixels(snapshot, foreground, frame.Item2,
                                        device.Model, output, region);
                                }
                                var layer = BitmapSource.Create(layoutCanvas.Width, layoutCanvas.Height, 96, 96,
                                    System.Windows.Media.PixelFormats.Bgr32, null, frame.Item2, checked(layoutCanvas.Width * 4));
                                return DisplayFrameProcessor.PrepareForegroundVideoPixels(snapshot, foreground, layer, MediaLayout.Default,
                                    device.Model, frameRotation, region, output);
                            }, captureFps,
                                T("SendingLiveOutput"), jpegInput: false, outputCanvas: null, rawInputCanvas: output,
                                reuseFrameBuffers: true);
                        }
                        finally
                        {
                            if (browser is not null)
                                await browser.DisposeAsync();
                        }

                        // 将当前捕捉帧按布局合成到输出画布。
                        async ValueTask<Tuple<object, byte[]>?> ComposeAsync(object? frame, CancellationToken cancellation)
                        {
                            if (frame is null)
                                return null;
                            byte[] pixels = backgroundBuffers[nextBackground];
                            await decoder.ReadAsync(pixels, cancellation);
                            nextBackground = (nextBackground + 1) % backgroundBuffers.Length;
                            return Tuple.Create(frame, pixels);
                        }

                        // 复用同一输入数组已解码的冻结位图。
                        BitmapSource DecodeImage(byte[] image)
                        {
                            if (ReferenceEquals(previousImage, image))
                                return decodedImage!;
                            decodedImage = DisplayFrameProcessor.DecodeFrame(image);
                            decodedImage.Freeze();
                            previousImage = image;
                            return decodedImage;
                        }
                    }, active.Token);
                    return;
                }

                if (backgroundVideo)
                {
                    // 其余视频背景交给设备解码。打开播放会话前先准备设备码流和本地预览。
                    SetVideoStartupStage(active, "ReadingVideoFrameRate");
                    videoFps = await ReadDeviceVideoFrameRateAsync(background, active.Token);
                    prepared = await PrepareDeviceVideoAsync(active, background, device.Model, videoFps, backgroundPlacement,
                        frameRotation, active.Token);
                    SetVideoStartupStage(active, "PreparingVideoPreview");
                    active.PreviewVideo = await PreparePreviewVideoAsync(prepared.Stream.Name, videoFps, active.Token);
                    active.Token.ThrowIfCancellationRequested();
                    SetVideoStartupStage(active, SendingVideoToDisplayKey);
                    SetPlaybackStatus(active, T(SendingVideoToDisplayKey));
                }
                await Task.Run(async () =>
                {
                    var token = active.Token;
                    token.ThrowIfCancellationRequested();
                    byte[]? backgroundImage = IsImage(background) ? await File.ReadAllBytesAsync(background, token) : null;
                    byte[]? png = mode == MediaSourceMode
                        ? DisplayFrameProcessor.Prepare(await File.ReadAllBytesAsync(source, token), device.Model, preserveTransparency: true, foreground, frameRotation)
                        : null;
                    var capturedBackground = mode is 0 or 1 && backgroundImage is not null
                        ? DisplayFrameProcessor.DecodeFrame(backgroundImage)
                        : null;
                    capturedBackground?.Freeze();
                    HtmlCapture? html = null;
                    // 浏览器视口已与原生画布匹配时，直接发送 JPEG，省去解码和重新编码。
                    bool preferFastJpeg = device.Model.ProductId is 0x0070 or 0x0123
                        || (device.Model.ProductId == 0x0055 && captureFps == 20);
                    byte browserOutputRotation = device.Model.ProductId == 0x0070 && mode == 0
                        && File.Exists(source)
                        && !backgroundVideo && capturedBackground is null && crop is null
                        && foreground is { ZoomPercent: 100, OffsetX: 0, OffsetY: 0 }
                        ? frameRotation : (byte)0;
                    bool directJpeg = browserOutputRotation != 0 || (device.Model.ProductId == 0x0021 || preferFastJpeg)
                        && !backgroundVideo
                        && capturedBackground is null && frameRotation == 0 && crop is null
                        && foreground is { ZoomPercent: 100, OffsetX: 0, OffsetY: 0 };
                    bool usePreviewFrame = startupHtmlFrame is not null && browserOutputRotation == 0;
                    // 无视频背景时用 JPEG 捕捉与送屏；原生直传以外的布局仍由主机合成。
                    bool jpegCapture = CanCaptureHtmlAsJpeg(directJpeg, preferFastJpeg,
                        backgroundVideo, capturedBackground is not null);
                    bool pushCompositedHtml = !jpegCapture && captureFps >= 20;
                    bool useScreencast = jpegCapture || pushCompositedHtml;
                    // 从当前网页捕捉链路读取下一帧。
                    async ValueTask<byte[]?> ReadHtmlFrameAsync(CancellationToken frameToken)
                    {
                        if (usePreviewFrame)
                        {
                            usePreviewFrame = false;
                            // 实时捕捉启动期间，先发送缓存中的预览帧。
                            return startupHtmlFrame;
                        }

                        // 单独送屏的 JPEG 快路径允许 Chromium 更快生成下一帧。
                        html ??= await HtmlCapture.OpenAsync(source, canvas, frameToken,
                            fastCapture: (jpegCapture && preferFastJpeg) || (!useScreencast && captureFps >= 20),
                            pixelScale: 1, outputRotation: browserOutputRotation);
                        if (pushCompositedHtml && useScreencast)
                        {
                            try
                            {
                                // PNG 保留 Alpha 通道，供前景裁剪和背景合成使用。
                                return await html.ReadPngScreencastAsync(frameToken, repeatAfter: TimeSpan.Zero);
                            }
                            catch (Exception error) when (!frameToken.IsCancellationRequested
                                && (error is TimeoutException or IOException))
                            {
                                useScreencast = false;
                                (html, byte[]? screenshot) = await UseScreenshotFallbackAsync(html, source, canvas,
                                    frameToken, fastCapture: true);
                                return screenshot;
                            }
                        }
                        if (jpegCapture)
                        {
                            byte[]? jpeg;
                            try
                            {
                                jpeg = useScreencast
                                    ? await html.ReadJpegScreencastAsync(frameToken,
                                        quality: preferFastJpeg ? 70 : 85,
                                        repeatAfter: TimeSpan.FromMilliseconds(preferFastJpeg ? 1000d / captureFps : 5))
                                    : await html.ReadJpegFrameAsync(frameToken);
                            }
                            catch (Exception error) when (useScreencast && !frameToken.IsCancellationRequested
                                && (error is TimeoutException or IOException))
                            {
                                useScreencast = false;
                                (html, jpeg) = await UseScreenshotFallbackAsync(html, source, canvas, frameToken,
                                    jpeg: true, fastCapture: preferFastJpeg, outputRotation: browserOutputRotation);
                            }
                            return jpeg;
                        }
                        return await html.ReadFrameAsync(frameToken);
                    }

                    // 以送屏帧关联预览；送屏帧退出队列和缓存后，预览位图也可回收。
                    var slideshowPreviews = new ConditionalWeakTable<byte[], BitmapSource>();
                    var slideshowBackground = mode == SlideshowSourceMode && !backgroundVideo ? backgroundImage : null;
                    BitmapSource? slideshowBackgroundBitmap = slideshowBackground is null
                        ? null : DisplayFrameProcessor.DecodeFrame(slideshowBackground);
                    slideshowBackgroundBitmap?.Freeze();
                    await using var slideshow = new SlideshowCapture(slides, slideshowInterval, image =>
                    {
                        var (frame, preview) = DisplayFrameProcessor.PrepareSlideshowFrame(image, foreground,
                            slideshowBackgroundBitmap, backgroundPlacement, device.Model, backgroundVideo, frameRotation,
                            preferJpeg: fastSlideshow);
                        slideshowPreviews.Add(frame, preview);
                        return frame;
                    }, cacheLimitBytes: active.IsImageSlideshow ? 16 * 1024 * 1024 : 0);

                    using var windowSurface = mode == 1 ? new WindowCapture.CaptureSurface() : null;
                    Func<CancellationToken, ValueTask<byte[]?>>? capture = mode switch
                    {
                        0 => ReadHtmlFrameAsync,
                        1 => async cancellation => DisplayFrameProcessor.PrepareLiveBitmapFrame(
                            await window!.CaptureNextBitmapAsync(canvas, windowSurface!, cancellation), foreground, capturedBackground, backgroundPlacement,
                            device.Model, preserveTransparency: backgroundVideo, rotation: frameRotation, crop: crop),
                        MediaSourceMode => _ => ValueTask.FromResult(png),
                        SlideshowSourceMode => slideshow.ReadFrameAsync,
                        _ => null
                    };
                    Task<byte[]?>? pendingHtmlFrame = null;
                    if (mode == 0 && capture is { } originalCapture)
                    {
                        byte[]? previousScreenshot = null;
                        byte[]? previousOverlay = null;
                        capture = async frameToken =>
                        {
                            byte[]? frame = await (pendingHtmlFrame ?? originalCapture(frameToken).AsTask());
                            pendingHtmlFrame = null;
                            if (frame is null)
                                return null;
                            // 截图和当前帧合成并行，推帧由 Chromium 自行调度。
                            if (!directJpeg && !backgroundVideo && !useScreencast)
                                pendingHtmlFrame = originalCapture(frameToken).AsTask();
                            if (ReferenceEquals(frame, previousScreenshot)
                                || (previousScreenshot is not null && frame.AsSpan().SequenceEqual(previousScreenshot)))
                                return previousOverlay;

                            // JPEG 播放路径始终使用同一固件图层，缓存的 PNG 预览帧也发送到该图层。
                            previousOverlay = directJpeg ? DisplayFrameProcessor.PrepareDirectJpegFrame(frame, device.Model)
                                : DisplayFrameProcessor.PrepareLiveHtmlFrame(frame, foreground, capturedBackground,
                                backgroundPlacement, device.Model, preserveTransparency: backgroundVideo,
                                rotation: frameRotation, crop: crop,
                                preferJpeg: device.Model.ProductId is 0x0070 or 0x0123);
                            previousScreenshot = frame;
                            return previousOverlay;
                        };
                    }

                    var video = prepared?.Stream;
                    using var session = OpenPlaybackDevice(device, key, rotation, token, hasVideo: backgroundVideo);
                    using var playbackTimer = captureFps >= 20
                        && (mode is 0 or 1 or SlideshowSourceMode)
                        ? new PlaybackTimer() : null;
                    // 逐帧轮播只在换图时发送，设备保留上一张图。
                    if (active.IsImageSlideshow)
                        capture = slideshow.ReadChangedFrameAsync;

                    // 网页首帧尚未就绪时可以先启动视频背景，用透明帧清掉设备残留的前景。
                    bool deferForeground = backgroundVideo && mode == 0 && startupHtmlFrame is null;
                    if (device.Model.ProductId != 0x0123 && (deferForeground || (mode == 0 && jpegCapture)))
                    {
                        byte[] blank = DisplayFrameProcessor.CreateBlankOverlay(device.Model);
                        token.ThrowIfCancellationRequested();
                        // 0070 切入 JPEG 前先初始化黑色主层，再清理 PNG 覆盖层，避免旧图层拖慢合成。
                        if (device.Model.ProductId == 0x0070 && jpegCapture)
                            session.ShowJpegFrame(DisplayFrameProcessor.PreparePlaybackJpeg(blank,
                                MediaLayout.Default, null, MediaLayout.Default, device.Model)
                                ?? throw new InvalidDataException("Could not prepare the initial JPEG frame."));
                        session.ShowOverlay(blank);
                    }
                    long sampleStarted = 0;
                    int sampleFrames = 0;
                    double pendingPreviewFps = 0;
                    byte[]? lastQueuedFrame = null;
                    var previewFrames = System.Threading.Channels.Channel.CreateBounded<byte[]>(
                        new System.Threading.Channels.BoundedChannelOptions(1)
                        {
                            SingleReader = true,
                            SingleWriter = true,
                            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest
                        });
                    Task? previewWorker = null;
                    Task<BitmapSource>? pendingWindow = null;
                    if (mode == 1 && !backgroundVideo)
                    {
                        capture = async cancellation =>
                        {
                            // 当前帧合成时预取下一帧，最多保留一个待完成的窗口捕捉。
                            BitmapSource bitmap = await (pendingWindow ?? CaptureWindowAsync(cancellation));
                            pendingWindow = CaptureWindowAsync(cancellation);
                            return DisplayFrameProcessor.PrepareLiveBitmapFrame(bitmap, foreground,
                                capturedBackground, backgroundPlacement, device.Model,
                                preserveTransparency: false, rotation: frameRotation, crop: crop,
                                preferJpeg: device.Model.ProductId == 0x0021 || preferFastJpeg);
                        };
                    }

                    // 以窗口原生尺寸捕捉，后续再应用布局或裁剪。
                    Task<BitmapSource> CaptureWindowAsync(CancellationToken cancellation) =>
                        window!.CaptureNextBitmapAsync(canvas, windowSurface!, cancellation, nativeSize: true);

                    try
                    {
                        previewWorker = Task.Run(PublishPreviewAsync, token);
                        await LiveScene.RunAsync(session, capture, video, videoFps, captureFps, token,
                            () => Dispatcher.Invoke(() =>
                            {
                                if (!token.IsCancellationRequested)
                                {
                                    SetPlaybackVideo(active, videoFps);
                                    SetPlaybackStatus(active, T("SendingLiveOutput"));
                                }
                            }),
                            frame =>
                            {
                                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                                bool measured = false;
                                if (sampleStarted == 0)
                                    sampleStarted = now;
                                else
                                {
                                    sampleFrames++;
                                    double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(sampleStarted, now).TotalSeconds;
                                    if (seconds >= 1)
                                    {
                                        Interlocked.Exchange(ref pendingPreviewFps, sampleFrames / seconds);
                                        measured = true;
                                        sampleStarted = now;
                                        sampleFrames = 0;
                                    }
                                }
                                // 预览忙时保留最新一帧已获设备确认的画面。
                                if (measured || !ReferenceEquals(lastQueuedFrame, frame))
                                {
                                    lastQueuedFrame = frame;
                                    previewFrames.Writer.TryWrite(frame);
                                }
                                return ValueTask.CompletedTask;
                            }, delayAfterFrame: false,
                            bufferLatestForeground: !active.IsImageSlideshow && (mode is 0 or 1 or SlideshowSourceMode),
                            adaptiveCapture: false,
                            deferForeground: deferForeground);

                        // 把可用的捕捉预览更新到界面线程。
                        async Task PublishPreviewAsync()
                        {
                            byte[]? lastPublishedFrame = null;
                            await foreach (byte[] frame in previewFrames.Reader.ReadAllAsync(token).ConfigureAwait(false))
                            {
                                if (!slideshowPreviews.TryGetValue(frame, out var preview)
                                    && !ReferenceEquals(lastPublishedFrame, frame)
                                    && string.Equals(Volatile.Read(ref previewDevicePath), device.Path, StringComparison.OrdinalIgnoreCase))
                                {
                                    try
                                    {
                                        preview = DisplayFrameProcessor.DecodeFrame(frame);
                                        preview.Freeze();
                                    }
                                    catch (Exception error) when (error is IOException or FormatException or NotSupportedException or ArgumentException)
                                    {
                                        continue;
                                    }
                                }

                                // 预览解码期间可能切换播放任务，提交前再次核对设备路径上的任务身份。
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    if (token.IsCancellationRequested
                                        || !ReferenceEquals(devicePlaybacks.GetValueOrDefault(device.Path), active))
                                        return;

                                    double measured = Interlocked.Exchange(ref pendingPreviewFps, 0);
                                    if (measured > 0)
                                    {
                                        active.SentFps = measured;
                                        if (ReferenceEquals(CurrentPlayback, active))
                                            UpdatePlaybackIndicators();
                                    }
                                    if (!active.HasOutput)
                                        SetPlaybackStatus(active, T("SendingLiveOutput"));
                                    if (!ReferenceEquals(active.Frame, frame) || active.Overlay != backgroundVideo)
                                        SetPlaybackFrame(active, frame, overlay: backgroundVideo, preview);
                                }, System.Windows.Threading.DispatcherPriority.Render, token).Task.ConfigureAwait(false);
                                lastPublishedFrame = frame;
                            }
                        }
                    }
                    finally
                    {
                        // 先关闭预览队列并等待读取退出，再等待仍在使用捕捉资源的预取任务。
                        previewFrames.Writer.TryComplete();
                        if (previewWorker is not null)
                            await previewWorker.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                        if (pendingWindow is not null)
                            await ((Task)pendingWindow).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                        if (pendingHtmlFrame is not null)
                            await ((Task)pendingHtmlFrame).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                        if (html is not null)
                            await html.DisposeAsync();
                    }
                });
                active.Token.ThrowIfCancellationRequested();
                SetPlaybackStatus(active, T("OutputComplete"));
            }
            finally
            {
                prepared?.Dispose();
                CryptographicOperations.ZeroMemory(key);
            }
        }, T("PreparingLiveInput"), resumeOnReconnect: true);
    }

    /// <summary>将实时画面编码成分块视频，避免每个画面单独等待 USB 应答。</summary>
    private async Task<bool> TryPlayBufferedForegroundAsync(DevicePlayback active, string source,
        WindowCapture? window, byte[] key, byte rotation, byte fps)
    {
        try
        {
            await Task.Run(async () =>
            {
                var token = active.Token;
                var device = active.Device;
                var canvas = device.Model;
                // 6.8 寸原生 1080 × 2320，0.75 缩放仍能在 20 FPS 下保留更多文字细节。
                double pixelScale = BufferedForegroundPixelScale(device.Model);
                const int bitrateKbps = 1200;
                using var session = OpenPlaybackDevice(device, key, rotation, token, hasVideo: true);
                using var timer = new PlaybackTimer();
                if (window is not null)
                {
                    using var surface = new WindowCapture.CaptureSurface();
                    BitmapSource? previousFrame = null;
                    await PlayForegroundVideoAsync<BitmapSource>(active, session, async cancellation =>
                    {
                        previousFrame = await window.CaptureNextBitmapAsync(canvas, surface, cancellation,
                            nativeSize: true, previousFrame: previousFrame);
                        return previousFrame;
                    }, bitmap => DisplayFrameProcessor.PrepareForegroundVideoFrame(bitmap, MediaLayout.Default,
                        null, MediaLayout.Default, canvas, rotation: 0, crop: null), fps,
                        T("SendingLiveOutput"), jpegInput: true, outputCanvas: canvas, bitrateKbps: bitrateKbps);
                    return;
                }

                HtmlCapture? browser = null;
                bool push = true;
                try
                {
                    await PlayForegroundVideoAsync(active, session, ReadFrameAsync, static frame => frame, fps,
                        T("SendingLiveOutput"), jpegInput: true, outputCanvas: canvas, bitrateKbps: bitrateKbps);
                }
                finally
                {
                    if (browser is not null)
                        await browser.DisposeAsync();
                }

                async ValueTask<byte[]?> ReadFrameAsync(CancellationToken cancellation)
                {
                    browser ??= await HtmlCapture.OpenAsync(source, canvas, cancellation, fastCapture: true, pixelScale);
                    if (!push)
                        return await browser.ReadJpegFrameAsync(cancellation);

                    try
                    {
                        return await browser.ReadJpegScreencastAsync(cancellation, quality: 75,
                            repeatAfter: TimeSpan.FromMilliseconds(1000d / fps));
                    }
                    catch (Exception error) when (!cancellation.IsCancellationRequested
                        && (error is TimeoutException or IOException))
                    {
                        push = false;
                        (browser, byte[]? frame) = await UseScreenshotFallbackAsync(browser, source, canvas,
                            cancellation, jpeg: true, fastCapture: true, pixelScale: pixelScale);
                        return frame;
                    }
                }
            }, active.Token);
            return true;
        }
        catch (FfmpegNotFoundException)
        {
            return false;
        }
        catch (Exception error) when (!active.HasOutput && !active.Token.IsCancellationRequested
            && (error is FfmpegProcessException or DeviceResponseException))
        {
            return false;
        }
    }

    /// <summary>判断实时前景是否优先使用分块 H.264 输出。</summary>
    internal static bool UsesBufferedForegroundVideo(DisplayModel model, int mode) =>
        model.ProductId is 0x0021 or 0x0046 or 0x0070
        || (model.ProductId == 0x0068 && mode == 0);

    /// <summary>确定分块 H.264 路径的浏览器像素缩放。</summary>
    internal static double BufferedForegroundPixelScale(DisplayModel model) =>
        model.ProductId == 0x0068 ? 0.75 : 0.5;

    /// <summary>需要与背景合成时保留网页 Alpha，仅在不需要背景时采用 JPEG 捕捉。</summary>
    internal static bool CanCaptureHtmlAsJpeg(bool directJpeg, bool preferFastJpeg,
        bool hasVideoBackground, bool hasImageBackground) =>
        !hasVideoBackground && !hasImageBackground && (directJpeg || preferFastJpeg);

    /// <summary>推帧不可用时转为按需截图，并返回可继续使用的捕捉实例。</summary>
    private static async Task<(HtmlCapture Capture, byte[]? Frame)> UseScreenshotFallbackAsync(
        HtmlCapture capture, string source, DisplayModel canvas, CancellationToken cancellation,
        bool jpeg = false, bool fastCapture = false, double pixelScale = 1, byte outputRotation = 0)
    {
        try
        {
            await capture.StopScreencastAsync(cancellation);
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested && (error is IOException or TimeoutException))
        {
            // Chromium 可能拒绝推帧控制命令，此时普通截图仍可工作。
        }

        try
        {
            return (capture, await ReadAsync(capture));
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested && (error is IOException or TimeoutException))
        {
            // 只要原页面还能截图，就继续复用它。
            await capture.DisposeAsync();
            var replacement = await HtmlCapture.OpenAsync(source, canvas, cancellation, fastCapture, pixelScale, outputRotation);
            try
            {
                return (replacement, await ReadAsync(replacement));
            }
            catch
            {
                await replacement.DisposeAsync();
                throw;
            }
        }

        // 按输出格式选择 JPEG 或 PNG 截图。
        ValueTask<byte[]?> ReadAsync(HtmlCapture browser) => jpeg
            ? browser.ReadJpegFrameAsync(cancellation)
            : browser.ReadFrameAsync(cancellation);
    }

    /// <summary>实时编码前景、发送至设备并更新本地预览。</summary>
    private async Task PlayForegroundVideoAsync<TFrame>(DevicePlayback active, DisplaySession session,
        Func<CancellationToken, ValueTask<TFrame?>> capture, Func<TFrame, byte[]> prepareFrame,
        byte fps, string playingStatus, bool jpegInput, DisplayModel? outputCanvas,
        DisplayModel? rawInputCanvas = null, bool reuseFrameBuffers = false, int bitrateKbps = 3000) where TFrame : class
    {
        var token = active.Token;
        byte[]? displayedVideoFrame = null;
        bool started = false;
        long lastPreview = 0;
        long sampleStarted = 0;
        int sampleFrames = 0;
        Task? rateUpdate = null;
        Task? preview = null;
        Task? startupStatus = null;
        try
        {
            token.ThrowIfCancellationRequested();
            // 0123 已在打开会话时完成无应答清层，其他型号等待 PNG 清层应答。
            if (active.Device.Model.ProductId != 0x0123)
                session.ShowOverlay(DisplayFrameProcessor.CreateBlankOverlay(active.Device.Model));
            await LiveVideoEncoder.RunAsync(session, capture, frame =>
            {
                byte[] prepared = prepareFrame(frame);
                UpdateVideoPreview(prepared);
                return prepared;
            }, fps, token, onStarted: () =>
            {
                startupStatus = Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested)
                        return;
                    SetPlaybackStatus(active, playingStatus);
                    SetPlaybackVideo(active, fps);
                }, System.Windows.Threading.DispatcherPriority.Normal, token).Task;
                Volatile.Write(ref started, true);
            }, jpegInput: jpegInput, outputCanvas: outputCanvas, rawInputCanvas: rawInputCanvas,
                bitrateKbps: bitrateKbps, onInputWrite: _ =>
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (sampleStarted == 0)
                {
                    sampleStarted = now;
                    return;
                }

                sampleFrames++;
                double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(sampleStarted, now).TotalSeconds;
                if (seconds < 1 || rateUpdate is { IsCompleted: false })
                    return;

                // 按实际写入编码器的帧计速。界面仍有上次更新待处理时，延长当前采样窗口。
                double measured = sampleFrames / seconds;
                sampleStarted = now;
                sampleFrames = 0;
                rateUpdate = Dispatcher.InvokeAsync(() =>
                {
                    active.EncoderInputFps = measured;
                    if (ReferenceEquals(CurrentPlayback, active))
                        UpdatePlaybackIndicators();
                }, System.Windows.Threading.DispatcherPriority.Background, token).Task;
            });
        }
        finally
        {
            if (startupStatus is not null)
                await startupStatus.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (preview is not null)
                await preview.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (rateUpdate is not null)
                await rateUpdate.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        // 限制预览更新频率，并避免多个预览任务同时写入界面。
        void UpdateVideoPreview(byte[]? frame)
        {
            if (Volatile.Read(ref started) && frame is not null
                && (!reuseFrameBuffers || string.Equals(Volatile.Read(ref previewDevicePath), active.Device.Path,
                    StringComparison.OrdinalIgnoreCase))
                && (reuseFrameBuffers || !ReferenceEquals(Volatile.Read(ref displayedVideoFrame), frame))
                && (preview is null || preview.IsCompleted)
                && System.Diagnostics.Stopwatch.GetElapsedTime(lastPreview).TotalMilliseconds >= 100)
            {
                lastPreview = System.Diagnostics.Stopwatch.GetTimestamp();
                // Dispatcher 异步读取预览像素，不能持有循环复用的编码缓冲区。
                _ = preview?.Exception;
                byte[] snapshot = reuseFrameBuffers ? (byte[])frame.Clone() : frame;
                preview = PublishVideoPreviewAsync(snapshot);
            }
        }

        // 在界面线程发布编码过程的本地预览帧。
        async Task PublishVideoPreviewAsync(byte[] snapshot)
        {
            // 预览 JPEG 在后台生成，UI 线程只接收已编码的画面。
            byte[] image = rawInputCanvas is { } raw
                ? await Task.Run(() => DisplayFrameProcessor.EncodeVideoPreview(snapshot, raw), token).ConfigureAwait(false)
                : snapshot;
            await Dispatcher.InvokeAsync(() =>
            {
                SetPlaybackFrame(active, image, overlay: false);
                Volatile.Write(ref displayedVideoFrame, snapshot);
            }, cancellationToken: token, priority: System.Windows.Threading.DispatcherPriority.Background).Task.ConfigureAwait(false);
        }
    }

    /// <summary>在实时播放期间请求 1 毫秒定时精度，并在退出时释放请求。</summary>
    private sealed class PlaybackTimer : IDisposable
    {
        /// <summary>记录是否已成功请求更细的 Windows 定时器周期。</summary>
        private readonly bool requested = TimeBeginPeriod(1) == 0;

        /// <summary>撤销当前播放申请的 Windows 定时器周期。</summary>
        public void Dispose()
        {
            if (requested)
                TimeEndPeriod(1);
        }
    }

    /// <summary>请求 Windows 提高计时器分辨率。</summary>
    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    /// <summary>撤销此前对 Windows 计时器分辨率的请求。</summary>
    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);
}
