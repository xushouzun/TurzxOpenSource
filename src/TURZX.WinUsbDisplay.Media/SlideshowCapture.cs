using System.Diagnostics;

namespace TURZX.WinUsbDisplay.Media;

/// <summary>按换图间隔串行读取图片，并在同一张图显示期间复用已编码的帧。</summary>
/// <param name="paths">按播放顺序排列的图片路径。</param>
/// <param name="interval">每张图片从开始显示到换图的最短间隔。</param>
/// <param name="encode">将图片文件内容转换为待发送帧的回调。</param>
/// <param name="cacheLimitBytes">已编码帧缓存的总字节上限。非正值时禁用缓存。</param>
public sealed class SlideshowCapture(
    IReadOnlyList<string> paths, TimeSpan interval, Func<byte[], byte[]> encode, int cacheLimitBytes) : IAsyncDisposable
{
    /// <summary>创建按顺序轮播的图片来源，仅保留当前帧和预取帧。</summary>
    public SlideshowCapture(IReadOnlyList<string> paths, TimeSpan interval, Func<byte[], byte[]> encode)
        : this(paths, interval, encode, cacheLimitBytes: 0)
    {
    }

    internal SlideshowCapture(IReadOnlyList<string> paths, TimeSpan interval, Func<byte[], byte[]> encode,
        Func<string, CancellationToken, Task<byte[]>> readImage)
        : this(paths, interval, encode)
    {
        this.readImage = readImage;
    }

    private readonly Func<string, CancellationToken, Task<byte[]>> readImage = File.ReadAllBytesAsync;

    /// <summary>构造时复制的轮播路径列表。</summary>
    private readonly string[] slides = paths.ToArray();
    /// <summary>控制当前读取和后台预取任务的生命周期。</summary>
    private readonly CancellationTokenSource lifetime = new();
    /// <summary>串行化释放标记与取帧后的状态提交。</summary>
    private readonly Lock stateGate = new();
    /// <summary>按图片索引保留已编码帧的可选缓存。</summary>
    private readonly Dictionary<int, byte[]>? cache = cacheLimitBytes > 0 ? new() : null;
    /// <summary>仍可用于缓存的字节数。</summary>
    private int remainingCacheBytes = cacheLimitBytes;
    /// <summary>下一张待读取图片在列表内的位置。</summary>
    private int index;
    /// <summary>当前图片对应的换图节拍。</summary>
    private long lastChange;
    /// <summary>目前可重复返回的已编码帧。</summary>
    private byte[]? currentFrame;
    /// <summary>下一张图片的后台读取和编码任务。</summary>
    private Task<byte[]>? nextFrame;
    /// <summary>记录轮播捕捉是否已释放。</summary>
    private bool disposed;

    /// <summary>读取当前帧。没有图片时返回空。调用方须串行读取。</summary>
    public ValueTask<byte[]?> ReadFrameAsync(CancellationToken cancellation) =>
        ReadFrameAsync(cancellation, waitForChange: false);

    /// <summary>等待下一张图片，播放同一张期间不返回重复帧。</summary>
    public ValueTask<byte[]?> ReadChangedFrameAsync(CancellationToken cancellation) =>
        ReadFrameAsync(cancellation, waitForChange: true);

    /// <summary>根据取帧模式返回当前帧或等待换图。</summary>
    private async ValueTask<byte[]?> ReadFrameAsync(CancellationToken cancellation, bool waitForChange)
    {
        CancellationToken lifetimeToken;
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lifetimeToken = lifetime.Token;
        }
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetimeToken);
        cancellation = reading.Token;
        cancellation.ThrowIfCancellationRequested();
        if (slides.Length == 0)
            return null;
        if (currentFrame is not null)
        {
            // 单张图片没有下一帧，读取模式只决定是立即复用还是等待取消。
            if (slides.Length == 1)
            {
                if (waitForChange)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellation).ConfigureAwait(false);
                return currentFrame;
            }

            TimeSpan remaining = interval - Stopwatch.GetElapsedTime(lastChange);
            if (!waitForChange && (remaining > TimeSpan.Zero || nextFrame is { IsCompleted: false }))
                return currentFrame;
            if (waitForChange)
            {
                while (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : remaining,
                        cancellation).ConfigureAwait(false);
                    remaining = interval - Stopwatch.GetElapsedTime(lastChange);
                }
            }
        }

        byte[] frame;
        try
        {
            frame = nextFrame is { } prepared
                ? await prepared.WaitAsync(cancellation).ConfigureAwait(false)
                : await LoadFrameAsync(index, cancellation).ConfigureAwait(false);
        }
        catch when (!cancellation.IsCancellationRequested && nextFrame is { IsFaulted: true })
        {
            // 预取失败后保留当前索引，让下次取帧重新读取该文件。
            nextFrame = null;
            throw;
        }
        lock (stateGate)
        {
            cancellation.ThrowIfCancellationRequested();
            if (disposed)
                throw new OperationCanceledException(cancellation);
            currentFrame = frame;
            long now = Stopwatch.GetTimestamp();
            TimeSpan elapsed = Stopwatch.GetElapsedTime(lastChange, now);
            // 保留换图节拍，免得每次计时器唤醒的误差累积。落后一整个间隔时重新起算。
            if (lastChange != 0 && interval > TimeSpan.Zero && elapsed >= interval && elapsed - interval < interval)
                lastChange += (long)(interval.TotalSeconds * Stopwatch.Frequency);
            else
                lastChange = now;
            index = (index + 1) % slides.Length;
            if (slides.Length > 1)
            {
                // 当前帧显示期间预读并编码下一张，缩短切换时的等待。
                int nextIndex = index;
                nextFrame = Task.Run(() => LoadFrameAsync(nextIndex, lifetimeToken), lifetimeToken);
            }
            return currentFrame;
        }
    }

    /// <summary>从缓存取得帧，或读取并编码指定图片。</summary>
    private async Task<byte[]> LoadFrameAsync(int slide, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (cache is not null && cache.TryGetValue(slide, out var cached))
            return cached;

        // 打开文件可能同步阻塞或忽略取消。只等待读取，在取消后不再调用编码回调。
        Task<byte[]> read = Task.Run(() => readImage(slides[slide], cancellation), cancellation);
        byte[] image;
        try
        {
            image = await read.WaitAsync(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _ = ObserveReadAsync(read);
            throw;
        }
        cancellation.ThrowIfCancellationRequested();
        byte[] frame = encode(image);
        cancellation.ThrowIfCancellationRequested();
        lock (stateGate)
        {
            cancellation.ThrowIfCancellationRequested();
            if (disposed)
                throw new OperationCanceledException(cancellation);
            if (cache is not null && frame.Length <= remainingCacheBytes)
            {
                cache.Add(slide, frame);
                remainingCacheBytes -= frame.Length;
            }
        }
        return frame;
    }

    private static async Task ObserveReadAsync(Task read) =>
        await read.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    /// <summary>取消当前读取及预取，并等待预取任务结束后释放资源。</summary>
    public async ValueTask DisposeAsync()
    {
        lock (stateGate)
        {
            if (disposed)
                return;
            disposed = true;
        }
        lifetime.Cancel();
        if (nextFrame is { } pending)
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        lifetime.Dispose();
    }
}
