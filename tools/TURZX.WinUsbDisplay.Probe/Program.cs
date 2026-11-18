using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TURZX.WinUsbDisplay.Windows;
using TURZX.WinUsbDisplay.Media;

namespace TURZX.WinUsbDisplay.Probe;

/// <summary>运行设备、捕捉和媒体发送诊断。</summary>
internal static class Program
{
    /// <summary>指示浏览器捕捉测试是否关闭默认帧率限制。</summary>
    private static bool UncapBrowserFrames =>
        Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_BROWSER_CAP") != "1";

    /// <summary>解析探针参数并启动指定诊断。</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        bool preciseTimer = (args.FirstOrDefault() == "--parallel-window" ||
            Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_PRECISE_TIMER") == "1")
            && TimeBeginPeriod(1) == 0;
        try
        {
            return Run(args);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException
            or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        finally
        {
            if (preciseTimer)
                TimeEndPeriod(1);
        }
    }

    /// <summary>请求 Windows 使用指定的定时器周期。</summary>
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    /// <summary>撤销对 Windows 定时器周期的请求。</summary>
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);

    /// <summary>执行所选探针任务并报告结果。</summary>
    private static int Run(string[] args, bool parallelWindow = false)
    {
        if (args is ["--parallel-window", var title, .. var products] && products.Length >= 2)
        {
            if (products.Distinct(StringComparer.OrdinalIgnoreCase).Count() != products.Length ||
                products.Any(product => product.Length != 4 || !ushort.TryParse(product,
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)))
            {
                Console.Error.WriteLine("Specify two or more distinct four-digit hexadecimal PIDs.");
                return 2;
            }

            // 在同一进程运行现有窗口路径，让各设备共享窗口捕捉缓存。
            Task<int>[] measurements = products.Select(product => Task.Run(() =>
                Run([product, "--window", title], parallelWindow: true))).ToArray();
            return Task.WhenAll(measurements).GetAwaiter().GetResult().Max();
        }
        if (args is ["--verify-video-blend"])
            return VerifyVideoBlend();
        if (args is ["--measure-window-buffer"])
            return MeasureWindowBuffer();
        if (args.Length is < 1 or > 6 || !ushort.TryParse(args[0], NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out ushort pid))
        {
            Console.Error.WriteLine("Usage: TURZX.WinUsbDisplay.Probe <hex-pid> [image-path] [--send|--benchmark|--benchmark-jpeg|--still]");
            Console.Error.WriteLine("       TURZX.WinUsbDisplay.Probe <hex-pid> <--capture|--live> <html-path-or-url>");
            Console.Error.WriteLine("       TURZX.WinUsbDisplay.Probe <hex-pid> <--window|--window-video> <exact-window-title>");
            Console.Error.WriteLine("       TURZX.WinUsbDisplay.Probe --parallel-window <exact-window-title> <hex-pid> <hex-pid> [...]");
            Console.Error.WriteLine("       TURZX.WinUsbDisplay.Probe <hex-pid> --slides <image-directory>");
            Console.Error.WriteLine("       TURZX.WinUsbDisplay.Probe <hex-pid> --still-video <image-path> [output-percent]");
            Console.Error.WriteLine("       TURZX.WinUsbDisplay.Probe <hex-pid> --slides-video <image-directory> [bitrate-kbps] [output-percent] [png|jpeg]");
            Console.Error.WriteLine("       TURZX.WinUsbDisplay.Probe <hex-pid> --video <30-fps-annex-b-file>");
            Console.Error.WriteLine("       TURZX.WinUsbDisplay.Probe <hex-pid> --live-video <html-path-or-url> [pixel-scale] [block-KiB] [output-percent]");
            Console.Error.WriteLine("       TURZX.WinUsbDisplay.Probe <hex-pid> --composite-video <html-path-or-url> <background-image> [pixel-scale]");
            Console.Error.WriteLine("       TURZX.WinUsbDisplay.Probe <hex-pid> --benchmark-jpeg <scale-percent>");
            return 2;
        }

        byte rotation = 0;
        if (Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_ROTATION") is { } angle
            && (!byte.TryParse(angle, out rotation) || rotation > 3))
        {
            Console.Error.WriteLine("Probe rotation must be a quarter-turn count between 0 and 3.");
            return 2;
        }
        if (args is [_, "--capture", var captureSource])
        {
            var model = DisplayModel.Known.FirstOrDefault(model => model.ProductId == pid);
            if (model is null)
            {
                Console.Error.WriteLine($"Unsupported PID {pid:X4}.");
                return 2;
            }

            try
            {
                MeasureCaptureAsync(model, captureSource, rotation: rotation).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception ex)
            {
                ReportFailure(pid, ex);
                return 1;
            }
        }

        bool send = args.Contains("--send");
        bool benchmark = args.Contains("--benchmark");
        bool jpegBenchmark = args.Contains("--benchmark-jpeg");
        int jpegScale = 100;
        bool scaledJpeg = args is [_, "--benchmark-jpeg", _];
        if (scaledJpeg && (!int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out jpegScale)
            || jpegScale is < 10 or > 100))
        {
            Console.Error.WriteLine("JPEG scale must be between 10 and 100 percent.");
            return 2;
        }
        bool still = args.Contains("--still");
        string? imagePath = scaledJpeg ? null : args.Skip(1).FirstOrDefault(value =>
            value is not ("--send" or "--benchmark" or "--benchmark-jpeg" or "--still"));
        var matches = DisplayDevice.Discover(DisplaySettings.InterfaceGuid)
            .Where(device => device.Model.ProductId == pid).ToArray();
        if (matches.Length != 1)
        {
            Console.Error.WriteLine($"Expected exactly one device with PID {pid:X4}; found {matches.Length}.");
            return 2;
        }

        var device = matches[0];
        if (args is [_, "--video", var videoPath])
        {
            byte[]? videoKey = null;
            try
            {
                using var video = videoPath == "-" ? Console.OpenStandardInput() : File.OpenRead(videoPath);
                videoKey = CommandKeyStore.Read();
                using var session = device.Open(videoKey);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var clock = Stopwatch.StartNew();
                session.StopPlayback();
                session.ShowDefaultClock(false);
                session.SetVideoClockSync(false);
                session.PlayVideo(video, 30, loop: false, timeout.Token,
                    () => Console.WriteLine($"First video block acknowledged at {clock.Elapsed.TotalSeconds:F2}s."),
                    resetPlayback: false);
                Console.WriteLine($"PID {pid:X4}: H.264 transfer completed in {clock.Elapsed.TotalSeconds:F2}s.");
                return 0;
            }
            catch (Exception ex)
            {
                ReportFailure(pid, ex);
                return 1;
            }
            finally
            {
                if (videoKey is not null)
                    CryptographicOperations.ZeroMemory(videoKey);
            }
        }
        bool composite = args.Length is 4 or 5 && args[1] is "--composite-video" or "--composite";
        bool liveVideo = args.Length >= 3 && args[1] == "--live-video";
        double pixelScale = 0.5;
        if (composite && args.Length == 5
            && (!double.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out pixelScale)
                || !double.IsFinite(pixelScale) || pixelScale is <= 0 or > 1))
        {
            Console.Error.WriteLine("Pixel scale must be greater than zero and at most one.");
            return 2;
        }
        bool slidesVideo = args.Length >= 3 && args[1] == "--slides-video";
        bool pngSlides = slidesVideo && args.Length == 6 && args[5] == "png";
        if (slidesVideo && args.Length == 6 && args[5] is not ("png" or "jpeg"))
        {
            Console.Error.WriteLine("Slideshow input must be png or jpeg.");
            return 2;
        }
        bool windowVideo = args.Length is 3 or 4 && args[1] == "--window-video";
        bool stillVideo = args.Length is 3 or 4 && args[1] == "--still-video";
        int bitrateKbps = 3000;
        int outputPercent = 100;
        if ((windowVideo || stillVideo) && args.Length == 4 &&
            (!int.TryParse(args[3], out outputPercent) || outputPercent is < 25 or > 100))
        {
            Console.Error.WriteLine("Video output scale must be between 25 and 100 percent.");
            return 2;
        }
        if (slidesVideo && args.Length >= 4 &&
            (!int.TryParse(args[3], out bitrateKbps) || bitrateKbps is < 500 or > 10000))
        {
            Console.Error.WriteLine("Bitrate must be between 500 and 10000 kbps.");
            return 2;
        }
        if (slidesVideo && args.Length >= 5 &&
            (!int.TryParse(args[4], out outputPercent) || outputPercent is < 25 or > 100))
        {
            Console.Error.WriteLine("Video output scale must be between 25 and 100 percent.");
            return 2;
        }
        if (args.Length >= 4 && !composite && !slidesVideo && !windowVideo && !stillVideo && (!liveVideo
            || !double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out pixelScale)
            || !double.IsFinite(pixelScale) || pixelScale is <= 0 or > 1))
        {
            Console.Error.WriteLine("Pixel scale must be greater than zero and at most one.");
            return 2;
        }
        int blockKiB = LiveVideoEncoder.DefaultMaximumBlockBytes / 1024;
        if (liveVideo && args.Length >= 5 && (!int.TryParse(args[4], out blockKiB) || blockKiB is < 1 or > 4096))
        {
            Console.Error.WriteLine("Block size must be between 1 and 4096 KiB.");
            return 2;
        }
        if (liveVideo && args.Length == 6 &&
            (!int.TryParse(args[5], out outputPercent) || outputPercent is < 25 or > 100))
        {
            Console.Error.WriteLine("Video output scale must be between 25 and 100 percent.");
            return 2;
        }

        if (composite || liveVideo || slidesVideo || windowVideo || stillVideo || args is [_, "--live" or "--window" or "--slides", _])
        {
            string source = args[2];
            try
            {
                byte[] liveKey = CommandKeyStore.Read();
                try
                {
                    using var session = device.Open(liveKey);
                    if (composite)
                        MeasureCompositeAsync(device.Model, session, source, args[3], blockKiB, pixelScale,
                            video: args[1] == "--composite-video").GetAwaiter().GetResult();
                    else if (args[1] == "--live-video")
                        MeasureLiveVideoAsync(device.Model, session, source, pixelScale, blockKiB, outputPercent, rotation).GetAwaiter().GetResult();
                    else if (args[1] is "--window" or "--window-video")
                    {
                        var windows = WindowCapture.List(0).Where(window => window.Title == source).ToArray();
                        if (windows.Length != 1)
                            throw new IOException($"Expected exactly one visible window with that title; found {windows.Length}.");
                        using var windowSurface = new WindowCapture.CaptureSurface();
                        if (Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_WINDOW_SNAPSHOT") == "1")
                        {
                            var first = windows[0].CaptureBitmapAsync(device.Model, windowSurface, CancellationToken.None)
                                .GetAwaiter().GetResult();
                            byte[] firstHash = HashBitmap(first);
                            Thread.Sleep(250);
                            var second = windows[0].CaptureBitmapAsync(device.Model, windowSurface, CancellationToken.None)
                                .GetAwaiter().GetResult();
                            windowSurface.Dispose();
                            if (!firstHash.AsSpan().SequenceEqual(HashBitmap(first)))
                                throw new IOException("The previous window frame changed after the GDI surface was reused.");
                            if (firstHash.AsSpan().SequenceEqual(HashBitmap(second)))
                                throw new IOException("The test window did not change between captures.");
                            Console.WriteLine("Window snapshots remain distinct after the GDI surface is disposed.");
                            return 0;
                        }
                        TimeSpan windowSample = GetLiveSampleDuration(
                            defaultSeconds: windowVideo && outputPercent != 100 ? 60 : 10);
                        using var timeout = new CancellationTokenSource(
                            (windowVideo ? windowSample * 3 : windowSample) + TimeSpan.FromSeconds(35));
                        bool rawWindow = windowVideo && outputPercent == 50;
                        bool nativeWindow = parallelWindow ||
                            Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_NATIVE_WINDOW") == "1";
                        bool pipeline = parallelWindow ||
                            Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_WINDOW_PIPELINE") == "1";
                        byte windowVideoFps = ReadProbeCaptureFps();
                        int windowVideoFrames = (int)windowSample.TotalSeconds * windowVideoFps;
                        if (windowVideo && Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_VIDEO_BACKGROUND")
                            is { Length: > 0 } windowVideoPath)
                        {
                            var compositeCanvas = Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_COMPOSITE_OUTPUT_PERCENT") is { } scaleText
                                && int.TryParse(scaleText, out int percent) && percent is >= 20 and <= 100
                                ? ScaleVideoCanvas(device.Model, percent) : HostCompositeCanvas(device.Model);
                            MeasureHostCompositeAsync(device.Model, session, compositeCanvas,
                                null, windowVideoPath, blockKiB, (int)windowSample.TotalSeconds * 30, timeout.Token,
                                async cancellation => await windows[0].CaptureBitmapAsync(device.Model, windowSurface,
                                    cancellation, nativeSize: true), static bitmap => bitmap).GetAwaiter().GetResult();
                            return 0;
                        }
                        double windowCaptureMs = 0;
                        double windowRenderMs = 0;
                        int windowFrames = 0;
                        bool windowJpeg = windowVideo || Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_JPEG") == "1"
                            || (!parallelWindow && device.Model.ProductId == 0x0021
                                && Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_0021_JPEG") == "1");
                        // 编码刚捕捉的窗口帧并记录处理耗时。
                        byte[] PrepareWindowFrame(BitmapSource bitmap, long started)
                        {
                            if (rawWindow)
                            {
                                long captured = Stopwatch.GetTimestamp();
                                byte[] pixels = DisplayFrameProcessor.PrepareForegroundVideoPixels(bitmap,
                                    MediaLayout.Default, null, MediaLayout.Default, device.Model, rotation: 0, crop: null);
                                windowCaptureMs += Stopwatch.GetElapsedTime(started, captured).TotalMilliseconds;
                                windowRenderMs += Stopwatch.GetElapsedTime(captured).TotalMilliseconds;
                                windowFrames++;
                                return pixels;
                            }
                            if (windowJpeg)
                                return DisplayFrameProcessor.PrepareForegroundVideoFrame(bitmap,
                                    MediaLayout.Default, null, MediaLayout.Default, device.Model, rotation: 0, crop: null,
                                    outputCanvas: windowVideo ? null : device.Model);
                            long captureCompleted = Stopwatch.GetTimestamp();
                            var frame = DisplayFrameProcessor.PrepareLiveBitmapFrame(bitmap,
                                MediaLayout.Default, null, MediaLayout.Default, device.Model,
                                preserveTransparency: false, rotation: 0, crop: null,
                                preferJpeg: parallelWindow && device.Model.ProductId == 0x0021);
                            windowCaptureMs += Stopwatch.GetElapsedTime(started, captureCompleted).TotalMilliseconds;
                            windowRenderMs += Stopwatch.GetElapsedTime(captureCompleted).TotalMilliseconds;
                            windowFrames++;
                            return frame;
                        }
                        // 针对指定窗口测试实时捕捉及送屏。
                        async ValueTask<byte[]?> CaptureWindow(CancellationToken cancellation)
                        {
                            long started = Stopwatch.GetTimestamp();
                            var bitmap = await windows[0].CaptureBitmapAsync(device.Model, windowSurface, cancellation, nativeWindow);
                            return PrepareWindowFrame(bitmap, started);
                        }
                        byte[]? windowVideoPixels = null;
                        if (rawWindow && pipeline)
                            MeasureVideoFramesAsync<BitmapSource>(device.Model, session,
                                async cancellation => await windows[0].CaptureBitmapAsync(device.Model, windowSurface, cancellation, nativeWindow),
                                timeout.Token, jpegInput: false, blockKiB, frameCount: windowVideoFrames,
                                prepareFrame: bitmap => windowVideoPixels = DisplayFrameProcessor.PrepareForegroundVideoPixels(bitmap,
                                    MediaLayout.Default, null, MediaLayout.Default, device.Model, rotation: 0, crop: null,
                                    destination: windowVideoPixels),
                                rawInputCanvas: ScaleVideoCanvas(device.Model, outputPercent), fps: windowVideoFps,
                                bitrateKbps: ReadProbeLiveBitrate()).GetAwaiter().GetResult();
                        else if (windowVideo)
                            MeasureVideoFramesAsync(device.Model, session, CaptureWindow, timeout.Token,
                                jpegInput: !rawWindow, blockKiB, frameCount: windowVideoFrames,
                                outputCanvas: ScaleVideoCanvas(device.Model, outputPercent),
                                rawInputCanvas: rawWindow ? ScaleVideoCanvas(device.Model, outputPercent) : null,
                                fps: windowVideoFps, bitrateKbps: ReadProbeLiveBitrate()).GetAwaiter().GetResult();
                        else if (pipeline)
                        {
                            Task<(BitmapSource Bitmap, TimeSpan Elapsed)>? pending = null;
                            try
                            {
                                MeasureLiveAsync(device.Model, session, async cancellation =>
                                {
                                    var (bitmap, elapsed) = await (pending ?? StartCapture(cancellation));
                                    pending = StartCapture(cancellation);
                                    long started = Stopwatch.GetTimestamp();
                                    byte[] frame = PrepareWindowFrame(bitmap, started);
                                    if (!windowJpeg)
                                        windowCaptureMs += elapsed.TotalMilliseconds;
                                    return frame;
                                }, timeout.Token).GetAwaiter().GetResult();
                            }
                            finally
                            {
                                ((Task?)pending)?.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing).GetAwaiter().GetResult();
                            }

                            // 启动指定来源的捕捉任务。
                            async Task<(BitmapSource, TimeSpan)> StartCapture(CancellationToken cancellation)
                            {
                                long started = Stopwatch.GetTimestamp();
                                BitmapSource bitmap = await windows[0].CaptureBitmapAsync(device.Model, windowSurface, cancellation, nativeWindow);
                                return (bitmap, Stopwatch.GetElapsedTime(started));
                            }
                        }
                        else
                            MeasureLiveAsync(device.Model, session, CaptureWindow, timeout.Token).GetAwaiter().GetResult();
                        if (windowFrames > 0)
                            Console.WriteLine(FormattableString.Invariant(
                                $"Window capture: {windowCaptureMs / windowFrames:F2} ms/frame; frame preparation: {windowRenderMs / windowFrames:F2} ms/frame ({windowFrames} captured frames)."));
                    }
                    else if (args[1] == "--still-video")
                    {
                        if (Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_VIDEO_BACKGROUND") is { Length: > 0 } stillBackground)
                        {
                            byte[] image = File.ReadAllBytes(source);
                            MeasureImageVideoCompositeAsync(device.Model, session, stillBackground, blockKiB,
                                _ => ValueTask.FromResult<byte[]?>(image)).GetAwaiter().GetResult();
                            return 0;
                        }
                        byte[] stillFrame = DisplayFrameProcessor.PreparePlaybackImage(File.ReadAllBytes(source),
                            MediaLayout.Default, null, MediaLayout.Default, device.Model);
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(outputPercent == 100 ? 45 : 110));
                        MeasureVideoFramesAsync(device.Model, session, _ => ValueTask.FromResult<byte[]?>(stillFrame),
                            timeout.Token, jpegInput: stillFrame.AsSpan().StartsWith<byte>([0xff, 0xd8, 0xff]),
                            blockKiB, frameCount: outputPercent == 100 ? 300 : 1800,
                            outputCanvas: ScaleVideoCanvas(device.Model, outputPercent)).GetAwaiter().GetResult();
                    }
                    else if (args[1] is "--slides" or "--slides-video")
                    {
                        string[] paths = Directory.GetFiles(source).Where(path =>
                            Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg").ToArray();
                        if (paths.Length < 2)
                            throw new IOException("The slideshow requires at least two images.");
                        MeasureSlidesAsync(device.Model, session, paths, args[1] == "--slides-video",
                            blockKiB, bitrateKbps, outputPercent, pngSlides, rotation).GetAwaiter().GetResult();
                    }
                    else
                        MeasureCaptureAsync(device.Model, source, session, rotation).GetAwaiter().GetResult();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(liveKey);
                }
                return 0;
            }
            catch (Exception ex)
            {
                ReportFailure(pid, ex);
                return 1;
            }
        }
        byte[] input = imagePath is null ? CreateChart() : File.ReadAllBytes(imagePath);
        byte[] frame = still
            ? DisplayFrameProcessor.PreparePlaybackImage(input, MediaLayout.Default, null, MediaLayout.Default, device.Model)
            : DisplayFrameProcessor.Prepare(input, device.Model, preserveTransparency: benchmark);
        using var stream = new MemoryStream(frame);
        var bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        if (bitmap.PixelWidth != device.Model.Width || bitmap.PixelHeight != device.Model.Height)
            throw new InvalidDataException("Prepared frame dimensions do not match the device.");
        if (jpegBenchmark)
        {
            BitmapSource jpegBitmap = scaledJpeg && jpegScale != 100
                ? new TransformedBitmap(bitmap, new ScaleTransform(jpegScale / 100d, jpegScale / 100d))
                : bitmap;
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(jpegBitmap));
            Console.WriteLine($"JPEG raster: {jpegBitmap.PixelWidth}x{jpegBitmap.PixelHeight}.");
            using var output = new MemoryStream();
            encoder.Save(output);
            frame = output.ToArray();
        }
        byte command = frame[0] == 0x89 ? (byte)102 : (byte)101;
        Console.WriteLine($"PID {pid:X4}: {bitmap.PixelWidth}x{bitmap.PixelHeight}; source={input.Length} B; frame={frame.Length} B; command={command}.");

        byte[]? key = null;
        try
        {
            key = CommandKeyStore.Read();
            using var session = device.Open(key);
            Step("GET_FIRMWARE_VERSION", () => session.ReadVerifiedQueryResponse(10));
            if (still)
            {
                TimeSpan sampleDuration = GetLiveSampleDuration();
                using var timeout = new CancellationTokenSource(sampleDuration + TimeSpan.FromSeconds(35));
                var clock = Stopwatch.StartNew();
                int frames = 0;
                long previousAck = 0;
                var gaps = new List<double>();
                var transfers = new List<double>();
                try
                {
                    Task.Run(() => session.PlayImage(frame, 30, timeout.Token, onStarted: null, onFrameSent: () =>
                    {
                        long now = Stopwatch.GetTimestamp();
                        if (previousAck != 0)
                            gaps.Add(Stopwatch.GetElapsedTime(previousAck, now).TotalMilliseconds);
                        previousAck = now;
                        if (++frames == 1)
                            clock.Restart();
                        if (clock.Elapsed >= sampleDuration)
                        {
                            clock.Stop();
                            timeout.Cancel();
                        }
                    }, onTransfer: (write, reply) => transfers.Add((write + reply).TotalMilliseconds)))
                        .GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (!clock.IsRunning) { }
                Console.WriteLine(FormattableString.Invariant(
                    $"PID {pid:X4}: {(frames - 1) / clock.Elapsed.TotalSeconds:F2} still-image FPS."));
                gaps.Sort();
                transfers.Sort();
                Console.WriteLine(FormattableString.Invariant(
                    $"Acknowledgement gap p95: {gaps[(int)Math.Ceiling(gaps.Count * 0.95) - 1]:F2} ms; USB round trip p95: {transfers[(int)Math.Ceiling(transfers.Count * 0.95) - 1]:F2} ms, max: {transfers[^1]:F2} ms."));
            }
            if (benchmark || jpegBenchmark)
            {
                session.StopPlayback();
                session.ShowDefaultClock(false);
                var length = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(length, frame.Length);
                var elapsed = Stopwatch.StartNew();
                int frames = 0;
                do
                {
                    if (jpegBenchmark)
                        session.ExchangeCommand(command, length, frame);
                    else
                        session.ShowOverlay(frame);
                    frames++;
                } while (elapsed.Elapsed < TimeSpan.FromSeconds(10));
                Console.WriteLine($"PID {pid:X4}: {frames / elapsed.Elapsed.TotalSeconds:F2} transfers/s, {frames} frame transfers with a USB reply.");
            }
            if (send)
            {
                Step("STOP_H264_STREAM", () => session.ExchangeCommand(123));
                Step("STOP_DEVICE_CLOCK", () => session.ExchangeCommand(52));
                var length = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(length, frame.Length);
                Step(command == 102 ? "DISPLAY_PNG_OVERLAY" : "DISPLAY_JPEG",
                    () => session.ExchangeCommand(command, length, frame));
                Step("GET_FIRMWARE_VERSION after image", () => session.ReadVerifiedQueryResponse(10));
            }
            return 0;
        }
        catch (Exception ex)
        {
            ReportFailure(pid, ex);
            return 1;
        }
        finally
        {
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>将诊断失败及异常信息写入输出。</summary>
    private static void ReportFailure(ushort pid, Exception error)
    {
        if (error is DisplayOpenException open)
            Console.Error.WriteLine($"PID {pid:X4}: {open.Failure} (Win32 {open.NativeErrorCode}). {open.Message}");
        else
            Console.Error.WriteLine($"PID {pid:X4}: {error.Message}");
    }

    /// <summary>测量实时前景视频编码和 USB 送屏。</summary>
    private static async Task MeasureLiveVideoAsync(DisplayModel model, DisplaySession session, string source,
        double pixelScale, int blockKiB, int outputPercent, byte rotation)
    {
        byte fps = ReadProbeCaptureFps();
        int frameCount = (int)GetLiveSampleDuration(defaultSeconds: outputPercent == 100 ? 10 : 60).TotalSeconds * fps;
        int bitrate = ReadProbeLiveBitrate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(frameCount / 15d + 40));
        await using var html = await HtmlCapture.OpenAsync(source, model.ForRotation(rotation), timeout.Token, fastCapture: UncapBrowserFrames, pixelScale);
        Console.WriteLine(FormattableString.Invariant($"Browser pixel scale: {pixelScale:P0}."));
        byte[]? previous = null;
        int reads = 0;
        int reused = 0;
        await MeasureVideoFramesAsync(model, session, async token =>
        {
            byte[]? frame = await ReadBrowserJpegAsync(html, token);
            if (frame is not null)
            {
                reads++;
                if (ReferenceEquals(previous, frame))
                    reused++;
                previous = frame;
            }
            return frame;
        }, timeout.Token,
            jpegInput: true, blockKiB, frameCount: frameCount, bitrateKbps: bitrate,
            outputCanvas: ScaleVideoCanvas(model, outputPercent), outputRotation: rotation, fps: fps);
        Console.WriteLine($"Browser capture: {reads} reads ({reused} reused).");
    }

    /// <summary>测量前景和背景组合后的实时输出。</summary>
    private static async Task MeasureCompositeAsync(DisplayModel model, DisplaySession session,
        string source, string backgroundPath, int blockKiB, double pixelScale, bool video)
    {
        TimeSpan videoDuration = GetLiveSampleDuration(defaultSeconds: 60);
        using var timeout = new CancellationTokenSource(video ? videoDuration * 3 + TimeSpan.FromSeconds(35)
            : GetLiveSampleDuration() + TimeSpan.FromSeconds(35));
        var background = DisplayFrameProcessor.DecodeFrame(File.ReadAllBytes(backgroundPath));
        background.Freeze();
        await using var html = await HtmlCapture.OpenAsync(source, model, timeout.Token, fastCapture: UncapBrowserFrames, pixelScale);
        Console.WriteLine(FormattableString.Invariant($"Browser pixel scale: {pixelScale:P0}."));
        double composeMs = 0;
        int composedFrames = 0;
        int reusedFrames = 0;
        byte[]? previousScreenshot = null;
        byte[]? previousPixels = null;
        var crop = new Rect(0.1, 0.1, 0.8, 0.8);
        bool pushPng = Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_COMPOSITE_PNG_PUSH") == "1";
        if (!video)
        {
            string? videoPath = Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_VIDEO_BACKGROUND");
            using var videoBackground = string.IsNullOrWhiteSpace(videoPath) ? null : File.OpenRead(videoPath);
            double captureMs = 0;
            int capturedFrames = 0;
            int reusedComposites = 0;
            byte[]? lastFrame = null;
            byte[]? lastComposite = null;
            bool pipeline = Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_COMPOSITE_PIPELINE") == "1";
            var scaling = Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_COMPOSITE_FAST_SCALE") == "1"
                ? BitmapScalingMode.LowQuality : BitmapScalingMode.HighQuality;
            Task<(byte[]? Frame, double Milliseconds)>? pending = null;
            try
            {
                await MeasureLiveAsync(model, session, async cancellation =>
                {
                    var (frame, milliseconds) = await (pending ?? CaptureFrameAsync(cancellation));
                    if (frame is null)
                        return null;
                    // 最多预取一帧，当前帧合成期间让 Chromium 抓取下一帧。
                    pending = pipeline ? CaptureFrameAsync(cancellation) : null;
                    captureMs += milliseconds;
                    capturedFrames++;
                    if (lastComposite is not null && frame.AsSpan().SequenceEqual(lastFrame))
                    {
                        reusedComposites++;
                        return lastComposite;
                    }
                    long started = Stopwatch.GetTimestamp();
                    byte[] prepared = DisplayFrameProcessor.PrepareLiveHtmlFrame(frame, MediaLayout.Default,
                        videoBackground is null ? background : null, MediaLayout.Default, model,
                        preserveTransparency: videoBackground is not null, rotation: 0, crop: crop,
                        scalingMode: scaling);
                    composeMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    lastFrame = frame;
                    lastComposite = prepared;
                    return prepared;
                }, timeout.Token, videoBackground: videoBackground,
                    maximumBackgroundBlockBytes: int.TryParse(Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_BACKGROUND_BLOCK_KIB"),
                        out int backgroundBlockKiB) && backgroundBlockKiB is >= 4 and <= 512
                        ? backgroundBlockKiB * 1024 : int.MaxValue);
            }
            finally
            {
                if (pending is not null)
                    await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            if (capturedFrames > 0)
                Console.WriteLine(FormattableString.Invariant(
                    $"Browser read: {captureMs / capturedFrames:F2} ms/frame; decode, composite and encode: {composeMs / (capturedFrames - reusedComposites):F2} ms/composed frame ({capturedFrames - reusedComposites} composed, {reusedComposites} reused)."));
            return;

            // 读取当前来源的一个捕捉帧。
            async Task<(byte[]?, double)> CaptureFrameAsync(CancellationToken cancellation)
            {
                long started = Stopwatch.GetTimestamp();
                byte[]? frame = pushPng
                    ? await html.ReadPngScreencastAsync(cancellation, repeatAfter: TimeSpan.Zero)
                    : await html.ReadFrameAsync(cancellation);
                return (frame, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        bool smallerRaster = Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_COMPOSITE_SMALL_RASTER") == "1";
        DisplayModel videoCanvas = smallerRaster ? DisplayFrameProcessor.HtmlVideoCanvas(model) : ScaleVideoCanvas(model, 50);
        if (int.TryParse(Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_COMPOSITE_OUTPUT_PERCENT"),
            out int outputPercent) && outputPercent is >= 20 and <= 100)
            videoCanvas = ScaleVideoCanvas(model, outputPercent);
        if (Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_VIDEO_BACKGROUND") is { Length: > 0 } hostVideoPath)
        {
            await MeasureHostVideoCompositeAsync(model, session, html, videoCanvas, crop, hostVideoPath,
                pushPng, blockKiB, (int)videoDuration.TotalSeconds * 30, timeout.Token);
            return;
        }
        await MeasureVideoFramesAsync(model, session, cancellation => pushPng
                ? html.ReadPngScreencastAsync(cancellation, repeatAfter: TimeSpan.Zero)
                : html.ReadFrameAsync(cancellation), timeout.Token,
            jpegInput: false, blockKiB,
            frameCount: (int)videoDuration.TotalSeconds * 30,
            rawInputCanvas: videoCanvas, prepareFrame: frame =>
        {
            if (previousPixels is not null && frame.AsSpan().SequenceEqual(previousScreenshot))
            {
                reusedFrames++;
                return previousPixels;
            }
            long started = Stopwatch.GetTimestamp();
            byte[] composed = DisplayFrameProcessor.PrepareForegroundVideoPixels(
                DisplayFrameProcessor.DecodeFrame(frame), MediaLayout.Default, background, MediaLayout.Default,
                model, rotation: 0, crop: crop, outputCanvas: videoCanvas);
            composeMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            composedFrames++;
            previousScreenshot = frame;
            previousPixels = composed;
            return composed;
        });
        if (composedFrames > 0)
            Console.WriteLine(FormattableString.Invariant(
                $"Decode, composite and copy pixels: {composeMs / composedFrames:F2} ms/frame ({composedFrames} composed, {reusedFrames} reused)."));
    }

    /// <summary>测量主机端进行视频背景合成的成本。</summary>
    private static async Task MeasureHostVideoCompositeAsync(DisplayModel model, DisplaySession session,
        HtmlCapture html, DisplayModel canvas, Rect crop, string path, bool pushPng, int blockKiB,
        int frameCount, CancellationToken token)
        => await MeasureHostCompositeAsync(model, session, canvas, crop, path, blockKiB, frameCount, token,
            cancellation => pushPng ? html.ReadPngScreencastAsync(cancellation, repeatAfter: TimeSpan.Zero)
                : html.ReadFrameAsync(cancellation), DisplayFrameProcessor.DecodeFrame,
            cacheOverlay: Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_HTML_OVERLAY_CACHE") != "0");

    /// <summary>测量窗口捕捉路径的缓冲区性能。</summary>
    private static int MeasureWindowBuffer()
    {
        var model = DisplayModel.Known.First(model => model.ProductId == 0x0070);
        byte[] source = new byte[640 * 360 * 4];
        new Random(42).NextBytes(source);
        var bitmap = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgr32, null, source, 640 * 4);
        // 渲染一帧测试像素，可复用调用方提供的缓冲区。
        byte[] Render(byte[]? buffer) => DisplayFrameProcessor.PrepareForegroundVideoPixels(bitmap, MediaLayout.Default,
            null, MediaLayout.Default, model, rotation: 0, crop: null, destination: buffer);

        byte[] reusable = Render(null);
        Render(reusable);
        const int frameCount = 120;
        foreach (byte[]? buffer in new byte[]?[] { null, reusable })
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            for (int frame = 0; frame < frameCount; frame++)
                Render(buffer);
            double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds / frameCount;
            long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / frameCount;
            Console.WriteLine(FormattableString.Invariant(
                $"PID {model.ProductId:X4}, reuse={buffer is not null}: {bytes} managed bytes/frame; {milliseconds:F2} ms/frame."));
        }
        return 0;
    }

    /// <summary>检查视频图层混合是否完整覆盖输出。</summary>
    private static int VerifyVideoBlend()
    {
        var model = DisplayModel.Known.First(model => model.ProductId == 0x0070);
        var output = DisplayFrameProcessor.HtmlVideoCanvas(model);
        byte[] foreground = new byte[64 * 48 * 4];
        byte[] background = new byte[output.Width * output.Height * 4];
        var random = new Random(42);
        random.NextBytes(foreground);
        random.NextBytes(background);
        var foregroundBitmap = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, foreground, 64 * 4);
        var backgroundBitmap = BitmapSource.Create(output.Width, output.Height, 96, 96, PixelFormats.Bgr32,
            null, background, output.Width * 4);
        var layout = new MediaLayout(false, 13, -8) { ZoomPercent = 115 };
        var crop = new Rect(.1, .15, .7, .7);
        byte[] expected = DisplayFrameProcessor.PrepareForegroundVideoPixels(foregroundBitmap, layout, backgroundBitmap,
            MediaLayout.Default, model, rotation: 0, crop, output);
        // 用非零初值验证复用缓冲区时整帧像素都会被覆盖。
        var destination = new byte[expected.Length];
        Array.Fill(destination, (byte)0xA5);
        byte[] reused = DisplayFrameProcessor.PrepareForegroundVideoPixels(foregroundBitmap, layout, backgroundBitmap,
            MediaLayout.Default, model, rotation: 0, crop, output, destination);
        if (!ReferenceEquals(destination, reused) || !reused.AsSpan().SequenceEqual(expected))
        {
            Console.Error.WriteLine("Reused video buffer does not match the freshly allocated frame.");
            return 1;
        }
        Console.WriteLine("Reused video buffer matches the freshly allocated frame.");
        byte[] actual = DisplayFrameProcessor.CompositeVideoPixels(foregroundBitmap, layout, background, model, output, crop);
        int maxDifference = 0;
        for (int i = 0; i < actual.Length; i += 4)
            for (int channel = i; channel < i + 3; channel++)
                maxDifference = Math.Max(maxDifference, Math.Abs(actual[channel] - expected[channel]));
        Console.WriteLine($"Maximum RGB channel difference: {maxDifference}.");
        return maxDifference <= 1 ? 0 : 1;
    }

    /// <summary>测量主机端执行前景与背景合成的成本。</summary>
    private static async Task MeasureHostCompositeAsync<T>(DisplayModel model, DisplaySession session,
        DisplayModel canvas, Rect? crop, string path, int blockKiB, int frameCount, CancellationToken token,
        Func<CancellationToken, ValueTask<T?>> capture, Func<T, BitmapSource> decode,
        bool cacheOverlay = false) where T : class
    {
        bool rotate = Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_ROTATED_COMPOSITE") == "1";
        byte rotation = rotate ? (byte)1 : (byte)0;
        var layoutCanvas = canvas.ForRotation(rotation);
        var layout = rotate ? new MediaLayout(true, 12, -8) { ZoomPercent = 110 } : MediaLayout.Default;
        bool blendPixels = !rotate && Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_BLEND_PIXELS") == "1";
        await using var decoder = new LoopingVideoFrames(path, layoutCanvas, fps: 30, layout: layout);
        int stride = checked(layoutCanvas.Width * 4);
        // 一帧正在写入编码器、一帧排队、一帧正在读取。探针没有异步预览引用。
        byte[][]? buffers = Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_REUSE_BACKGROUND") == "1"
            ? Enumerable.Range(0, 3).Select(_ => new byte[checked(stride * layoutCanvas.Height)]).ToArray()
            : null;
        int nextBuffer = 0;
        byte[]? overlay = blendPixels && cacheOverlay ? new byte[checked(stride * layoutCanvas.Height)] : null;
        BitmapSource? renderedForeground = null;
        T? previousSource = null;
        BitmapSource? decodedForeground = null;
        int overlayRenders = 0;
        double composeMs = 0;
        double decodeMs = 0, renderMs = 0, blendMs = 0;
        Action<TimeSpan, TimeSpan> recordBlend = (render, blend) =>
        {
            renderMs += render.TotalMilliseconds;
            blendMs += blend.TotalMilliseconds;
        };
        int composed = 0;
        await MeasureVideoFramesAsync<Tuple<T, byte[]>>(model, session, async cancellation =>
        {
            var foreground = await capture(cancellation);
            if (foreground is null)
                return null;
            if (buffers is null)
                return Tuple.Create(foreground, await decoder.ReadAsync(cancellation));
            byte[] buffer = buffers[nextBuffer];
            await decoder.ReadAsync(buffer, cancellation);
            nextBuffer = (nextBuffer + 1) % buffers.Length;
            return Tuple.Create(foreground, buffer);
        }, token, jpegInput: false, blockKiB, prepareFrame: frame =>
        {
            long started = Stopwatch.GetTimestamp();
            var foreground = overlay is not null && ReferenceEquals(previousSource, frame.Item1)
                ? decodedForeground! : decode(frame.Item1);
            previousSource = frame.Item1;
            decodedForeground = foreground;
            decodeMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (overlay is not null && !ReferenceEquals(renderedForeground, foreground))
            {
                DisplayFrameProcessor.RenderVideoOverlay(foreground, MediaLayout.Default, model, canvas, crop, overlay);
                renderedForeground = foreground;
                overlayRenders++;
            }
            var pixels = overlay is not null ? DisplayFrameProcessor.BlendVideoOverlay(overlay, frame.Item2) : blendPixels
                ? DisplayFrameProcessor.CompositeVideoPixels(foreground, MediaLayout.Default, frame.Item2, model, canvas, crop, recordBlend)
                : DisplayFrameProcessor.PrepareForegroundVideoPixels(foreground, MediaLayout.Default,
                    BitmapSource.Create(layoutCanvas.Width, layoutCanvas.Height, 96, 96, PixelFormats.Bgr32, null, frame.Item2, stride),
                    MediaLayout.Default, model, rotation: rotation, crop: crop, outputCanvas: canvas);
            composeMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            composed++;
            return pixels;
        }, frameCount: frameCount, rawInputCanvas: canvas);
        if (composed > 0)
            Console.WriteLine(FormattableString.Invariant($"Host composition: {composeMs / composed:F2} ms/frame ({composed} frames)."));
        if (overlay is not null)
            Console.WriteLine($"Foreground overlay rendered {overlayRenders} times for {composed} frames.");
        else if (blendPixels && composed > 0)
            Console.WriteLine(FormattableString.Invariant(
                $"Foreground stages: decode {decodeMs / composed:F2} ms, render and copy {renderMs / composed:F2} ms, blend {blendMs / composed:F2} ms per frame."));
    }

    /// <summary>测量图片轮播的实际输出表现。</summary>
    private static async Task MeasureSlidesAsync(DisplayModel model, DisplaySession session,
        string[] paths, bool video, int blockKiB, int bitrateKbps, int outputPercent, bool pngInput, byte rotation)
    {
        if (video && Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_VIDEO_BACKGROUND") is { Length: > 0 } slidesBackground)
        {
            await using var originalSlides = new SlideshowCapture(paths, TimeSpan.FromSeconds(1), static image => image);
            await MeasureImageVideoCompositeAsync(model, session, slidesBackground, blockKiB, originalSlides.ReadFrameAsync);
            return;
        }
        using var timeout = new CancellationTokenSource(video
            ? TimeSpan.FromSeconds(95) : GetLiveSampleDuration() + TimeSpan.FromSeconds(35));
        bool jpeg = !pngInput && (video || Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_SLIDES_JPEG") == "1"
            || (model.ProductId == 0x0021 && Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_0021_JPEG") == "1"));
        double interval = 1;
        if (Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_SLIDES_INTERVAL") is { } value
            && (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out interval)
                || interval is not (>= 0.03 and <= 3600)))
            throw new ArgumentException("Slideshow interval must be from 0.03 to 3600 seconds.");
        await using var slides = new SlideshowCapture(paths, TimeSpan.FromSeconds(interval),
            image => jpeg
                ? DisplayFrameProcessor.PreparePlaybackJpeg(image, MediaLayout.Default, null, MediaLayout.Default, model, rotation)
                    ?? throw new InvalidDataException("The slideshow frame exceeds the JPEG size limit.")
                : pngInput
                    ? DisplayFrameProcessor.Prepare(image, model, preserveTransparency: true, rotation: rotation)
                    : DisplayFrameProcessor.PreparePlaybackImage(image, MediaLayout.Default, null, MediaLayout.Default, model, rotation),
            cacheLimitBytes: video ? 0 : 16 * 1024 * 1024);
        if (video)
            await MeasureVideoFramesAsync(model, session, slides.ReadFrameAsync, timeout.Token,
                jpegInput: !pngInput, blockKiB, frameCount: 1800, bitrateKbps: bitrateKbps,
                outputCanvas: ScaleVideoCanvas(model, outputPercent));
        else
            await MeasureLiveAsync(model, session, slides.ReadChangedFrameAsync, timeout.Token, bufferLatestForeground: false);
    }

    /// <summary>测量静态图片与视频背景组合输出。</summary>
    private static async Task MeasureImageVideoCompositeAsync(DisplayModel model, DisplaySession session,
        string path, int blockKiB, Func<CancellationToken, ValueTask<byte[]?>> capture)
    {
        TimeSpan duration = GetLiveSampleDuration(defaultSeconds: 15);
        using var timeout = new CancellationTokenSource(duration * 3 + TimeSpan.FromSeconds(35));
        byte[]? previous = null;
        BitmapSource? bitmap = null;
        await MeasureHostCompositeAsync(model, session, HostCompositeCanvas(model), null,
            path, blockKiB, (int)duration.TotalSeconds * 30, timeout.Token, capture, image =>
            {
                if (!ReferenceEquals(image, previous))
                {
                    bitmap = DisplayFrameProcessor.DecodeFrame(image);
                    bitmap.Freeze();
                    previous = image;
                }
                return bitmap!;
            }, cacheOverlay: true);
    }

    /// <summary>计算按比例缩放的视频输出画布。</summary>
    private static DisplayModel ScaleVideoCanvas(DisplayModel model, int percent) =>
        model with { Width = model.Width * percent / 200 * 2, Height = model.Height * percent / 200 * 2 };

    /// <summary>计算主机合成所使用的设备画布。</summary>
    private static DisplayModel HostCompositeCanvas(DisplayModel model) => model.ProductId switch
    {
        0x0021 => model,
        _ => DisplayFrameProcessor.HtmlVideoCanvas(model)
    };

    /// <summary>测量读取视频原始帧的吞吐。</summary>
    private static Task MeasureVideoFramesAsync(DisplayModel model, DisplaySession session,
        Func<CancellationToken, ValueTask<byte[]?>> capture, CancellationToken cancellation, bool jpegInput,
        int blockKiB, int frameCount = 300, int bitrateKbps = 3000, DisplayModel? outputCanvas = null,
        Func<byte[], byte[]>? prepareFrame = null, DisplayModel? rawInputCanvas = null, byte outputRotation = 0, byte fps = 30)
        => MeasureVideoFramesAsync<byte[]>(model, session, capture, cancellation, jpegInput, blockKiB,
            prepareFrame ?? (static frame => frame), frameCount, bitrateKbps, outputCanvas, rawInputCanvas, outputRotation, fps);

    /// <summary>在指定输入参数下测量解码帧流。</summary>
    private static async Task MeasureVideoFramesAsync<TFrame>(DisplayModel model, DisplaySession session,
        Func<CancellationToken, ValueTask<TFrame?>> capture, CancellationToken cancellation, bool jpegInput,
        int blockKiB, Func<TFrame, byte[]> prepareFrame, int frameCount = 300, int bitrateKbps = 3000,
        DisplayModel? outputCanvas = null, DisplayModel? rawInputCanvas = null, byte outputRotation = 0, byte fps = 30) where TFrame : class
    {
        int frames = 0;
        double captureMs = 0;
        double longestCaptureMs = 0;
        double longestEnqueueGapMs = 0;
        long firstInput = 0;
        long lastInput = 0;
        double writeMs = 0;
        long firstWrite = 0;
        long lastWrite = 0;
        int writes = 0;
        int changedWrites = 0;
        byte[]? previousEncoderFrame = null;
        double longestWriteGapMs = 0;
        double longestStartupGapMs = 0;
        var steadyWriteGaps = new List<double>();
        long firstSteadyWrite = 0;
        long firstBlockAcknowledged = 0;
        double queueWaitMs = 0;
        int queueWaits = 0;
        int acknowledgedBlocks = 0;
        int maxQueueDepth = 0;
        var clock = Stopwatch.StartNew();
        await LiveVideoEncoder.RunAsync(session,
            async token =>
            {
                if (frames >= frameCount)
                    return null;
                long started = Stopwatch.GetTimestamp();
                TFrame? frame = await capture(token);
                double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                captureMs += elapsedMs;
                longestCaptureMs = Math.Max(longestCaptureMs, elapsedMs);
                return frame;
            },
            frame =>
            {
                byte[] prepared = prepareFrame(frame);
                // 合成路径可原地复用缓冲区，保留上一帧的字节用于比较。
                if (previousEncoderFrame is null)
                    previousEncoderFrame = (byte[])prepared.Clone();
                else if (!prepared.AsSpan().SequenceEqual(previousEncoderFrame))
                {
                    changedWrites++;
                    if (previousEncoderFrame.Length != prepared.Length)
                        previousEncoderFrame = new byte[prepared.Length];
                    prepared.CopyTo(previousEncoderFrame, 0);
                }
                return prepared;
            }, fps, cancellation,
            onStarted: () =>
            {
                Volatile.Write(ref firstBlockAcknowledged, Stopwatch.GetTimestamp());
                Console.WriteLine($"First H.264 block acknowledged at {clock.Elapsed.TotalSeconds:F2}s.");
            },
            onFrameEncodedInput: () =>
            {
                long now = Stopwatch.GetTimestamp();
                if (frames > 0)
                    longestEnqueueGapMs = Math.Max(longestEnqueueGapMs, Stopwatch.GetElapsedTime(lastInput, now).TotalMilliseconds);
                lastInput = now;
                if (++frames == 1)
                    firstInput = lastInput;
            }, jpegInput: jpegInput, outputCanvas: outputCanvas ?? rawInputCanvas ?? model, maximumBlockBytes: blockKiB * 1024,
            onInputWrite: duration =>
            {
                long now = Stopwatch.GetTimestamp();
                if (++writes == 1)
                    firstWrite = now;
                else
                {
                    double gap = Stopwatch.GetElapsedTime(lastWrite, now).TotalMilliseconds;
                    long boundary = Volatile.Read(ref firstBlockAcknowledged);
                    if (boundary != 0 && lastWrite >= boundary)
                    {
                        longestWriteGapMs = Math.Max(longestWriteGapMs, gap);
                        if (steadyWriteGaps.Count == 0)
                            firstSteadyWrite = lastWrite;
                        steadyWriteGaps.Add(gap);
                    }
                    else
                        longestStartupGapMs = Math.Max(longestStartupGapMs, gap);
                }
                lastWrite = now;
                writeMs += duration.TotalMilliseconds;
            },
            onQueueWait: duration =>
            {
                queueWaits++;
                queueWaitMs += duration.TotalMilliseconds;
            }, bitrateKbps: bitrateKbps, rawInputCanvas: rawInputCanvas, outputRotation: outputRotation,
            onBlockAcknowledged: depth =>
            {
                acknowledgedBlocks++;
                maxQueueDepth = Math.Max(maxQueueDepth, depth);
            });
        var canvas = outputCanvas ?? rawInputCanvas ?? model;
        Console.WriteLine($"Encoded raster: {canvas.Width}x{canvas.Height}.");
        Console.WriteLine($"H.264 blocks acknowledged: {acknowledgedBlocks}; maximum reported queue depth: {maxQueueDepth}.");
        Console.WriteLine(FormattableString.Invariant(
            $"Encoder input writes: {writeMs / Math.Max(1, frames):F2} ms/frame. Device queue waits: {queueWaits}, {queueWaitMs / 1000:F2}s total."));
        if (frames > 1)
            Console.WriteLine(FormattableString.Invariant(
                $"Steady enqueue rate: {(frames - 1) / Stopwatch.GetElapsedTime(firstInput, lastInput).TotalSeconds:F2} FPS; longest gap: {longestEnqueueGapMs:F2} ms; longest frame acquisition: {longestCaptureMs:F2} ms."));
        if (writes > 1)
            Console.WriteLine(FormattableString.Invariant(
                $"Encoder input: {(writes - 1) / Stopwatch.GetElapsedTime(firstWrite, lastWrite).TotalSeconds:F2} FPS; changed frame data: {changedWrites / Stopwatch.GetElapsedTime(firstWrite, lastWrite).TotalSeconds:F2} FPS ({writes - 1 - changedWrites} repeated); longest startup gap: {longestStartupGapMs:F2} ms."));
        if (steadyWriteGaps.Count > 0)
        {
            steadyWriteGaps.Sort();
            double p95 = steadyWriteGaps[(int)Math.Ceiling(steadyWriteGaps.Count * 0.95) - 1];
            Console.WriteLine(FormattableString.Invariant(
                $"Encoder input after first block acknowledgement: {steadyWriteGaps.Count / Stopwatch.GetElapsedTime(firstSteadyWrite, lastWrite).TotalSeconds:F2} FPS; gap p95 {p95:F2} ms, max {longestWriteGapMs:F2} ms ({steadyWriteGaps.Count} intervals)."));
        }
        double elapsedSeconds = clock.Elapsed.TotalSeconds;
        Console.WriteLine(FormattableString.Invariant(
            $"PID {model.ProductId:X4}, target bitrate {bitrateKbps} kbps, block cap {blockKiB} KiB: transfer completed in {elapsedSeconds:F2}s for {frames} input frames ({frames / elapsedSeconds:F2} input FPS including startup and drain). Mean frame acquisition: {captureMs / Math.Max(1, frames):F2} ms."));
    }

    /// <summary>测量来源取帧速度及等待时间。</summary>
    private static async Task MeasureCaptureAsync(DisplayModel model, string source, DisplaySession? session = null,
        byte rotation = 0)
    {
        TimeSpan sampleDuration = GetLiveSampleDuration();
        using var timeout = new CancellationTokenSource(sampleDuration + TimeSpan.FromSeconds(35));
        var token = timeout.Token;
        double pixelScale = 1;
        if (Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_PIXEL_SCALE") is { } scale
            && (!double.TryParse(scale, NumberStyles.Float, CultureInfo.InvariantCulture, out pixelScale)
                || !double.IsFinite(pixelScale) || pixelScale is <= 0 or > 1))
            throw new ArgumentException("Probe pixel scale must be greater than zero and at most one.");

        await using var capture = await HtmlCapture.OpenAsync(source, model.ForRotation(rotation), token, fastCapture: UncapBrowserFrames, pixelScale);
        if (session is null && Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_COMPARE_ALPHA") == "1")
        {
            // 先截图，再启动推送，避免两个 CDP 读取操作互相干扰。
            ReportAlpha("Screenshot", await capture.ReadFrameAsync(token));
            ReportAlpha("Screencast", await capture.ReadPngScreencastAsync(token));
            return;
        }
        double captureMs = 0, encodeMs = 0;
        int frames = 0;
        long bytes = 0;
        if (session is not null)
        {
            int jpegPercent = 0;
            if (Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_JPEG_PERCENT") is { } percent
                && (!int.TryParse(percent, out jpegPercent) || jpegPercent is < 25 or > 100))
                throw new ArgumentException("Probe JPEG output scale must be between 25 and 100 percent.");
            bool directJpeg = rotation == 0 && pixelScale == 1
                && (Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_JPEG") == "1"
                    || (model.ProductId == 0x0021 && Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_0021_JPEG") == "1"));
            DisplayModel jpegCanvas = ScaleVideoCanvas(model, jpegPercent);
            byte[]? previousCapture = null;
            int reusedCaptures = 0;
            await MeasureLiveAsync(model, session, async cancellation =>
            {
                long started = Stopwatch.GetTimestamp();
                byte[] screenshot = (await (directJpeg || jpegPercent != 0
                    ? ReadBrowserJpegAsync(capture, cancellation)
                    : capture.ReadFrameAsync(cancellation)))
                    ?? throw new IOException("The browser did not return a frame.");
                if (ReferenceEquals(previousCapture, screenshot))
                    reusedCaptures++;
                previousCapture = screenshot;
                captureMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                started = Stopwatch.GetTimestamp();
                byte[] frame = jpegPercent != 0
                    ? DisplayFrameProcessor.PrepareForegroundVideoFrame(DisplayFrameProcessor.DecodeFrame(screenshot),
                        MediaLayout.Default, null, MediaLayout.Default, model, rotation, crop: null, jpegCanvas)
                    : directJpeg ? screenshot : DisplayFrameProcessor.PrepareLiveHtmlFrame(screenshot, MediaLayout.Default, null,
                        MediaLayout.Default, model, preserveTransparency: false, rotation, crop: null);
                encodeMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                bytes += frame.Length;
                frames++;
                return frame;
            }, token, browserScreencast: directJpeg &&
                Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_SCREENCAST") == "1");
            if (frames > 0)
                Console.WriteLine(FormattableString.Invariant(
                    $"Browser capture: {frames} reads ({reusedCaptures} reused), {captureMs / frames:F2} ms/frame; host encoding: {encodeMs / frames:F2} ms/frame; {bytes / frames} B/frame."));
            return;
        }
        var elapsed = Stopwatch.StartNew();
        do
        {
            long started = Stopwatch.GetTimestamp();
            byte[] screenshot = await capture.ReadFrameAsync(token)
                ?? throw new IOException("The browser did not return a frame.");
            captureMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            started = Stopwatch.GetTimestamp();
            byte[] frame = DisplayFrameProcessor.PrepareLiveHtmlFrame(screenshot, MediaLayout.Default, null,
                MediaLayout.Default, model, preserveTransparency: false, rotation, crop: null);
            encodeMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            bytes += frame.Length;
            frames++;
        } while (elapsed.Elapsed < sampleDuration);

        Console.WriteLine(FormattableString.Invariant(
            $"PID {model.ProductId:X4}: capture+encode {frames / elapsed.Elapsed.TotalSeconds:F2} FPS, capture {captureMs / frames:F2} ms, encode {encodeMs / frames:F2} ms, {bytes / frames} B/frame."));
    }

    /// <summary>输出抓取帧的透明通道统计。</summary>
    private static void ReportAlpha(string name, byte[]? frame)
    {
        if (frame is null)
            throw new IOException($"{name} returned no frame.");

        var pixels = new FormatConvertedBitmap(DisplayFrameProcessor.DecodeFrame(frame), PixelFormats.Bgra32, null, 0);
        var sample = new byte[4];
        var alpha = new List<byte>();
        // 取横向三个等宽色块的中心，避开边缘抗锯齿。
        foreach (int numerator in new[] { 1, 3, 5 })
        {
            pixels.CopyPixels(new Int32Rect(pixels.PixelWidth * numerator / 6, pixels.PixelHeight / 2, 1, 1),
                sample, 4, 0);
            alpha.Add(sample[3]);
        }
        Console.WriteLine($"{name} alpha: {string.Join(", ", alpha)}");
    }

    /// <summary>计算位图像素的哈希以比较帧内容。</summary>
    private static byte[] HashBitmap(BitmapSource bitmap)
    {
        int stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        return SHA256.HashData(pixels);
    }

    /// <summary>按探针模式取得浏览器 JPEG 帧。</summary>
    private static ValueTask<byte[]?> ReadBrowserJpegAsync(HtmlCapture capture, CancellationToken cancellation)
    {
        if (Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_SCREENCAST") != "1")
            return capture.ReadJpegFrameAsync(cancellation);
        int quality = int.TryParse(Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_JPEG_QUALITY"),
            out int requested) && requested is >= 1 and <= 100 ? requested : 85;
        string? repeatMode = Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_REPEAT_STATIC");
        // 模式 2 由外层定时器限速，直接取最新已完成的浏览器帧。
        TimeSpan? repeatAfter = repeatMode switch
        {
            "1" => TimeSpan.FromMilliseconds(33),
            "2" => TimeSpan.Zero,
            "3" => TimeSpan.FromMilliseconds(1000d / ReadProbeCaptureFps()),
            _ => null
        };
        return capture.ReadJpegScreencastAsync(cancellation, quality, repeatAfter);
    }

    /// <summary>测量一段时间内实时捕捉和发送的表现。</summary>
    private static async Task MeasureLiveAsync(DisplayModel model, DisplaySession session,
        Func<CancellationToken, ValueTask<byte[]?>> capture, CancellationToken token, bool browserScreencast = false,
        Stream? videoBackground = null, int maximumBackgroundBlockBytes = int.MaxValue, bool bufferLatestForeground = true)
    {
        using var sample = CancellationTokenSource.CreateLinkedTokenSource(token);
        TimeSpan sampleDuration = GetLiveSampleDuration();
        var clock = Stopwatch.StartNew();
        int frames = 0;
        int captureFps = ReadProbeCaptureFps();
        var usbDurations = new List<double>();
        var usbWrites = new List<double>();
        var usbReplies = new List<double>();
        byte[]? previousSentFrame = null;
        var changedFrameGaps = new List<double>();
        int repeatedFrames = 0;
        double longestRepeatedGapMs = 0;
        long previousAcknowledgement = 0;
        long previousChangedAcknowledgement = 0;
        if (browserScreencast && Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_NATIVE_PACING") == "1")
        {
            // 推帧已经等待浏览器生成画面。测量时直接确认新帧，避免定时器影响推送频率。
            session.StopVideo();
            session.ShowDefaultClock(false);
            while (!sample.IsCancellationRequested)
            {
                byte[] frame = await capture(sample.Token) ?? throw new IOException("The browser stopped returning frames.");
                long sendStarted = Stopwatch.GetTimestamp();
                session.ShowJpegFrame(frame, RecordJpegTransfer);
                usbDurations.Add(Stopwatch.GetElapsedTime(sendStarted).TotalMilliseconds);
                await CountFrameAsync(frame);
            }
            Console.WriteLine(FormattableString.Invariant(
                $"PID {model.ProductId:X4}: {(frames - 1) / clock.Elapsed.TotalSeconds:F2} live FPS ({frames} frames acknowledged; browser-paced)."));
            PrintUsbDurations();
            return;
        }
        if (Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_USB_ONLY") == "1")
        {
            // 复用实际浏览器生成的帧，隔离捕捉开销并保留相同的 USB 命令路径。
            byte[] frame = await capture(token) ?? throw new IOException("No frame available for the USB probe.");
            capture = _ => ValueTask.FromResult<byte[]?>(frame);
        }
        try
        {
            await LiveScene.RunAsync(session, capture, videoBackground, videoBackground is null ? (byte)0 : (byte)30,
                captureFps, sample.Token, null, CountFrameAsync,
                delayAfterFrame: false, bufferLatestForeground: bufferLatestForeground, adaptiveCapture: false, deferForeground: false,
                onForegroundExchange: duration => usbDurations.Add(duration.TotalMilliseconds),
                onJpegTransfer: RecordJpegTransfer, maximumBackgroundBlockBytes: maximumBackgroundBlockBytes);
        }
        catch (OperationCanceledException) when (!clock.IsRunning && !token.IsCancellationRequested) { }
        Console.WriteLine(FormattableString.Invariant(
            $"PID {model.ProductId:X4}: {(frames - 1) / clock.Elapsed.TotalSeconds:F2} live FPS ({frames} frames acknowledged; capture target {captureFps} FPS)."));
        PrintUsbDurations();

        // 记录一帧 JPEG 的写入及应答耗时。
        void RecordJpegTransfer(TimeSpan write, TimeSpan reply)
        {
            usbWrites.Add(write.TotalMilliseconds);
            usbReplies.Add(reply.TotalMilliseconds);
        }

        // 输出所记录的 USB 传输耗时统计。
        void PrintUsbDurations()
        {
            if (usbDurations.Count == 0)
                return;
            if (changedFrameGaps.Count > 0)
            {
                changedFrameGaps.Sort();
                double p95 = changedFrameGaps[(int)Math.Ceiling(changedFrameGaps.Count * 0.95) - 1];
                Console.WriteLine(FormattableString.Invariant(
                    $"Changed frame data: {changedFrameGaps.Count / clock.Elapsed.TotalSeconds:F2} FPS, acknowledgement gap p95 {p95:F2} ms, max {changedFrameGaps[^1]:F2} ms; {repeatedFrames} repeated frames (max gap {longestRepeatedGapMs:F2} ms)."));
            }
            usbDurations.Sort();
            // 按最近秩从已排序的样本中取出指定百分位。
            double Percentile(double fraction) => usbDurations[(int)Math.Ceiling(usbDurations.Count * fraction) - 1];
            Console.WriteLine(FormattableString.Invariant(
                $"USB round trip: median {Percentile(0.5):F2} ms, p95 {Percentile(0.95):F2} ms, max {usbDurations[^1]:F2} ms; {usbDurations.Count(ms => ms > 1000d / captureFps)} over frame budget."));
            if (usbWrites.Count > 0)
            {
                usbWrites.Sort();
                usbReplies.Sort();
                int index = (int)Math.Ceiling(usbWrites.Count * 0.95) - 1;
                Console.WriteLine(FormattableString.Invariant(
                    $"USB write: p95 {usbWrites[index]:F2} ms, max {usbWrites[^1]:F2} ms; reply wait: p95 {usbReplies[index]:F2} ms, max {usbReplies[^1]:F2} ms."));
            }
        }

        // 统计测量区间内的送屏帧。
        ValueTask CountFrameAsync(byte[] frame)
        {
            long now = Stopwatch.GetTimestamp();
            if (previousAcknowledgement != 0)
            {
                double gap = Stopwatch.GetElapsedTime(previousAcknowledgement, now).TotalMilliseconds;
                if (!frame.AsSpan().SequenceEqual(previousSentFrame))
                {
                    changedFrameGaps.Add(Stopwatch.GetElapsedTime(previousChangedAcknowledgement, now).TotalMilliseconds);
                    previousChangedAcknowledgement = now;
                }
                else
                {
                    repeatedFrames++;
                    longestRepeatedGapMs = Math.Max(longestRepeatedGapMs, gap);
                }
            }
            previousAcknowledgement = now;
            previousSentFrame = frame;
            if (++frames == 1)
            {
                previousChangedAcknowledgement = now;
                clock.Restart();
            }
            if (clock.Elapsed >= sampleDuration)
            {
                clock.Stop();
                sample.Cancel();
            }
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>从环境变量读取 1 至 120 秒的采样时长，否则使用默认值。</summary>
    private static TimeSpan GetLiveSampleDuration(int defaultSeconds = 10) =>
        TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_SAMPLE_SECONDS"),
            out int seconds) && seconds is >= 1 and <= 120 ? seconds : defaultSeconds);

    private static byte ReadProbeCaptureFps() =>
        byte.TryParse(Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_CAPTURE_FPS"),
            out byte fps) && fps is >= 1 and <= 60 ? fps : (byte)30;

    private static int ReadProbeLiveBitrate() =>
        int.TryParse(Environment.GetEnvironmentVariable("WINUSB_DISPLAY_PROBE_LIVE_BITRATE"),
            out int bitrate) && bitrate is >= 500 and <= 10000 ? bitrate : 3000;

    /// <summary>输出一项诊断步骤及其进展。</summary>
    private static void Step(string stage, Func<byte[]> exchange)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            byte[] reply = exchange();
            Console.WriteLine($"{stage}: {reply.Length} B, reply[0]=0x{reply[0]:X2}, {watch.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            throw new IOException($"{stage}: {ex.Message}", ex);
        }
    }

    /// <summary>生成用于送屏诊断的彩色测试图。</summary>
    private static byte[] CreateChart()
    {
        var visual = new DrawingVisual();
        Brush[] colors = [Brushes.RoyalBlue, Brushes.DarkCyan, Brushes.Orange, Brushes.White];
        using (var drawing = visual.RenderOpen())
        {
            for (int index = 0; index < colors.Length; index++)
                drawing.DrawRectangle(colors[index], null, new Rect(index * 480, 0, 480, 1080));
            drawing.DrawEllipse(Brushes.Navy, null, new Point(960, 540), 250, 250);
        }
        var bitmap = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
