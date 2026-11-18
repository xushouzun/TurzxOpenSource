using TURZX.WinUsbDisplay.Studio;
using Xunit;

namespace TURZX.WinUsbDisplay.Windows.Tests;

public class DeviceVideoCacheTests
{
    private static string CachePath(TemporaryDirectory directory) => directory.File(new string('a', 64) + ".h264");

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0, 0, 1 })]
    [InlineData(new byte[] { 1, 2, 3, 4, 5 })]
    public async Task IncompleteOrInvalidCacheIsNotReused(byte[] bytes)
    {
        using var directory = new TemporaryDirectory();
        string path = CachePath(directory);
        await File.WriteAllBytesAsync(path, bytes);

        using var entry = await DeviceVideoCache.OpenAsync(path, null, CancellationToken.None);

        Assert.Null(entry);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 1, 0x67, 10 })]
    [InlineData(new byte[] { 0, 0, 0, 1, 0x67, 20 })]
    public async Task PreparedVideoReplacesInvalidCacheAndStartsAtFirstByte(byte[] bytes)
    {
        using var directory = new TemporaryDirectory();
        string path = CachePath(directory);
        string prepared = directory.File("prepared.h264");
        await File.WriteAllBytesAsync(path, [9, 8, 7]);
        await File.WriteAllBytesAsync(prepared, bytes);

        using var entry = await DeviceVideoCache.OpenAsync(path, prepared, CancellationToken.None);

        Assert.NotNull(entry);
        Assert.False(File.Exists(prepared));
        Assert.Equal(0, entry.Stream.Position);
        var actual = new byte[bytes.Length];
        await entry.Stream.ReadExactlyAsync(actual);
        Assert.Equal(bytes, actual);
    }

    [Fact]
    public async Task InvalidPreparedVideoDoesNotReplaceExistingData()
    {
        using var directory = new TemporaryDirectory();
        string path = CachePath(directory);
        string prepared = directory.File("prepared.h264");
        byte[] original = [9, 8, 7, 6];
        await File.WriteAllBytesAsync(path, original);
        await File.WriteAllBytesAsync(prepared, [1, 2, 3, 4]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DeviceVideoCache.OpenAsync(path, prepared, CancellationToken.None));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.True(File.Exists(prepared));
    }

    [Fact]
    public async Task OpenPlaybackEntryIsProtectedFromDeletionAndDoesNotReplaceGoodCache()
    {
        using var directory = new TemporaryDirectory();
        string path = CachePath(directory);
        string prepared = directory.File("prepared.h264");
        byte[] original = [0, 0, 0, 1, 0x67, 42];
        await File.WriteAllBytesAsync(path, original);
        await File.WriteAllBytesAsync(prepared, [0, 0, 0, 1, 0x67, 99]);

        using (var entry = await DeviceVideoCache.OpenAsync(path, prepared, CancellationToken.None))
        {
            Assert.NotNull(entry);
            Assert.True(File.Exists(prepared));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Throws<IOException>(() => File.Delete(path));
            Assert.True(File.Exists(path));
        }

        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ConcurrentPublishersReuseOneCompleteVideo()
    {
        using var directory = new TemporaryDirectory();
        string path = CachePath(directory);
        string first = directory.File("first.h264");
        string second = directory.File("second.h264");
        await File.WriteAllBytesAsync(first, [0, 0, 0, 1, 0x67, 11]);
        await File.WriteAllBytesAsync(second, [0, 0, 0, 1, 0x67, 22]);

        var entries = await Task.WhenAll(
            DeviceVideoCache.OpenAsync(path, first, CancellationToken.None),
            DeviceVideoCache.OpenAsync(path, second, CancellationToken.None));
        try
        {
            Assert.All(entries, entry => Assert.NotNull(entry));
            var firstContent = new byte[6];
            var secondContent = new byte[6];
            await entries[0]!.Stream.ReadExactlyAsync(firstContent);
            await entries[1]!.Stream.ReadExactlyAsync(secondContent);
            Assert.Equal(firstContent, secondContent);
            Assert.Contains(firstContent[5], new byte[] { 11, 22 });
            Assert.NotEqual(File.Exists(first), File.Exists(second));
        }
        finally
        {
            foreach (var entry in entries)
                entry?.Dispose();
        }
    }

    [Fact]
    public async Task CancellationInterruptsWaitingForAnotherPublisher()
    {
        using var directory = new TemporaryDirectory();
        using var publisher = new FileStream(directory.File("cache.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DeviceVideoCache.OpenAsync(CachePath(directory), null, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
