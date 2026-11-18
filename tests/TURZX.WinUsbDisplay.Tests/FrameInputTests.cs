using System.Buffers.Binary;
using TURZX.WinUsbDisplay.Media;
using Xunit;

namespace TURZX.WinUsbDisplay.Tests;

/// <summary>验证带长度前缀的 PNG 输入流。</summary>
public class FrameInputTests
{
    private static byte[] RgbaPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==");

    /// <summary>作为测试输入的示例帧字节。</summary>
    private static MemoryStream Frame(byte[] png)
    {
        var stream = new MemoryStream();
        WriteFrame(stream, png);
        stream.Position = 0;
        return stream;
    }

    /// <summary>将小端长度及帧字节写入测试流。</summary>
    private static void WriteFrame(MemoryStream stream, byte[] png)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)png.Length);
        stream.Write(length);
        stream.Write(png);
    }

    /// <summary>验证读取有效的长度前缀和 PNG 帧。</summary>
    [Fact]
    public async Task ReadsLengthPrefixedPng()
    {
        byte[] png = [0x89, 0x50, 0x4e, 0x47, 1, 2, 3];
        using var stream = Frame(png);
        byte[]? frame = await FrameInput.ReadPngAsync(stream);
        Assert.Equal(png, frame);
    }

    [Fact]
    public async Task DisplayFrameLimitAppliesBeforeReadingPayload()
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, DisplaySession.MaxEncodedFrameBytes + 1);
        stream.Write(length);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => FrameInput.ReadDisplayPngAsync(stream).AsTask());
    }

    [Fact]
    public async Task DisplayFramesRequireEightBitRgbaPng()
    {
        byte[] png = RgbaPng();
        using var accepted = Frame(png);
        Assert.Equal(png, await FrameInput.ReadDisplayPngAsync(accepted));
        Assert.Null(await FrameInput.ReadDisplayPngAsync(accepted));

        png[25] = 2; // RGB，不带 Alpha。
        using var rejected = Frame(png);
        await Assert.ThrowsAsync<InvalidDataException>(() => FrameInput.ReadDisplayPngAsync(rejected).AsTask());
    }

    [Theory]
    [InlineData(33)]
    [InlineData(46)]
    [InlineData(58)]
    [InlineData(69)]
    public async Task DisplayFrameRejectsTruncatedPng(int bytes)
    {
        using var stream = Frame(RgbaPng()[..bytes]);
        await Assert.ThrowsAsync<InvalidDataException>(() => FrameInput.ReadDisplayPngAsync(stream).AsTask());
    }

    [Fact]
    public async Task PngEndChunkCannotReplaceImageData()
    {
        byte[] png = RgbaPng();
        using var stream = Frame([.. png[..33], .. png[58..]]);
        await Assert.ThrowsAsync<InvalidDataException>(() => FrameInput.ReadDisplayPngAsync(stream).AsTask());
    }

    [Fact]
    public async Task CancellingDisplayFrameDoesNotWaitForABlockedReadAsyncCall()
    {
        using var stream = new BlockingInput();
        using var cancellation = new CancellationTokenSource();
        TimeSpan timeout = TimeSpan.FromSeconds(5);
        Task<byte[]?> read = Task.Run(() => FrameInput.ReadDisplayPngAsync(stream, cancellation.Token).AsTask());
        try
        {
            await stream.Started.Task.WaitAsync(timeout);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(timeout));
            Assert.False(stream.Release.Task.IsCompleted);
        }
        finally
        {
            stream.Release.TrySetResult();
        }
    }

    private sealed class BlockingInput : MemoryStream
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            Release.Task.GetAwaiter().GetResult();
            return ValueTask.FromResult(0);
        }
    }

    /// <summary>验证在帧边界遇到流末尾时返回空。</summary>
    [Fact]
    public async Task EofReturnsNull()
    {
        using var stream = new MemoryStream();
        Assert.Null(await FrameInput.ReadPngAsync(stream));
    }

    /// <summary>验证拒绝零长度帧。</summary>
    [Fact]
    public async Task ZeroLengthIsRejected()
    {
        using var stream = Frame([]);
        await Assert.ThrowsAsync<InvalidDataException>(() => FrameInput.ReadPngAsync(stream).AsTask());
    }

    /// <summary>验证拒绝超过上限的帧长度。</summary>
    [Fact]
    public async Task OversizeLengthIsRejected()
    {
        var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, 17 * 1024 * 1024);
        stream.Write(length);
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => FrameInput.ReadPngAsync(stream).AsTask());
    }

    /// <summary>验证顺序读取多帧后正确报告流结束。</summary>
    [Fact]
    public async Task ReadsConsecutiveFramesThenEof()
    {
        byte[] a = [1, 2, 3];
        byte[] b = [4, 5];
        var stream = new MemoryStream();
        WriteFrame(stream, a);
        WriteFrame(stream, b);
        stream.Position = 0;
        Assert.Equal(a, await FrameInput.ReadPngAsync(stream));
        Assert.Equal(b, await FrameInput.ReadPngAsync(stream));
        Assert.Null(await FrameInput.ReadPngAsync(stream));
    }
}
