using System.Diagnostics;
using TURZX.WinUsbDisplay.Media;
using Xunit;

namespace TURZX.WinUsbDisplay.Tests;

public class LifecycleTests
{
    [Fact]
    public async Task SlideshowRetriesAFileAfterItsPrefetchFails()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int attempts = 0;
        await using var slideshow = new SlideshowCapture(["first.png", "second.png"], TimeSpan.Zero,
            bytes => bytes, (path, _) => path == "first.png" ? Task.FromResult<byte[]>([1])
                : Interlocked.Increment(ref attempts) == 1 ? Task.FromException<byte[]>(new FileNotFoundException(path))
                : Task.FromResult<byte[]>([2]));

        Assert.Equal(new byte[] { 1 }, await slideshow.ReadChangedFrameAsync(deadline.Token));
        await Assert.ThrowsAsync<FileNotFoundException>(() => slideshow.ReadChangedFrameAsync(deadline.Token).AsTask());
        Assert.Equal(new byte[] { 2 }, await slideshow.ReadChangedFrameAsync(deadline.Token));
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DisposingSlideshowCancelsPendingChange(int slideCount)
    {
        using var cleanup = new CancellationTokenSource();
        await using var slideshow = new SlideshowCapture(
            Enumerable.Range(0, slideCount).Select(i => $"{i}.png").ToArray(), TimeSpan.FromHours(1),
            bytes => bytes, (_, _) => Task.FromResult<byte[]>([1]));
        await slideshow.ReadFrameAsync(cleanup.Token);
        Task<byte[]?> pending = slideshow.ReadChangedFrameAsync(cleanup.Token).AsTask();
        Assert.False(pending.IsCompleted);
        try
        {
            await slideshow.DisposeAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            cleanup.Cancel();
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task DisposingSlideshowCancelsBlockedFirstReadBeforeEncoding()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        int encoded = 0;
        using var cleanup = new CancellationTokenSource();
        await using var slideshow = new SlideshowCapture(["first.png"], TimeSpan.Zero,
            bytes => { Interlocked.Increment(ref encoded); return bytes; },
            (_, _) => { started.SetResult(); return release.Task; });
        Task<byte[]?> pending = slideshow.ReadFrameAsync(cleanup.Token).AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await slideshow.DisposeAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
            release.SetResult([1]);
            Assert.Equal(0, Volatile.Read(ref encoded));
        }
        finally
        {
            cleanup.Cancel();
            release.TrySetResult([1]);
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    public async Task MediaCancellationAndCleanupDoNotWaitForInheritedPipes(bool cancel, bool finishDiagnostics,
        bool readDecoderOutput = false)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var directory = Directory.CreateTempSubdirectory();
        string childId = Path.Combine(directory.FullName, "child.pid");
        string childScript = Path.Combine(directory.FullName, "child.ps1");
        string parentScript = Path.Combine(directory.FullName, "parent.cmd");
        using var cancellation = new CancellationTokenSource();
        Task<string>? conversion = null;
        try
        {
            await File.WriteAllTextAsync(childScript, $"[IO.File]::WriteAllText('{childId.Replace("'", "''")}', [string]$PID)\nStart-Sleep -Seconds 60\n");
            await File.WriteAllTextAsync(parentScript,
                $"@echo off\r\nstart \"\" /b powershell.exe -NoProfile -WindowStyle Hidden -File \"{childScript}\"\r\n:wait\r\nif not exist \"{childId}\" goto wait\r\necho wrapper diagnostics 1>&2\r\n"
                + (readDecoderOutput ? "echo x\r\n" : "") + "exit /b 7\r\n");
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                RedirectStandardOutput = readDecoderOutput,
                Arguments = $"/d /c \"\"{parentScript}\"\""
            };
            var pixels = new byte[32];
            conversion = readDecoderOutput ? ReadDecoderFrameAsync()
                : finishDiagnostics ? FinishInheritedDiagnosticsAsync() : VideoConverter.RunAsync(start, cancellation.Token);

            async Task<string> ReadDecoderFrameAsync()
            {
                await using var decoder = new LoopingVideoFrames(start, pixels.Length);
                await decoder.ReadAsync(pixels, cancel ? cancellation.Token : CancellationToken.None);
                return "unexpected frame";
            }

            async Task<string> FinishInheritedDiagnosticsAsync()
            {
                using var process = Process.Start(start)!;
                using var reader = process.StandardError;
                Task<string> diagnostics = reader.ReadToEndAsync();
                await process.WaitForExitAsync(cancellation.Token);
                await VideoConverter.FinishDiagnosticsAsync(reader, diagnostics);
                Assert.Throws<ObjectDisposedException>(() => reader.Peek());
                return "finished";
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(childId))
                await Task.Delay(20, deadline.Token);
            // The wrapper has exited, while its child keeps the inherited pipe handles open.
            await Task.Delay(150, deadline.Token);
            Assert.False(conversion.IsCompleted);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => conversion.WaitAsync(TimeSpan.FromSeconds(3)));
                if (readDecoderOutput)
                    Assert.Equal(new byte[pixels.Length], pixels);
            }
            else if (finishDiagnostics)
            {
                Assert.Equal("finished", await conversion.WaitAsync(TimeSpan.FromSeconds(3)));
                using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(childId)));
                Assert.False(child.HasExited);
            }
            else
            {
                var error = await Assert.ThrowsAsync<FfmpegProcessException>(() =>
                    conversion.WaitAsync(TimeSpan.FromSeconds(readDecoderOutput ? 5 : 3)));
                Assert.Equal(7, error.ExitCode);
                Assert.Contains("wrapper diagnostics", error.Diagnostics);
                if (readDecoderOutput)
                    Assert.Equal(new byte[pixels.Length], pixels);
            }
        }
        finally
        {
            cancellation.Cancel();
            if (File.Exists(childId) && int.TryParse(await File.ReadAllTextAsync(childId), out int id))
            {
                try
                {
                    using var child = Process.GetProcessById(id);
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }
                catch (ArgumentException) { }
            }
            if (conversion is not null)
                await ((Task)conversion).WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            directory.Delete(recursive: true);
        }
    }
}
