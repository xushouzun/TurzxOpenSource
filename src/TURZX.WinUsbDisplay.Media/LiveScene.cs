using System.Diagnostics;
using System.Threading.Channels;

namespace TURZX.WinUsbDisplay.Media;

/// <summary>协调实时前景帧和可选的设备端背景视频。</summary>
public static class LiveScene
{
    /// <summary>按指定帧率发送实时前景，并可同时播放背景视频。</summary>
    public static Task RunAsync(DisplaySession session, Func<CancellationToken, ValueTask<byte[]?>>? capture,
        Stream? background, byte backgroundFps, int captureFps, CancellationToken cancellation) =>
        RunAsync(session, capture, background, backgroundFps, captureFps, cancellation, null, null);

    /// <summary>播放实时场景，并可在背景启动和前景帧发送后接收通知。</summary>
    public static Task RunAsync(DisplaySession session, Func<CancellationToken, ValueTask<byte[]?>>? capture,
        Stream? background, byte backgroundFps, int captureFps, CancellationToken cancellation,
        Action? backgroundStarted, Func<byte[], ValueTask>? foregroundSent) =>
        RunAsync(session, capture, background, backgroundFps, captureFps, cancellation,
            backgroundStarted, foregroundSent, delayAfterFrame: false);

    /// <summary>播放实时场景，可选择在发送前景帧之后再等待一个取帧间隔。</summary>
    public static Task RunAsync(DisplaySession session, Func<CancellationToken, ValueTask<byte[]?>>? capture,
        Stream? background, byte backgroundFps, int captureFps, CancellationToken cancellation,
        Action? backgroundStarted, Func<byte[], ValueTask>? foregroundSent, bool delayAfterFrame) =>
        RunAsync(session, capture, background, backgroundFps, captureFps, cancellation,
            backgroundStarted, foregroundSent, delayAfterFrame, bufferLatestForeground: false);

    /// <summary>播放实时场景，可在发送繁忙时仅保留最新的待发送前景帧。</summary>
    public static Task RunAsync(DisplaySession session, Func<CancellationToken, ValueTask<byte[]?>>? capture,
        Stream? background, byte backgroundFps, int captureFps, CancellationToken cancellation,
        Action? backgroundStarted, Func<byte[], ValueTask>? foregroundSent, bool delayAfterFrame, bool bufferLatestForeground) =>
        RunAsync(session, capture, background, backgroundFps, captureFps, cancellation,
            backgroundStarted, foregroundSent, delayAfterFrame, bufferLatestForeground, adaptiveCapture: false);

    /// <summary>播放实时场景，可根据捕捉和发送耗时自适应调整取帧频率。</summary>
    public static Task RunAsync(DisplaySession session, Func<CancellationToken, ValueTask<byte[]?>>? capture,
        Stream? background, byte backgroundFps, int captureFps, CancellationToken cancellation,
        Action? backgroundStarted, Func<byte[], ValueTask>? foregroundSent, bool delayAfterFrame,
        bool bufferLatestForeground, bool adaptiveCapture) =>
        RunAsync(session, capture, background, backgroundFps, captureFps, cancellation, backgroundStarted,
            foregroundSent, delayAfterFrame, bufferLatestForeground, adaptiveCapture, deferForeground: false);

    /// <summary>持续发送所选前景和背景，直到播放结束或被取消。</summary>
    /// <param name="session">接收前景和视频数据的设备会话。</param>
    /// <param name="capture">生成可直接送屏的已编码前景帧。回调为空时省略前景，返回空帧时结束捕捉。</param>
    /// <param name="background">可选的背景视频流。</param>
    /// <param name="backgroundFps">背景视频的播放帧率，范围为 1 至 60 FPS。</param>
    /// <param name="captureFps">前景捕捉帧率，范围为 1 至 60 FPS。</param>
    /// <param name="cancellation">用于取消前景捕捉和视频播放。</param>
    /// <param name="backgroundStarted">设备开始播放背景视频后调用。</param>
    /// <param name="foregroundSent">设备确认收到前景帧后调用。</param>
    /// <param name="delayAfterFrame">为 true 时，每次发送前景帧后等待一个完整帧间隔。</param>
    /// <param name="bufferLatestForeground">允许捕捉与发送并行，最多缓存一帧待发送数据。</param>
    /// <param name="adaptiveCapture">根据实测处理耗时调整缓冲模式的捕捉间隔。</param>
    /// <param name="deferForeground">为 true 时先启动背景，再请求首张前景帧。</param>
    /// <param name="onForegroundExchange">可选的前景帧 USB 往返耗时回调。</param>
    /// <param name="onJpegTransfer">可选的 JPEG 写入与应答等待耗时回调。</param>
    /// <param name="maximumBackgroundBlockBytes">背景视频单次传输的数据块上限，单位为字节。</param>
    public static async Task RunAsync(DisplaySession session, Func<CancellationToken, ValueTask<byte[]?>>? capture,
        Stream? background, byte backgroundFps, int captureFps, CancellationToken cancellation,
        Action? backgroundStarted, Func<byte[], ValueTask>? foregroundSent, bool delayAfterFrame,
        bool bufferLatestForeground, bool adaptiveCapture, bool deferForeground, Action<TimeSpan>? onForegroundExchange = null,
        Action<TimeSpan, TimeSpan>? onJpegTransfer = null, int maximumBackgroundBlockBytes = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentOutOfRangeException.ThrowIfLessThan(captureFps, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(captureFps, 60);
        if (background is null && capture is null)
            throw new ArgumentException("Select a foreground source or a video background.");
        if (background is not null)
        {
            if (!background.CanRead || !background.CanSeek)
                throw new ArgumentException("A looping background must be readable and seekable.", nameof(background));
            ArgumentOutOfRangeException.ThrowIfZero(backgroundFps);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(backgroundFps, (byte)60);
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumBackgroundBlockBytes, 4);
            DisplaySession.ValidateSeekableVideo(background, cancellation);
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var token = lifetime.Token;
        token.ThrowIfCancellationRequested();
        var pacing = adaptiveCapture && bufferLatestForeground && !delayAfterFrame
            ? new AdaptiveCapturePacing(captureFps) : null;
        // 参数和视频前缀均已验证，此时才清理设备上的旧视频和时钟。
        session.StopVideo();
        session.ShowDefaultClock(false);
        if (background is null)
        {
            await CaptureAsync().ConfigureAwait(false);
            return;
        }

        // 前景可先就绪时先发首帧并留出短暂间隔，再启动背景解码。
        if (capture is not null && !deferForeground)
        {
            token.ThrowIfCancellationRequested();
            byte[]? firstFrame = await capture(token).ConfigureAwait(false);
            if (firstFrame is null)
                return;

            token.ThrowIfCancellationRequested();
            await SendForegroundAsync(firstFrame).ConfigureAwait(false);
            await Task.Delay(100, token).ConfigureAwait(false);
        }

        // 等待设备确认背景首块后再进入持续取帧，由两个图层共用取消信号。
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task video = Task.Run(() =>
            session.PlayVideo(background, backgroundFps, true, token, () => ready.TrySetResult(),
                resetPlayback: false, maximumBlockBytes: maximumBackgroundBlockBytes));
        await RunLayersAsync(video, ready.Task, capture is null ? null : CaptureAsync, backgroundStarted, lifetime)
            .ConfigureAwait(false);

        // 选择串行捕捉或带最新帧缓冲的捕捉路径。
        Task CaptureAsync()
        {
            var interval = TimeSpan.FromSeconds(1d / captureFps);
            return bufferLatestForeground && !delayAfterFrame
                ? CaptureLatestAsync(interval) : CaptureSerialAsync(interval);
        }

        // 串行取帧和送屏，根据配置维持捕捉节拍。
        async Task CaptureSerialAsync(TimeSpan interval)
        {
            using var timer = delayAfterFrame ? null : new PeriodicTimer(interval);
            do
            {
                token.ThrowIfCancellationRequested();
                byte[]? frame = await capture!(token).ConfigureAwait(false);
                if (frame is null)
                    return;
                await SendForegroundAsync(frame).ConfigureAwait(false);
                if (delayAfterFrame)
                    await Task.Delay(interval, token).ConfigureAwait(false);
            } while (timer is null || await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }

        // 连续取帧并保留最新待发送帧。
        async Task CaptureLatestAsync(TimeSpan interval)
        {
            using var captureLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            var frameToken = captureLifetime.Token;
            // 捕捉快于 USB 发送时，用新帧替换尚未发送的旧帧，避免延迟累积。
            var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
            {
                SingleWriter = true,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });

            Task producer = Task.Run(async () =>
            {
                try
                {
                    long epoch = Stopwatch.GetTimestamp();
                    double intervalTicks = interval.TotalSeconds * Stopwatch.Frequency;
                    double nextFrame = intervalTicks;
                    while (true)
                    {
                        frameToken.ThrowIfCancellationRequested();
                        long started = Stopwatch.GetTimestamp();
                        byte[]? frame = await capture!(frameToken).ConfigureAwait(false);
                        pacing?.ObserveCapture(Stopwatch.GetElapsedTime(started));
                        if (frame is null)
                            break;
                        frames.Writer.TryWrite(frame);
                        if (pacing is not null)
                        {
                            TimeSpan wait = pacing.GetInterval() - Stopwatch.GetElapsedTime(started);
                            if (wait > TimeSpan.Zero)
                                await Task.Delay(wait, frameToken).ConfigureAwait(false);
                        }
                        else
                        {
                            // 按累计时间调度，避免 33⅓ ms 的周期被定时器截断为 33 ms。
                            long elapsed = Stopwatch.GetTimestamp() - epoch;
                            if (nextFrame < elapsed)
                                nextFrame += Math.Floor((elapsed - nextFrame) / intervalTicks) * intervalTicks;
                            double waitMs = (nextFrame - elapsed) * 1000 / Stopwatch.Frequency;
                            if (waitMs > 0)
                                await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(waitMs)), frameToken).ConfigureAwait(false);
                            nextFrame += intervalTicks;
                        }
                    }
                    frames.Writer.TryComplete();
                }
                catch (Exception error)
                {
                    frames.Writer.TryComplete(error);
                }
            });

            try
            {
                await foreach (byte[] frame in frames.Reader.ReadAllAsync(frameToken).ConfigureAwait(false))
                {
                    frameToken.ThrowIfCancellationRequested();
                    await SendForegroundAsync(frame).ConfigureAwait(false);
                }
            }
            finally
            {
                captureLifetime.Cancel();
                await producer.ConfigureAwait(false);
            }
        }

        // 发送前景帧，并在设备确认后更新指标和回调。
        async ValueTask SendForegroundAsync(byte[] frame)
        {
            long started = Stopwatch.GetTimestamp();
            if (background is null && frame.AsSpan().StartsWith(DisplaySession.JpegSignature))
                session.ShowJpegFrame(frame, onJpegTransfer);
            else
                session.SendEncodedOverlay(frame);
            onForegroundExchange?.Invoke(Stopwatch.GetElapsedTime(started));
            if (foregroundSent is not null)
                await foregroundSent(frame).ConfigureAwait(false);
            pacing?.ObserveSend(Stopwatch.GetElapsedTime(started));
        }
    }

    /// <summary>等待背景就绪后启动前景，任一图层结束时取消并等待另一层。</summary>
    internal static async Task RunLayersAsync(Task video, Task ready, Func<Task>? startForeground,
        Action? backgroundStarted, CancellationTokenSource lifetime)
    {
        Task? foreground = null;
        Task shutdown = Task.CompletedTask;
        OperationCanceledException? cancellation = null;
        try
        {
            // 同时观察启动信号和播放任务，背景提前结束时也能退出等待。
            await Task.WhenAny(ready, video).ConfigureAwait(false);
            if (video.IsCompleted)
                await video.ConfigureAwait(false);
            else
            {
                await ready.ConfigureAwait(false);
                backgroundStarted?.Invoke();
                if (startForeground is null)
                    await video.ConfigureAwait(false);
                else
                {
                    foreground = startForeground();
                    Task finished = await Task.WhenAny(video, foreground).ConfigureAwait(false);
                    await finished.ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException error) when (lifetime.IsCancellationRequested)
        {
            cancellation = error;
        }
        finally
        {
            shutdown = lifetime.CancelAsync();
            await shutdown.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await video.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (foreground is not null)
                await foreground.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        // 两层都结束后，向调用方报告另一层的实际故障。
        // 先结束的图层读到 EOF 时，另一层被取消属于正常情况。
        if (video.IsFaulted)
            await video.ConfigureAwait(false);
        if (foreground is { IsFaulted: true })
            await foreground.ConfigureAwait(false);
        if (cancellation is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(cancellation);
        await shutdown.ConfigureAwait(false);
    }
}
