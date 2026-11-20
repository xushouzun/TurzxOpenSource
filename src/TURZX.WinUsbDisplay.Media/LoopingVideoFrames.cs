using System.Diagnostics;

namespace TURZX.WinUsbDisplay.Media;

/// <summary>循环解码背景视频，逐帧输出指定尺寸的 BGR32 像素。</summary>
public sealed class LoopingVideoFrames : IAsyncDisposable
{
    private static readonly TimeSpan ExitGracePeriod = TimeSpan.FromSeconds(1);
    /// <summary>将背景视频解码成原始像素的 FFmpeg 进程。</summary>
    private readonly Process decoder;
    private readonly Task decoderExit;
    /// <summary>持续读取 FFmpeg 标准错误的任务。</summary>
    private readonly ProcessDiagnostics diagnostics;
    /// <summary>一帧 BGR32 画面所占的字节数。</summary>
    private readonly int frameBytes;
    /// <summary>隔离管道读取与调用方可立即复用的缓冲区。</summary>
    private byte[]? pendingFrame;
    /// <summary>指示帧读取中断后不能继续使用解码器。</summary>
    private bool interrupted;
    private bool disposed;

    /// <summary>启动循环解码进程，并将视频按指定帧率和画布尺寸输出。</summary>
    /// <param name="path">源视频文件路径。</param>
    /// <param name="canvas">输出画布的像素尺寸。</param>
    /// <param name="fps">解码输出帧率，须大于零。</param>
    /// <param name="layout">可选画布布局。为空时直接缩放至画布宽高。</param>
    public LoopingVideoFrames(string path, DisplayModel canvas, byte fps, MediaLayout? layout = null)
        : this(CreateDecoderStartInfo(path, canvas, fps, layout), checked(canvas.Width * canvas.Height * 4))
    {
    }

    internal LoopingVideoFrames(ProcessStartInfo start, int frameBytes)
    {
        this.frameBytes = frameBytes;
        decoder = VideoConverter.StartFfmpeg(start);
        diagnostics = new ProcessDiagnostics(decoder.StandardError);
        decoderExit = decoder.WaitForExitAsync();
    }

    private static ProcessStartInfo CreateDecoderStartInfo(string path, DisplayModel canvas, byte fps, MediaLayout? layout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentOutOfRangeException.ThrowIfZero(fps);
        if (canvas.Width <= 0 || canvas.Height <= 0)
            throw new ArgumentException("Video dimensions must be positive.", nameof(canvas));
        var start = new ProcessStartInfo(VideoConverter.ResolveExecutable())
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        string scale = layout is null
            ? FormattableString.Invariant($"scale={canvas.Width}:{canvas.Height}")
            : VideoConverter.BuildLayoutFilter(canvas, layout);
        foreach (string arg in new[] { "-nostdin", "-loglevel", "error", "-stream_loop", "-1", "-threads", "2",
            "-i", Path.GetFullPath(path), "-map", VideoConverter.PrimaryVideoStream, "-vf", FormattableString.Invariant($"fps={fps},{scale}"),
            "-an", "-threads", "1", "-pix_fmt", "bgr0", "-f", "rawvideo", "pipe:1" })
            start.ArgumentList.Add(arg);
        return start;
    }

    /// <summary>读取一个完整帧。调用方须串行读取。</summary>
    public async ValueTask<byte[]> ReadAsync(CancellationToken cancellation)
    {
        CheckCanRead(cancellation);
        var pixels = GC.AllocateUninitializedArray<byte>(frameBytes);
        await ReadFrameAsync(pixels, cancellation).ConfigureAwait(false);
        return pixels;
    }

    /// <summary>将一个完整帧读入调用方提供的缓冲区。调用方须串行读取。取消进行中的读取会停止解码器并保留调用方的缓冲区内容。</summary>
    public async ValueTask ReadAsync(Memory<byte> pixels, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (pixels.Length != frameBytes)
            throw new ArgumentException("The buffer size must match one video frame.", nameof(pixels));
        CheckCanRead(cancellation);

        // 整帧成功后再交给调用方。关闭管道后迟到的读取也不会写入调用方的内存。
        var frame = pendingFrame ??= GC.AllocateUninitializedArray<byte>(frameBytes);
        await ReadFrameAsync(frame, cancellation).ConfigureAwait(false);
        frame.AsMemory().CopyTo(pixels);
    }

    private void CheckCanRead(CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellation.ThrowIfCancellationRequested();
        if (interrupted)
            throw new InvalidOperationException("The decoder was stopped after an interrupted frame read.");
    }

    private async ValueTask ReadFrameAsync(Memory<byte> pixels, CancellationToken cancellation)
    {
        Task read = decoder.StandardOutput.BaseStream.ReadExactlyAsync(pixels, cancellation).AsTask();
        try
        {
            if (!read.IsCompleted
                && await Task.WhenAny(read, decoderExit).WaitAsync(cancellation).ConfigureAwait(false) == decoderExit)
            {
                // 正常退出后仍可能读到最后一帧，但继承的管道不能阻止检测解码器退出。
                try { await read.WaitAsync(ExitGracePeriod, cancellation).ConfigureAwait(false); }
                catch (TimeoutException error)
                {
                    CloseOutputAndObserveRead(read);
                    throw new EndOfStreamException("The decoder exited with its output pipe still open.", error);
                }
            }
            else
                await read.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 子进程可能仍持有输出管道。关闭本地读端，取消时不等待迟到的管道读取。
            interrupted = true;
            try { StopDecoder(); }
            finally { CloseOutputAndObserveRead(read); }
            throw;
        }
        catch (EndOfStreamException error)
        {
            int? exitCode = null;
            try
            {
                // 输出管道可能先于进程退出关闭，先给 FFmpeg 时间报告真实的退出状态。
                using var exitDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                exitDeadline.CancelAfter(ExitGracePeriod);
                await decoderExit.WaitAsync(exitDeadline.Token).ConfigureAwait(false);
                exitCode = decoder.ExitCode;
                await diagnostics.Completion.WaitAsync(exitDeadline.Token)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                // 帧输出已经结束，继续等待无法取得完整帧。
            }
            finally
            {
                interrupted = true;
                StopDecoder();
            }

            cancellation.ThrowIfCancellationRequested();
            string details = diagnostics.Text.Trim();
            if (exitCode is int code && code != 0)
                throw new FfmpegProcessException(code, details, error);
            throw new IOException($"The background decoder stopped before returning a complete frame. {details}".TrimEnd(), error);
        }
    }

    private void CloseOutputAndObserveRead(Task read)
    {
        try { decoder.StandardOutput.Dispose(); }
        finally { _ = ObserveReadAsync(read); }
    }

    private static async Task ObserveReadAsync(Task read) =>
        await read.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    /// <summary>停止解码进程，等待退出并收尾诊断流读取。</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        using (decoder)
        using (decoder.StandardOutput)
        using (decoder.StandardError)
        {
            StopDecoder();
            await decoderExit.ConfigureAwait(false);
            // 子进程可能仍持有标准错误管道。关闭本地读端可以结束等待。
            await VideoConverter.FinishDiagnosticsAsync(decoder.StandardError, diagnostics.Completion).ConfigureAwait(false);
        }
    }

    /// <summary>终止 FFmpeg 及其子进程以关闭输出管道。</summary>
    private void StopDecoder()
    {
        if (decoder.HasExited)
            return;
        try { decoder.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (decoder.HasExited) { }
    }
}
