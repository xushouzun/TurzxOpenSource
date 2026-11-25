using System.IO;
using TURZX.WinUsbDisplay.Media;

namespace TURZX.WinUsbDisplay.Studio;

/// <summary>管理按转换参数复用的设备视频文件。</summary>
internal static class DeviceVideoCache
{
    internal const string PartialVideoSuffix = VideoConverter.PartialOutputMarker + ".h264";

    /// <summary>视频缓存允许保留的最大总字节数。</summary>
    private const long MaximumBytes = 1024L * 1024 * 1024;

    /// <summary>表示一个已打开的缓存条目及其文件访问权。</summary>
    /// <param name="stream">已打开的缓存文件流。</param>
    /// <param name="trimOnClose">关闭条目时是否安排缓存清理。</param>
    internal sealed class Entry(FileStream stream, bool trimOnClose) : IDisposable
    {
        /// <summary>记录缓存条目是否已释放。</summary>
        private int disposed;
        /// <summary>播放任务用于读取缓存视频的文件流。</summary>
        internal FileStream Stream { get; } = stream;

        /// <summary>关闭缓存文件，并安排后续清理。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            Stream.Dispose();
            if (trimOnClose)
                _ = Task.Run(() => TrimAfterCloseAsync(Path.GetDirectoryName(Stream.Name)!));
        }
    }

    /// <summary>打开可复用的缓存文件，必要时先执行转换。</summary>
    internal static Task<Entry?> OpenAsync(string path, string? prepared, CancellationToken cancellation) =>
        Task.Run(async () =>
        {
            string directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            // 各 Studio 进程须串行发布缓存、打开播放句柄和清理缓存。
            using var cacheLock = await LockAsync(directory, cancellation).ConfigureAwait(false);
            // 拿到发布锁后重新检查目标文件，其他进程可能已在本次转码期间生成可用条目。
            FileStream? video = File.Exists(path) ? OpenComplete(path) : null;
            if (video is null && prepared is not null)
            {
                var encoded = OpenComplete(prepared) ?? throw new InvalidDataException("FFmpeg did not produce an Annex B H.264 stream.");
                encoded.Dispose();
                File.Move(prepared, path, overwrite: true);
                video = File.OpenRead(path);
            }
            // 先保护当前播放文件；暂时无法降到容量上限时，等句柄关闭后再次清理。
            bool trimOnClose = Trim(directory, video?.Name);
            return video is null ? null : new Entry(video, trimOnClose);
        }, cancellation);

    /// <summary>取得指定缓存键的文件锁以协调并发转换。</summary>
    private static async Task<FileStream> LockAsync(string directory, CancellationToken cancellation)
    {
        string path = Path.Combine(directory, "cache.lock");
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when (error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
            {
                await Task.Delay(25, cancellation).ConfigureAwait(false);
            }
        }
    }

    /// <summary>打开已完成的缓存条目供播放使用。</summary>
    private static FileStream? OpenComplete(string path)
    {
        FileStream? video = null;
        try
        {
            video = File.OpenRead(path);
            Span<byte> prefix = stackalloc byte[4];
            if (video.ReadAtLeast(prefix, prefix.Length, false) != prefix.Length
                || !(prefix.StartsWith(new byte[] { 0, 0, 1 }) || prefix.SequenceEqual(new byte[] { 0, 0, 0, 1 })))
                return null;
            video.Position = 0;
            FileStream result = video;
            video = null;
            return result;
        }
        catch (FileNotFoundException) { return null; }
        finally { video?.Dispose(); }
    }

    /// <summary>在缓存文件关闭后安排容量清理。</summary>
    private static async Task TrimAfterCloseAsync(string directory)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            using var cacheLock = await LockAsync(directory, timeout.Token).ConfigureAwait(false);
            Trim(directory, current: null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException) { }
    }

    // 因播放句柄仍打开或当前视频过大而无法清理至容量上限时，返回 true。
    /// <summary>从磁盘上淘汰可移除的缓存文件。</summary>
    private static bool Trim(string directory, string? current)
    {
        try
        {
            RemoveAbandonedConversions(directory);
            var entries = new DirectoryInfo(directory).GetFiles("*.h264")
                .Where(file => file.Name.Length == 69 && file.Name.AsSpan(0, 64).IndexOfAnyExcept("0123456789abcdef") < 0)
                .OrderBy(file => file.LastWriteTimeUtc).ToArray();
            long total = entries.Sum(file => file.Length);
            foreach (FileInfo entry in entries)
            {
                if (total <= MaximumBytes)
                    break;
                if (entry.FullName.Equals(current, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    long size = entry.Length;
                    // 其他播放任务持有以 FileShare.Read 打开的句柄时，Windows 会拒绝删除。
                    entry.Delete();
                    total -= size;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return total > MaximumBytes;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>删除已中断转换留下的临时文件。</summary>
    private static void RemoveAbandonedConversions(string directory)
    {
        DateTime cutoff = DateTime.UtcNow.AddDays(-1);
        foreach (FileInfo entry in new DirectoryInfo(directory).EnumerateFiles("*" + PartialVideoSuffix))
        {
            if (entry.Name.Length != 32 + PartialVideoSuffix.Length
                || !Guid.TryParseExact(entry.Name.AsSpan(0, 32), "N", out _)
                || entry.LastWriteTimeUtc >= cutoff)
                continue;

            try
            {
                // 缓存锁保护发布过程。若 FFmpeg 仍在写入，该文件无法以此方式打开。
                using (var candidate = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    if (File.GetLastWriteTimeUtc(candidate.SafeFileHandle) >= cutoff)
                        continue;
                }
                entry.Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
