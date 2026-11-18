using System.Buffers.Binary;

namespace TURZX.WinUsbDisplay.Media;

/// <summary>从流中读取带长度前缀的 PNG 帧。</summary>
public static class FrameInput
{
    /// <summary>读取四字节小端长度，再读取对应长度的帧数据。</summary>
    /// <returns>帧数据。在帧边界读到流末尾时返回 <see langword="null"/>。</returns>
    /// <exception cref="EndOfStreamException">长度前缀或帧数据不完整。</exception>
    /// <exception cref="InvalidDataException">长度为零或超过 16 MiB。</exception>
    public static ValueTask<byte[]?> ReadPngAsync(Stream input, CancellationToken cancellation = default) =>
        ReadFrameAsync(input, 16 * 1024 * 1024, cancellation);

    /// <summary>读取带长度前缀的设备送屏帧，要求 8 位 RGBA PNG 且大小不超过设备上限。</summary>
    /// <returns>送屏帧。在帧边界遇到流末尾时返回 <see langword="null"/>。</returns>
    /// <remarks>在底层读取未结束时取消后，请换用新流，或等待旧流的读取结束后再使用。</remarks>
    /// <exception cref="InvalidDataException">帧大小或 PNG 格式不符合送屏要求。</exception>
    public static async ValueTask<byte[]?> ReadDisplayPngAsync(Stream input, CancellationToken cancellation = default)
    {
        // 某些标准输入和自定义流会在 ReadAsync 返回前同步阻塞。
        Task<byte[]?> read = Task.Run(() => ReadFrameAsync(input, DisplaySession.MaxEncodedFrameBytes, cancellation).AsTask(),
            cancellation);
        byte[]? frame;
        try
        {
            frame = await read.WaitAsync(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _ = ObserveReadAsync(read);
            throw;
        }
        if (frame is not null)
            DisplaySession.ValidateOverlay(frame);
        return frame;

        static async Task ObserveReadAsync(Task read) => await read.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static async ValueTask<byte[]?> ReadFrameAsync(Stream input, int maximumBytes, CancellationToken cancellation)
    {
        var length = new byte[4];
        int first = await input.ReadAsync(length.AsMemory(0, 1), cancellation).ConfigureAwait(false);
        if (first == 0)
            return null;
        await input.ReadExactlyAsync(length.AsMemory(1), cancellation).ConfigureAwait(false);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(length);
        if (size == 0 || size > maximumBytes)
            throw new InvalidDataException($"Frame length must be between 1 and {maximumBytes} bytes.");
        var frame = new byte[size];
        await input.ReadExactlyAsync(frame, cancellation).ConfigureAwait(false);
        return frame;
    }
}
