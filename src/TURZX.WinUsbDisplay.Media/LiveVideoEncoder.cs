using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace TURZX.WinUsbDisplay.Media;

/// <summary>把定时取得的图片帧编码为实时 Annex B H.264 并发送到设备。</summary>
public static class LiveVideoEncoder
{
    /// <summary>默认实时视频块上限，兼顾首块等待和 USB 往返开销。</summary>
    public const int DefaultMaximumBlockBytes = 128 * 1024;

    /// <summary>从字节帧捕捉源读取数据，实时编码为 H.264 并发送到设备。</summary>
    public static Task RunAsync(DisplaySession session, Func<CancellationToken, ValueTask<byte[]?>> capture,
        byte fps, CancellationToken cancellation, Action? onStarted = null, Action? onFrameEncodedInput = null,
        bool jpegInput = false, DisplayModel? outputCanvas = null, int maximumBlockBytes = DefaultMaximumBlockBytes,
        Action<TimeSpan>? onInputWrite = null, Action<TimeSpan>? onQueueWait = null, int bitrateKbps = 3000,
        Func<byte[], byte[]>? prepareFrame = null, DisplayModel? rawInputCanvas = null)
        => RunAsync(session, capture, prepareFrame ?? (static frame => frame), fps, cancellation, onStarted,
            onFrameEncodedInput, jpegInput, outputCanvas, maximumBlockBytes, onInputWrite, onQueueWait, bitrateKbps, rawInputCanvas);

    /// <summary>并行捕捉帧和准备编码输入，最多缓存一帧。</summary>
    public static Task RunAsync<TFrame>(DisplaySession session, Func<CancellationToken, ValueTask<TFrame?>> capture,
        Func<TFrame, byte[]> prepareFrame, byte fps, CancellationToken cancellation, Action? onStarted = null,
        Action? onFrameEncodedInput = null, bool jpegInput = false, DisplayModel? outputCanvas = null,
        int maximumBlockBytes = DefaultMaximumBlockBytes, Action<TimeSpan>? onInputWrite = null,
        Action<TimeSpan>? onQueueWait = null, int bitrateKbps = 3000, DisplayModel? rawInputCanvas = null,
        Action<int>? onBlockAcknowledged = null) where TFrame : class
        => RunAsync(session, capture, prepareFrame, fps, cancellation, 0, onStarted, onFrameEncodedInput,
            jpegInput, outputCanvas, maximumBlockBytes, onInputWrite, onQueueWait, bitrateKbps, rawInputCanvas,
            onBlockAcknowledged);

    /// <summary>并行捕捉和编码帧，并在指定输出画布时将画面转回设备原生方向。</summary>
    public static async Task RunAsync<TFrame>(DisplaySession session, Func<CancellationToken, ValueTask<TFrame?>> capture,
        Func<TFrame, byte[]> prepareFrame, byte fps, CancellationToken cancellation, byte outputRotation,
        Action? onStarted = null, Action? onFrameEncodedInput = null, bool jpegInput = false,
        DisplayModel? outputCanvas = null, int maximumBlockBytes = DefaultMaximumBlockBytes,
        Action<TimeSpan>? onInputWrite = null, Action<TimeSpan>? onQueueWait = null, int bitrateKbps = 3000,
        DisplayModel? rawInputCanvas = null, Action<int>? onBlockAcknowledged = null) where TFrame : class
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, (byte)1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fps, (byte)60);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bitrateKbps);
        string bitrate = FormattableString.Invariant($"{bitrateKbps}k");
        string buffer = FormattableString.Invariant($"{Math.Max(1, bitrateKbps / 3)}k");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var token = lifetime.Token;
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(VideoConverter.ResolveExecutable())
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        // 输入格式已知，避免 FFmpeg 为探测流信息预读数秒画面。
        foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-probesize", "32",
            "-analyzeduration", "0", "-framerate", fps.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        string[] inputArguments = rawInputCanvas is { } raw
            ? ["-f", "rawvideo", "-pixel_format", "bgr0", "-video_size", FormattableString.Invariant($"{raw.Width}x{raw.Height}")]
            : ["-f", "image2pipe", "-vcodec", jpegInput ? "mjpeg" : "png"];
        foreach (string argument in inputArguments)
            start.ArgumentList.Add(argument);
        string? requestedThreads = Environment.GetEnvironmentVariable("WINUSB_DISPLAY_ENCODER_THREADS");
        // 大画布用四线程减轻编码管道积压。较小画布用两线程，为并发捕捉和合成留出 CPU 时间。
        bool largeCanvas = (outputCanvas ?? rawInputCanvas) is { } encodingCanvas
            && (long)encodingCanvas.Width * encodingCanvas.Height >= 720L * 1920 && Environment.ProcessorCount >= 4;
        string encodingThreads = requestedThreads is "1" or "2" or "4" ? requestedThreads : largeCanvas ? "4" : "2";
        foreach (string argument in new[] { "-i", "pipe:0",
            "-an", "-filter_threads", "2", "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
            "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", buffer, "-threads", encodingThreads,
            "-pix_fmt", "yuv420p" })
            start.ArgumentList.Add(argument);
        // 原始像素已按输出尺寸合成时，只让 FFmpeg 转换像素格式。
        if (outputCanvas is { } nativeCanvas && (outputRotation != 0 || rawInputCanvas is null
            || nativeCanvas.Width != rawInputCanvas.Width || nativeCanvas.Height != rawInputCanvas.Height))
        {
            var canvas = nativeCanvas.ForRotation(outputRotation);
            start.ArgumentList.Add("-vf");
            start.ArgumentList.Add(FormattableString.Invariant($"scale={canvas.Width}:{canvas.Height}:flags=fast_bilinear")
                + VideoConverter.NativeRotationFilter(outputRotation));
        }
        foreach (string argument in new[] { "-f", "h264", "pipe:1" })
            start.ArgumentList.Add(argument);

        using var encoder = VideoConverter.StartFfmpeg(start);
        using var stderrReader = encoder.StandardError;
        var diagnostics = new ProcessDiagnostics(stderrReader);
        // 捕捉与编码写入通过单帧队列衔接，写入跟不上时取帧会等待。
        var frames = Channel.CreateBounded<TFrame>(new BoundedChannelOptions(1)
        {
            SingleReader = true, SingleWriter = true
        });
        var inputFailure = new TaskCompletionSource<(Exception Error, bool EncoderPipe)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task input = WriteFramesAsync();
        // 首帧可能同步调用 PrintWindow。让设备初始化与取帧同时进行。
        Task producer = Task.Run(CaptureFramesAsync);
        Task playback = Task.Run(() =>
        {
            try
            {
                session.StopPlayback();
                session.ShowDefaultClock(false);
                session.SetVideoClockSync(false);
                session.PlayVideo(encoder.StandardOutput.BaseStream, fps, loop: false, token, onStarted,
                    resetPlayback: false, maximumBlockBytes, onQueueWait, onBlockAcknowledged);
                return Task.CompletedTask;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 在工作委托内处理正常停播，保留上层可观察的取消状态。
                return Task.FromCanceled(token);
            }
        }, token);
        Exception? failure = null;
        int? exitCode = null;
        bool reportEncoderExit = false;
        try
        {
            // 同时监视设备播放和输入故障，避免取帧或写管道失败后只等 USB 端退出。
            Task finished = await Task.WhenAny(playback, inputFailure.Task).WaitAsync(cancellation).ConfigureAwait(false);
            if (finished == inputFailure.Task)
                failure = inputFailure.Task.Result.Error;
            else
            {
                await playback.ConfigureAwait(false);
                await input.WaitAsync(cancellation).ConfigureAwait(false);
                await producer.WaitAsync(cancellation).ConfigureAwait(false);
                await encoder.WaitForExitAsync(cancellation).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            // 输出截断可能由输入端出错引起，已有输入异常时优先保留该原因。
            failure = error is EndOfStreamException && inputFailure.Task.IsCompletedSuccessfully
                ? inputFailure.Task.Result.Error : error;
        }
        finally
        {
            bool pipeFailed = failure is EndOfStreamException || (inputFailure.Task.IsCompletedSuccessfully
                && inputFailure.Task.Result is { EncoderPipe: true } pipe && ReferenceEquals(pipe.Error, failure));
            reportEncoderExit = failure is null || pipeFailed;
            if (pipeFailed && !cancellation.IsCancellationRequested)
            {
                // FFmpeg 可能先关闭标准输出，随后才提供退出码。
                try
                {
                    await encoder.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromMilliseconds(250), cancellation)
                        .ConfigureAwait(false);
                }
                catch (Exception waitError) when (waitError is TimeoutException or OperationCanceledException) { }
            }
            lifetime.Cancel();
            if (encoder.HasExited)
                exitCode = encoder.ExitCode;
            else
            {
                try { encoder.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (encoder.HasExited) { exitCode = encoder.ExitCode; }
            }
            await playback.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await input.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await producer.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await encoder.WaitForExitAsync().ConfigureAwait(false);
            await VideoConverter.FinishDiagnosticsAsync(stderrReader, diagnostics.Completion).ConfigureAwait(false);
        }

        if (failure is OperationCanceledException)
        {
            if (playback.Exception?.InnerException is PlaybackStopException)
                await playback.ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
        }
        // 编码进程异常退出时附上 FFmpeg 的诊断信息，保留原来的管道异常作为内部原因。
        if (reportEncoderExit && exitCode is int code && code != 0)
        {
            string details = diagnostics.Text.Trim();
            throw new FfmpegProcessException(code, details, failure);
        }
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);

        // 将待编码帧写入 FFmpeg 标准输入。
        async Task WriteFramesAsync()
        {
            bool writingToEncoder = false;
            try
            {
                await using var stdin = encoder.StandardInput.BaseStream;
                await foreach (TFrame frame in frames.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    byte[] inputFrame = prepareFrame(frame);
                    if (rawInputCanvas is { } input && inputFrame.Length != checked(input.Width * input.Height * 4))
                        throw new InvalidDataException("The raw frame size does not match the input canvas.");
                    long started = Stopwatch.GetTimestamp();
                    writingToEncoder = true;
                    await stdin.WriteAsync(inputFrame, token).ConfigureAwait(false);
                    writingToEncoder = false;
                    onInputWrite?.Invoke(Stopwatch.GetElapsedTime(started));
                }
            }
            catch (Exception error)
            {
                if (error is not OperationCanceledException || !token.IsCancellationRequested)
                    inputFailure.TrySetResult((error, writingToEncoder && error is IOException));
                throw;
            }
        }

        // 按目标帧率生产待编码画面，交由写入循环处理。
        async Task CaptureFramesAsync()
        {
            var clock = new Stopwatch();
            double interval = (double)Stopwatch.Frequency / fps;
            double nextFrame = 0;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    TFrame? frame = await capture(token).ConfigureAwait(false);
                    if (frame is null)
                        break;
                    await frames.Writer.WriteAsync(frame, token).ConfigureAwait(false);
                    if (!clock.IsRunning)
                        clock.Start();
                    onFrameEncodedInput?.Invoke();
                    // 用累计时间保留 30 FPS 的 33⅓ 毫秒间隔，落后时跳过已错过的节拍。
                    long now = clock.ElapsedTicks;
                    nextFrame += interval;
                    if (nextFrame <= now)
                        nextFrame += Math.Floor((now - nextFrame) / interval) * interval;
                    double remainingMs = (nextFrame - now) * 1000 / Stopwatch.Frequency;
                    if (remainingMs > 0)
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(remainingMs)), token).ConfigureAwait(false);
                }
                frames.Writer.TryComplete();
            }
            catch (Exception error)
            {
                if (error is not OperationCanceledException || !token.IsCancellationRequested)
                    inputFailure.TrySetResult((error, false));
                frames.Writer.TryComplete(error);
                throw;
            }
        }
    }
}
