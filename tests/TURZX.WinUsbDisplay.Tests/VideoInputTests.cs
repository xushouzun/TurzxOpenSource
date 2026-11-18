using System.Runtime.CompilerServices;
using TURZX.WinUsbDisplay.Media;
using Xunit;

namespace TURZX.WinUsbDisplay.Tests;

/// <summary>验证视频输入校验、取消、帧率选择和分块传输。</summary>
public class VideoInputTests
{
    [Fact]
    public async Task BackgroundDecoderReportsExitCodeAndDiagnosticsForIncompleteFrame()
    {
        var start = new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (OperatingSystem.IsWindows())
        {
            start.Arguments = "/d /c \"echo incomplete frame 1>&2 & exit /b 7\"";
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("exec 1>&-; sleep 0.1; echo incomplete frame >&2; exit 7");
        }

        await using var decoder = new LoopingVideoFrames(start, 4);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<FfmpegProcessException>(() => decoder.ReadAsync(timeout.Token).AsTask());
        Assert.Equal(7, error.ExitCode);
        Assert.Contains("incomplete frame", error.Diagnostics);
        Assert.IsType<EndOfStreamException>(error.InnerException);
        await Assert.ThrowsAsync<InvalidOperationException>(() => decoder.ReadAsync(timeout.Token).AsTask());
        await decoder.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => decoder.ReadAsync(timeout.Token).AsTask());
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-100, -100)]
    public void BackgroundDecoderRejectsInvalidDimensionsBeforeStartingFfmpeg(int width, int height)
    {
        var model = new DisplayModel("Test", 0, width, height);
        Assert.Throws<ArgumentException>("canvas", () => new LoopingVideoFrames("source.mp4", model, 24));
    }

    [Fact]
    public async Task SingleSlideReusesItsFrameAfterTheInterval()
    {
        int reads = 0;
        await using var slideshow = new SlideshowCapture(["first.png"], TimeSpan.Zero, static bytes => bytes,
            (_, _) => Task.FromResult<byte[]>([(byte)Interlocked.Increment(ref reads)]));

        byte[]? first = await slideshow.ReadFrameAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.Same(first, await slideshow.ReadFrameAsync(CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        Task<byte[]?> next = slideshow.ReadChangedFrameAsync(cancellation.Token).AsTask();
        Assert.False(next.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VideoOutputCanBeRetriedAfterFailureOrCancellation(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            string target = Path.Combine(directory.FullName, "preview.mp4");
            async Task WriteIncompleteOutput(string pending, CancellationToken token)
            {
                Assert.Equal(".mp4", Path.GetExtension(pending));
                await File.WriteAllTextAsync(pending, "partial", token);
                Assert.False(File.Exists(target));
                if (cancel)
                    cancellation.Cancel();
                else
                    throw new InvalidOperationException("Transcoding failed.");
            }

            Task operation = VideoConverter.WriteOutputAsync(target, WriteIncompleteOutput, cancellation.Token);
            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            else
                await Assert.ThrowsAsync<InvalidOperationException>(() => operation);

            Assert.Empty(directory.GetFileSystemInfos());
            await VideoConverter.WriteOutputAsync(target,
                (pending, token) => File.WriteAllTextAsync(pending, "complete", token), CancellationToken.None);
            Assert.Equal("complete", await File.ReadAllTextAsync(target));
            Assert.Single(directory.GetFileSystemInfos());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VideoOutputPreservesDestinationWrittenDuringConversion()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            string target = Path.Combine(directory.FullName, "video.h264");
            await Assert.ThrowsAsync<IOException>(() => VideoConverter.WriteOutputAsync(target, async (pending, token) =>
            {
                await File.WriteAllTextAsync(pending, "conversion", token);
                await File.WriteAllTextAsync(target, "existing", token);
            }, CancellationToken.None));

            Assert.Equal("existing", await File.ReadAllTextAsync(target));
            Assert.Single(directory.GetFileSystemInfos());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void FfmpegCanUseTheEnvironmentWhenItsSettingsFileIsLocked()
    {
        string path = Path.GetTempFileName();
        try
        {
            using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal("ffmpeg-from-environment", VideoConverter.ResolveExecutable(path, "ffmpeg-from-environment"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>验证前景先结束后仍报告背景视频故障。</summary>
    [Fact]
    public async Task LiveSceneReportsVideoFaultEvenWhenForegroundReachesEndFirst()
    {
        using var lifetime = new CancellationTokenSource();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.Token.Register(() => stopped.SetResult());
        var error = new IOException("USB transfer failed while the foreground ended.");
        Task video = FailAfterStopAsync();

        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            LiveScene.RunLayersAsync(video, Task.CompletedTask, () => Task.CompletedTask, null, lifetime));
        Assert.Same(error, thrown);

        // 等待场景请求停止后再模拟背景故障。
        async Task FailAfterStopAsync()
        {
            await stopped.Task;
            throw error;
        }
    }

    /// <summary>验证前景结束后忽略背景任务的预期取消。</summary>
    [Fact]
    public async Task LiveSceneIgnoresExpectedBackgroundCancellationAfterForegroundEnds()
    {
        using var lifetime = new CancellationTokenSource();
        Task video = Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
        await LiveScene.RunLayersAsync(video, Task.CompletedTask, () => Task.CompletedTask, null, lifetime);
        Assert.True(video.IsCanceled);
    }

    [Fact]
    public async Task LiveSceneReportsDeviceStopFailureAfterForegroundCancellation()
    {
        using var lifetime = new CancellationTokenSource();
        var video = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new PlaybackStopException(new IOException("USB stop command failed."));
        Task scene = LiveScene.RunLayersAsync(video.Task, Task.CompletedTask, () =>
        {
            lifetime.Cancel();
            return Task.FromCanceled(lifetime.Token);
        }, null, lifetime);

        video.SetException(error);
        Assert.Same(error, await Assert.ThrowsAsync<PlaybackStopException>(() => scene));
    }

    /// <summary>验证空轮播正常结束并允许重复释放。</summary>
    [Fact]
    public async Task EmptySlideshowEndsAndCanBeDisposedAgain()
    {
        await using var slideshow = new SlideshowCapture([], TimeSpan.FromSeconds(1), static bytes => bytes);
        Assert.Null(await slideshow.ReadFrameAsync(CancellationToken.None));
        await slideshow.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => slideshow.ReadFrameAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task SlideshowDisposalDoesNotWaitForABlockedPrefetch()
    {
        var prefetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePrefetch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var slideshow = new SlideshowCapture(["first.png", "second.png"], TimeSpan.FromSeconds(1),
            bytes => bytes[0] == 1 ? bytes : throw new InvalidOperationException("Cancelled prefetch was encoded."),
            (path, _) =>
            {
                if (path == "first.png")
                    return Task.FromResult<byte[]>([1]);
                prefetchStarted.SetResult();
                releasePrefetch.Task.GetAwaiter().GetResult();
                return Task.FromResult<byte[]>([2]);
            });
        try
        {
            Assert.Equal(new byte[] { 1 }, await slideshow.ReadFrameAsync(timeout.Token));
            await prefetchStarted.Task.WaitAsync(timeout.Token);
            await slideshow.DisposeAsync().AsTask().WaitAsync(timeout.Token);
            Assert.False(releasePrefetch.Task.IsCompleted);
        }
        finally
        {
            releasePrefetch.TrySetResult();
        }
    }

    [Fact]
    public async Task SlideshowCanRetryAfterCancellingABlockedFirstRead()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var first = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        int reads = 0;
        await using var slideshow = new SlideshowCapture(["first.png"], TimeSpan.FromSeconds(1), bytes => bytes,
            (_, _) =>
            {
                if (Interlocked.Increment(ref reads) != 1)
                    return Task.FromResult<byte[]>([2]);
                firstStarted.SetResult();
                releaseFirst.Task.GetAwaiter().GetResult();
                return Task.FromResult<byte[]>([1]);
            });
        try
        {
            Task<byte[]?> pending = slideshow.ReadFrameAsync(first.Token).AsTask();
            await firstStarted.Task.WaitAsync(timeout.Token);
            first.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(timeout.Token));
            Assert.Equal(new byte[] { 2 }, await slideshow.ReadFrameAsync(timeout.Token));
        }
        finally
        {
            releaseFirst.TrySetResult();
        }
    }

    /// <summary>验证轮播会保留构造时路径列表的副本。</summary>
    [Fact]
    public async Task SlideshowKeepsItsPlaylistWhenTheCallerChangesTheList()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var paths = new List<string> { path };
            await using var slideshow = new SlideshowCapture(paths, TimeSpan.FromSeconds(1), static bytes => bytes);
            paths.Clear();
            Assert.Equal(new byte[] { 1, 2, 3 }, await slideshow.ReadFrameAsync(CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>验证校验截断的 Annex B 前缀后恢复流位置。</summary>
    [Fact]
    public void TruncatedAnnexBPrefixPreservesCallerStreamPosition()
    {
        // 会话访问 USB 传输层之前会先验证输入。
        var session = (DisplaySession)RuntimeHelpers.GetUninitializedObject(typeof(DisplaySession));
        using var input = new MemoryStream(new byte[] { 9, 0, 0, 1 });
        input.Position = 1;

        Assert.Throws<EndOfStreamException>(() => session.PlayVideo(input, 24));

        Assert.Equal(1, input.Position);
    }

    /// <summary>验证已取消时无需读取视频前缀。</summary>
    [Fact]
    public void CancelledVideoDoesNotReadTheStreamPrefix()
    {
        var session = (DisplaySession)RuntimeHelpers.GetUninitializedObject(typeof(DisplaySession));
        using var input = new MemoryStream();
        Assert.Throws<OperationCanceledException>(() => session.PlayVideo(input, 24,
            cancellation: new CancellationToken(canceled: true)));
        Assert.Equal(0, input.Position);
    }

    /// <summary>验证取消不可定位视频时无需等待空闲输入。</summary>
    [Fact]
    public async Task CancellingNonSeekableVideoDoesNotWaitForAnIdleInput()
    {
        var session = (DisplaySession)RuntimeHelpers.GetUninitializedObject(typeof(DisplaySession));
        using var input = new UnresponsiveStream();
        using var cancellation = new CancellationTokenSource();
        Task playback = Task.Run(() => session.PlayVideo(input, 24, cancellation: cancellation.Token));
        try
        {
            await input.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playback.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(input.ReadResult.Task.IsCompleted);
        }
        finally
        {
            input.ReadResult.TrySetResult(0);
            await playback.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task CancellingVideoDoesNotWaitForASynchronouslyBlockedRead(bool canSeek, bool liveBlocks)
    {
        var session = (DisplaySession)RuntimeHelpers.GetUninitializedObject(typeof(DisplaySession));
        using var input = new BlockingReadStream(canSeek);
        using var cancellation = new CancellationTokenSource();
        Task playback = Task.Run(() =>
        {
            if (liveBlocks)
                DisplaySession.SendLiveVideoBlocks(input, [0, 0, 0, 1], new byte[128], cancellation.Token,
                    (_, _) => throw new InvalidOperationException("Unexpected video block."));
            else
                session.PlayVideo(input, 24, cancellation: cancellation.Token);
        });
        try
        {
            await input.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playback.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(input.ReleaseRead.Task.IsCompleted);
        }
        finally
        {
            input.ReleaseRead.TrySetResult();
            await playback.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    private sealed class BlockingReadStream(bool canSeek) : MemoryStream
    {
        public override bool CanSeek => canSeek;
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override int Read(Span<byte> buffer)
        {
            ReadStarted.TrySetResult();
            ReleaseRead.Task.GetAwaiter().GetResult();
            return 0;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));
    }

    /// <summary>验证无效的背景视频前缀在设备状态变化前被拒绝。</summary>
    [Fact]
    public async Task InvalidBackgroundPrefixIsRejectedBeforeChangingDeviceState()
    {
        var session = (DisplaySession)RuntimeHelpers.GetUninitializedObject(typeof(DisplaySession));
        using var input = new MemoryStream([9, 0, 0, 0, 2]) { Position = 1 };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LiveScene.RunAsync(session, capture: null, input, backgroundFps: 24, captureFps: 30, CancellationToken.None));
        Assert.Equal(1, input.Position);
    }

    /// <summary>验证直接播放会在读取视频之前拒绝超出设备范围的帧率。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    [InlineData(255)]
    public void InvalidVideoFrameRateIsRejectedBeforeReadingInput(byte fps)
    {
        var session = (DisplaySession)RuntimeHelpers.GetUninitializedObject(typeof(DisplaySession));
        using var input = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>("fps", () => session.PlayVideo(input, fps));
    }

    /// <summary>验证背景帧率无效时不发送设备命令。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    [InlineData(255)]
    public async Task InvalidBackgroundFrameRateIsRejectedBeforeSendingAnyDeviceCommands(byte fps)
    {
        var session = (DisplaySession)RuntimeHelpers.GetUninitializedObject(typeof(DisplaySession));
        using var input = new MemoryStream([0, 0, 0, 1]);
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            LiveScene.RunAsync(session, capture: null, input, backgroundFps: fps, captureFps: 30, CancellationToken.None));
        Assert.Equal("backgroundFps", error.ParamName);
    }

    [Theory]
    [InlineData(17, 12)]
    [InlineData(122, 9)]
    public void VerifiedVideoQuerySkipsUnrelatedAndShortReplies(byte command, int length)
    {
        var matching = new byte[length];
        matching[0] = command;
        var unrelated = (byte[])matching.Clone();
        unrelated[0] = 10;
        var replies = new Queue<byte[]>([unrelated, [command], matching]);

        Assert.Same(matching, DisplaySession.RetryVerifiedQuery(new Lock(), command, length,
            replies.Dequeue, () => { }, CancellationToken.None));
        Assert.Empty(replies);
    }

    /// <summary>验证块应答可在有无命令回显时读取队列深度。</summary>
    [Theory]
    [InlineData(121)]
    [InlineData(0)]
    public void VideoBlockReadsQueueDepthWithoutRequiringCommandEcho(byte firstByte)
    {
        byte[] reply = [firstByte, 0, 0, 0, 0, 0, 0, 0, 3];
        Assert.Equal(3, DisplaySession.ReadVideoBlockQueueDepth(reply));
    }

    /// <summary>验证短应答被拒绝，并保留原始字节供排查。</summary>
    [Fact]
    public void VideoBlockRejectsAcknowledgementWithoutQueueDepth()
    {
        byte[] reply = [121, 0, 0, 0, 0, 0, 0, 0];
        var error = Assert.Throws<DeviceResponseException>(() => DisplaySession.ReadVideoBlockQueueDepth(reply));
        Assert.Equal(DeviceResponseFailure.UnexpectedReply, error.Failure);
        Assert.Equal(reply, error.Response.ToArray());
    }

    /// <summary>验证帧率取自实际选中视频流。</summary>
    [Fact]
    public void FrameRateUsesTheSelectedVideoStreamInsteadOfTheCoverOrOtherTracks()
    {
        const string description = """
            Input #0, mov,mp4, from 'sample.mp4':
              Stream #0:0: Video: mjpeg, 640x640, 90k tbr (attached pic)
              Stream #0:1[0x2](und): Video: h264, 1920x1080, 29.97 fps, 30 tbr
              Stream #0:2: Video: h264, 320x240, 24 fps
            Stream mapping:
              Stream #0:1 -> #0:0 (copy)
            Output #0, null, to 'pipe:':
              Stream #0:0: Video: h264, 1920x1080, 30 fps
            """;
        Assert.Equal(29.97, VideoConverter.ParseSelectedFrameRate(description));
    }

    /// <summary>验证不会使用输出流或其他轨道的帧率。</summary>
    [Fact]
    public void FrameRateDoesNotBorrowTheOutputOrAnotherTracksRate()
    {
        const string description = """
            Input #0, matroska, from 'sample.mkv':
              Stream #0:0: Video: h264, 1920x1080, 1k tbr
              Stream #0:1: Video: h264, 320x240, 24 fps
            Output #0, null, to 'pipe:':
              Stream #0:0: Video: h264, 1920x1080, 25 fps
            Stream mapping:
              Stream #0:0 -> #0:0 (copy)
            """;
        Assert.Null(VideoConverter.ParseSelectedFrameRate(description));
    }

    /// <summary>验证等待后续输入时仍可发送已缓冲的视频字节。</summary>
    [Fact]
    public async Task LiveStreamSendsBufferedBytesWhileWaitingForMoreInput()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var input = new PausedStream();
        var sent = new List<(byte[] Data, bool Final)>();
        await Task.Run(() => DisplaySession.SendLiveVideoBlocks(input, [0, 0, 0, 1], new byte[128],
            cancellation.Token, (block, final) =>
            {
                sent.Add((block.ToArray(), final));
                input.End();
            }));

        Assert.Collection(sent,
            block => { Assert.Equal(new byte[] { 0, 0, 0, 1, 2, 3 }, block.Data); Assert.False(block.Final); },
            block => { Assert.Equal(new byte[] { 4 }, block.Data); Assert.True(block.Final); });
    }

    /// <summary>验证实时视频块遵守大小上限，且仅最后一块标记结束。</summary>
    [Fact]
    public void LiveStreamRespectsTheBlockLimitAndMarksOnlyTheLastBlock()
    {
        using var input = new MemoryStream([2, 3, 4, 5, 6, 7, 8, 9]);
        var sent = new List<(byte[] Data, bool Final)>();
        DisplaySession.SendLiveVideoBlocks(input, [0, 0, 0, 1], new byte[5],
            CancellationToken.None, (block, final) => sent.Add((block.ToArray(), final)));

        Assert.Collection(sent,
            block => { Assert.Equal(new byte[] { 0, 0, 0, 1, 2 }, block.Data); Assert.False(block.Final); },
            block => { Assert.Equal(new byte[] { 3, 4, 5, 6, 7 }, block.Data); Assert.False(block.Final); },
            block => { Assert.Equal(new byte[] { 8, 9 }, block.Data); Assert.True(block.Final); });
    }

    /// <summary>提供暂停输入和受控结束时机的测试流。</summary>
    private sealed class PausedStream : MemoryStream
    {
        /// <summary>控制测试流何时返回文件末尾。</summary>
        private readonly TaskCompletionSource<int> end = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>记录测试流已读取的状态。</summary>
        private bool read;

        /// <summary>令等待中的后续读取达到流末尾。</summary>
        internal void End() => end.TrySetResult(0);

        /// <summary>分阶段返回前缀数据及流末尾。</summary>
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (read)
                return new(end.Task.WaitAsync(cancellationToken));
            read = true;
            new byte[] { 2, 3, 4 }.CopyTo(buffer);
            return ValueTask.FromResult(3);
        }
    }

    /// <summary>模拟忽略取消请求且不会立即完成读取的输入流。</summary>
    private sealed class UnresponsiveStream : MemoryStream
    {
        /// <summary>声明测试流不支持定位。</summary>
        public override bool CanSeek => false;
        /// <summary>指示测试流已收到读取请求。</summary>
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>控制挂起读取的完成结果。</summary>
        internal TaskCompletionSource<int> ReadResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>触发读取信号并返回受测试控制的等待任务。</summary>
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            return new(ReadResult.Task);
        }
    }
}
