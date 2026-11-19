using System.Buffers.Binary;
using System.ComponentModel;
using System.Text;
using Xunit;

namespace TURZX.WinUsbDisplay.Tests;

/// <summary>验证启动配置、设备路径和目录应答处理。</summary>
public class DeviceFileTests
{
    /// <summary>验证启动配置使用 Win207 要求的字段顺序。</summary>
    [Theory]
    [InlineData(66, 100, 66)]
    [InlineData(100, 80, 80)]
    [InlineData(66, 80, 52)]
    [InlineData(0, 80, 0)]
    public void StartupConfigurationUsesWin207WireFieldOrder(int brightness, int limit, int expected)
    {
        byte[] args = DisplaySession.EncodeStartupConfiguration((byte)brightness, (byte)limit, 2, 3, true, 1);
        Assert.Equal(new byte[] { (byte)expected, 2, 0, 3, 0, 1, 1 }, args);
    }

    /// <summary>验证路径长度编码和保留字节。</summary>
    [Fact]
    public void EncodesPathLengthAndReservedBytes()
    {
        const string path = "/usr/data/video/a.h264";
        byte[] args = DisplaySession.EncodeDevicePath(path);
        Assert.Equal(path.Length, BinaryPrimitives.ReadInt32BigEndian(args));
        Assert.Equal(new byte[4], args[4..8]);
        Assert.Equal(path, Encoding.ASCII.GetString(args[8..]));
    }

    /// <summary>验证无法按设备协议编码的路径会被拒绝。</summary>
    [Theory]
    [InlineData("relative.jpg")]
    [InlineData("/usr/data/../a.jpg")]
    [InlineData("/usr/data/图.jpg")]
    public void RejectsUnrepresentablePaths(string path) =>
        Assert.Throws<ArgumentException>(() => DisplaySession.EncodeDevicePath(path));

    /// <summary>验证填充应答和不存在的目录能正确解析。</summary>
    [Fact]
    public void ParsesPaddedListingsAndMissingDirectories()
    {
        Assert.Equal(new[] { "one.jpg", "图.jpg" },
            DisplaySession.ParseDeviceFileList("dir:/file:one.jp\0g/图.jpg/../one.jpg/\0"));
        Assert.Empty(DisplaySession.ParseDeviceFileList("nodir-createdone\0"));
        Assert.Empty(DisplaySession.ParseDeviceFileList("result:dir:file:\0result:dir:file:\0"));
        Assert.Empty(DisplaySession.ParseDeviceFileList(new string('\0', 512)));
        Assert.Equal(new[] { "nodir-createdone.png" }, DisplaySession.ParseDeviceFileList("file:nodir-createdone.png/"));
        Assert.Throws<DeviceResponseException>(() => DisplaySession.ParseDeviceFileList("unexpected"));
    }

    [Fact]
    public void DirectoryCreationFailurePreservesTheFirmwareReply()
    {
        const string status = "result:nodir-createfailed";
        string reply = status.PadRight(512, '\0') + status.PadRight(512, '\0');
        var error = Assert.Throws<DeviceResponseException>(() => DisplaySession.ParseDeviceFileList(reply));

        Assert.Equal(DeviceResponseFailure.DirectoryCreationFailed, error.Failure);
        Assert.Equal(Encoding.UTF8.GetBytes(reply), error.Response.ToArray());
        Assert.Equal(new[] { status + ".png" }, DisplaySession.ParseDeviceFileList($"file:{status}.png/"));
    }

    /// <summary>验证不完整目录会忽略末尾未结束的文件名。</summary>
    [Fact]
    public void PartialDirectoryExcludesAnUnterminatedFilename()
    {
        const string reply = "dir:/file:one.jpg/two.jpg";
        Assert.Equal(new[] { "one.jpg", "two.jpg" }, DisplaySession.ParseDeviceFileList(reply));
        Assert.Equal(new[] { "one.jpg" }, DisplaySession.ParseDeviceFileList(reply, isComplete: false));
        Assert.Empty(DisplaySession.ParseDeviceFileList("dir:/file:one.jpg", isComplete: false));
    }

    /// <summary>验证重复出现的目录元数据不会成为文件条目。</summary>
    [Theory]
    [InlineData("dir:/file:one.jpg/dir:/file:one.jpg/", true)]
    [InlineData("result:dir:/file:one.jpg/result:dir:/file:one.jpg/", true)]
    [InlineData("dir:/file:one.jpgdir:/file:one.jpg/", true)]
    [InlineData("dir:/file:one.jpg/dir:/", false)]
    public void RepeatedDirectoryMetadataIsNotAFile(string reply, bool complete)
    {
        Assert.Equal(new[] { "one.jpg" }, DisplaySession.ParseDeviceFileList(reply, complete));
    }

    /// <summary>验证取消后不会继续请求目录分段。</summary>
    [Fact]
    public void CancelledDirectoryReadDoesNotRequestAnotherReply()
    {
        using var cancellation = new CancellationTokenSource();
        int requests = 0;
        var error = Assert.Throws<OperationCanceledException>(() => DisplaySession.CollectDirectoryReplies("/", () =>
        {
            requests++;
            cancellation.Cancel();
            return "file:one.jpg/"u8.ToArray();
        }, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, requests);
    }

    /// <summary>验证目录读取中途超时可返回已收集的数据。</summary>
    [Fact]
    public void DirectoryTimeoutAfterAReplyStillReturnsPartialData()
    {
        byte[] payload = "dir:/file:one.jpg/"u8.ToArray();
        int requests = 0;
        var result = DisplaySession.CollectDirectoryReplies("/", () => requests++ == 0
            ? payload : throw new DeviceResponseException(DeviceResponseFailure.NoData, "No data."),
            CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Equal(payload, result.Response);
        Assert.Equal(2, requests);
    }

    [Fact]
    public void DirectoryReplyLimitDoesNotConfirmATruncatedFilename()
    {
        int requests = 0;
        var result = DisplaySession.CollectDirectoryReplies("/", () => ++requests switch
        {
            1 => "dir:/file:one.jpg/"u8.ToArray(),
            20 => "two.jp"u8.ToArray(),
            _ => new byte[512]
        }, CancellationToken.None);

        Assert.Equal(20, requests);
        Assert.False(result.IsComplete);
        Assert.Equal(new[] { "one.jpg" },
            DisplaySession.ParseDeviceFileList(Encoding.UTF8.GetString(result.Response), result.IsComplete));
    }

    [Fact]
    public void RepeatedDirectoryHeaderConfirmsThePreviousListing()
    {
        int requests = 0;
        var result = DisplaySession.CollectDirectoryReplies("/", () => ++requests switch
        {
            1 => "dir:/file:one.jpg"u8.ToArray(),
            2 => "dir:/file:one"u8.ToArray(),
            _ => throw new DeviceResponseException(DeviceResponseFailure.NoData, "No data.")
        }, CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(2, requests);
        Assert.Equal(new[] { "one.jpg" },
            DisplaySession.ParseDeviceFileList(Encoding.UTF8.GetString(result.Response), result.IsComplete));
    }

    [Fact]
    public void AnotherDirectoryReplyCannotConfirmOrAddFilesToTheFirstListing()
    {
        var replies = new Queue<byte[]>(
        [
            "dir:/current/file:one.jpg/truncated"u8.ToArray(),
            "dir:/other/file:unrelated.jpg/"u8.ToArray(),
            "dir:/current/file:one.jpg/two.jpg/"u8.ToArray()
        ]);
        var result = DisplaySession.CollectDirectoryReplies("/current", () => replies.TryDequeue(out var reply)
            ? reply
            : throw new DeviceResponseException(DeviceResponseFailure.NoData, "No data."), CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Equal(new[] { "one.jpg" },
            DisplaySession.ParseDeviceFileList(Encoding.UTF8.GetString(result.Response), result.IsComplete));
    }

    [Fact]
    public void StaleDirectoryRepeatsCannotDisplaceTheRequestedListing()
    {
        int requests = 0;
        var result = DisplaySession.CollectDirectoryReplies("/target/", () => Encoding.UTF8.GetBytes(++requests switch
        {
            1 or 2 => "dir:/other/file:old.jpg/",
            3 => "dir:/tar",
            4 => "get/file:new.jpg/",
            5 => "dir:/target/file:new.jpg/",
            _ => throw new InvalidOperationException("The matching repeat completed the listing.")
        }), CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(5, requests);
        Assert.Equal(new[] { "new.jpg" }, DisplaySession.ParseDeviceFileList(Encoding.UTF8.GetString(result.Response)));
    }

    [Theory]
    [InlineData("dir:/other/file:old.jpg/")]
    [InlineData("dir:/target-other/file:old.jpg/")]
    [InlineData("dir:/file:old.jpg/")]
    [InlineData("file:old.jpg/")]
    public void UnscopedOrForeignDirectoryFilesAreRejected(string reply)
    {
        var error = Assert.Throws<DeviceResponseException>(() =>
            DisplaySession.CollectDirectoryReplies("/target/", () => Encoding.UTF8.GetBytes(reply), CancellationToken.None));
        Assert.Equal(DeviceResponseFailure.UnexpectedReply, error.Failure);
    }

    [Fact]
    public void UnrecognizedReplyCannotMasqueradeAsAnEmptyDirectory()
    {
        Assert.Throws<DeviceResponseException>(() => DisplaySession.ParseDeviceFileList("error:nodir-createdone"));
    }

    [Fact]
    public void StopPlaybackTriesRemainingLayersAndReportsFailures()
    {
        var first = new DeviceResponseException(DeviceResponseFailure.NoData, "No response.");
        var second = new IOException("Short write.");
        bool stoppedLast = false;
        var error = Assert.Throws<AggregateException>(() => DisplaySession.StopPlaybackLayers(
            () => throw first, () => throw second, () => stoppedLast = true));

        Assert.True(stoppedLast);
        Assert.Equal(new Exception[] { first, second }, error.InnerExceptions);
    }

    [Fact]
    public void StopPlaybackDoesNotSendMoreCommandsAfterDisconnect()
    {
        var disconnected = new Win32Exception(1167);
        var error = Assert.Throws<Win32Exception>(() => DisplaySession.StopPlaybackLayers(
            () => throw disconnected, () => throw new InvalidOperationException("Unexpected command.")));

        Assert.Same(disconnected, error);
    }

    [Fact]
    public void AllPaddingDoesNotConfirmAnEmptyDirectory()
    {
        byte[] padding = new byte[512];
        var result = DisplaySession.CollectDirectoryReplies("/", () => padding, CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Equal(20 * padding.Length, result.Response.Length);
        Assert.Empty(DisplaySession.ParseDeviceFileList(Encoding.UTF8.GetString(result.Response), result.IsComplete));
    }

    [Fact]
    public void ExplicitMissingDirectoryCanReturnACompleteEmptyListing()
    {
        int requests = 0;
        var result = DisplaySession.CollectDirectoryReplies("/", () =>
            requests++ == 0 ? "nodir-createdone"u8.ToArray() : new byte[512], CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(1, requests);
        Assert.Empty(DisplaySession.ParseDeviceFileList(Encoding.UTF8.GetString(result.Response), result.IsComplete));
    }

    [Theory]
    [InlineData("nodir-createdone")]
    [InlineData("result:nodir-createdone")]
    [InlineData("result:dir:file:")]
    public void ExplicitEmptyDirectoryReplyStopsAfterTheFirstRequest(string status)
    {
        byte[] reply = Encoding.UTF8.GetBytes(status.PadRight(512, '\0'));
        int requests = 0;
        var result = DisplaySession.CollectDirectoryReplies("/", () =>
            requests++ < 2 ? reply : new byte[512], CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(1, requests);
        Assert.Empty(DisplaySession.ParseDeviceFileList(Encoding.UTF8.GetString(result.Response), result.IsComplete));
    }

    [Theory]
    [InlineData(101, "result:dir:file:")]
    [InlineData(102, "result:nodir-createdone")]
    [InlineData(42, "result:dir:file:")]
    [InlineData(52, "result:dir:file:")]
    [InlineData(123, "result:dir:file:")]
    public void OldAcknowledgementDoesNotMaskAnEmptyDirectory(int command, string status)
    {
        int requests = 0;
        byte[] stale = new byte[512];
        Convert.FromHexString("65C82E6786050000").CopyTo(stale, 0);
        stale[0] = (byte)command;
        var result = DisplaySession.CollectDirectoryReplies("/usr/data/img/", () => ++requests switch
        {
            1 => stale,
            2 => Encoding.UTF8.GetBytes(status.PadRight(512, '\0')),
            _ => throw new InvalidOperationException("The empty response should terminate the read.")
        }, CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Empty(DisplaySession.ParseDeviceFileList(Encoding.UTF8.GetString(result.Response)));
        Assert.Equal(2, requests);
    }

    [Fact]
    public void OldFileSizeReplyWithPayloadDoesNotMaskAnEmptyDirectory()
    {
        byte[] staleSize = new byte[512];
        Convert.FromHexString("62C8E4059B0500001C400000").CopyTo(staleSize, 0);
        int requests = 0;
        var result = DisplaySession.CollectDirectoryReplies("/usr/data/img/", () => ++requests switch
        {
            1 => staleSize,
            2 => "result:dir:file:"u8.ToArray(),
            _ => throw new InvalidOperationException("The empty response should terminate the read.")
        }, CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Empty(DisplaySession.ParseDeviceFileList(Encoding.UTF8.GetString(result.Response)));
        Assert.Equal(2, requests);
    }

    [Fact]
    public void DirectoryContinuesAcrossAFragmentResemblingAFileSizeReply()
    {
        byte[] textFragment = new byte[512];
        "bȣ/"u8.CopyTo(textFragment);
        int requests = 0;
        var result = DisplaySession.CollectDirectoryReplies("/usr/data/img/", () => ++requests switch
        {
            1 or 3 => "dir:/usr/data/img/file:"u8.ToArray(),
            2 => textFragment,
            _ => throw new InvalidOperationException("The repeated header should terminate the read.")
        }, CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(new[] { "bȣ" }, DisplaySession.ParseDeviceFileList(Encoding.UTF8.GetString(result.Response)));
    }

    /// <summary>验证取消后不再重试查询或复位管道。</summary>
    [Fact]
    public void CancelledVerifiedQueryDoesNotRetryOrResetThePipe()
    {
        using var cancellation = new CancellationTokenSource();
        int requests = 0;
        int resets = 0;
        var error = Assert.Throws<OperationCanceledException>(() => DisplaySession.RetryVerifiedQuery(new Lock(), 10, 2, () =>
        {
            requests++;
            cancellation.Cancel();
            throw new Win32Exception(121);
        }, () => resets++, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, requests);
        Assert.Equal(0, resets);
    }

    [Fact]
    public void VerifiedQueryResetsOutputAfterAWriteTimeoutOnTheSecondAttempt()
    {
        var gate = new Lock();
        byte[] reply = [10, 1];
        int requests = 0;
        bool reset = false;
        byte[] received = DisplaySession.RetryVerifiedQuery(gate, 10, 2, () =>
        {
            if (++requests == 1)
                throw new DeviceResponseException(DeviceResponseFailure.NoData, "Read timed out.");
            if (requests == 2)
                throw new Win32Exception(121);
            Assert.True(reset);
            return reply;
        }, () =>
        {
            Assert.True(gate.IsHeldByCurrentThread);
            Assert.Equal(2, requests);
            reset = true;
        }, CancellationToken.None);

        Assert.Same(reply, received);
        Assert.Equal(3, requests);
    }

    /// <summary>验证写入恢复期间仍独占请求管道。</summary>
    [Fact]
    public void VerifiedQueryKeepsThePipeLockedDuringWriteRecovery()
    {
        var gate = new Lock();
        byte[] reply = [10, 1];
        int requests = 0;
        int resets = 0;
        byte[] received = DisplaySession.RetryVerifiedQuery(gate, 10, 2, () =>
        {
            Assert.True(gate.IsHeldByCurrentThread);
            return requests++ switch
            {
                0 => throw new Win32Exception(121),
                1 => [50, 1],
                _ => reply
            };
        }, () =>
        {
            Assert.True(gate.IsHeldByCurrentThread);
            resets++;
        }, CancellationToken.None);

        Assert.Same(reply, received);
        Assert.Equal(3, requests);
        Assert.Equal(1, resets);
    }

    [Fact]
    public void VerifiedQueryDrainsStaleStopRepliesWithoutResendingTheQuery()
    {
        var gate = new Lock();
        byte[] expected = new byte[512];
        expected[0] = 17;
        var replies = new Queue<byte[]>([new byte[] { 111 }, new byte[] { 111 }, expected]);
        int requests = 0;
        byte[] received = DisplaySession.RetryVerifiedQuery(gate, 17, 12, () =>
        {
            requests++;
            return [111];
        }, () => throw new InvalidOperationException("Unexpected reset."), CancellationToken.None,
            readNextResponse: () =>
            {
                Assert.True(gate.IsHeldByCurrentThread);
                return replies.Dequeue();
            });

        Assert.Same(expected, received);
        Assert.Equal(1, requests);
        Assert.Empty(replies);
    }

    [Fact]
    public void VerifiedQueryBoundsStaleRepliesAndPreservesTheLastResponse()
    {
        byte[] stale = [111];
        int requests = 0;
        int reads = 0;
        var error = Assert.Throws<DeviceResponseException>(() => DisplaySession.RetryVerifiedQuery(
            new Lock(), 17, 12, () => { requests++; return stale; }, () => { }, CancellationToken.None,
            readNextResponse: () => { reads++; return stale; }));

        Assert.Equal(DeviceResponseFailure.UnexpectedReply, error.Failure);
        Assert.Equal(stale, error.Response.ToArray());
        Assert.Equal(3, requests);
        Assert.Equal(60, reads);
    }

    [Fact]
    public void VerifiedQueryStopsDrainingStaleRepliesWhenCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        int reads = 0;
        var error = Assert.Throws<OperationCanceledException>(() => DisplaySession.RetryVerifiedQuery(
            new Lock(), 17, 12, () => [111], () => { }, cancellation.Token,
            readNextResponse: () =>
            {
                reads++;
                cancellation.Cancel();
                return [111];
            }));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, reads);
    }
}
