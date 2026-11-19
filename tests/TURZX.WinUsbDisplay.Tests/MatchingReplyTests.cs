using Xunit;

namespace TURZX.WinUsbDisplay.Tests;

public class MatchingReplyTests
{
    [Theory]
    [InlineData(42)]
    [InlineData(52)]
    [InlineData(101)]
    [InlineData(123)]
    [InlineData(125)]
    public void DeviceWritesRequireAMatchingCompleteReplyWithoutResending(int id)
    {
        byte command = (byte)id;
        int sends = 0;
        var replies = new Queue<byte[]>([new byte[] { command }, new byte[] { command, 200 }]);
        byte[] result = DisplaySession.ReadMatchingReply(command, 2,
            () => { sends++; return [14, 200]; }, () => replies.Dequeue(), CancellationToken.None);
        Assert.Equal(new byte[] { command, 200 }, result);
        Assert.Equal(1, sends);
        Assert.Empty(replies);
    }

    [Fact]
    public void StaleRepliesAreDrainedWithoutResendingTheControlCommand()
    {
        int sends = 0;
        var replies = new Queue<byte[]>([new byte[] { 111, 200 }, new byte[] { 14, 200 }]);
        byte[] result = DisplaySession.ReadMatchingReply(14, 2,
            () => { sends++; return [123, 200]; }, () => replies.Dequeue(), CancellationToken.None);
        Assert.Equal(new byte[] { 14, 200 }, result);
        Assert.Equal(1, sends);
        Assert.Empty(replies);
    }

    [Fact]
    public void MismatchDrainIsBoundedAndNeverRepeatsTheCommand()
    {
        int sends = 0, reads = 0;
        Assert.Throws<DeviceResponseException>(() => DisplaySession.ReadMatchingReply(12, 2,
            () => { sends++; return [111, 200]; }, () => { reads++; return [111, 200]; }, CancellationToken.None));
        Assert.Equal(1, sends);
        Assert.Equal(20, reads);
    }

    [Fact]
    public void ReadFailureDoesNotResendAMutatingRequest()
    {
        int sends = 0;
        Assert.Throws<IOException>(() => DisplaySession.ReadMatchingReply(14, 2,
            () => { sends++; return [123, 200]; }, () => throw new IOException(), CancellationToken.None));
        Assert.Equal(1, sends);
    }

    [Fact]
    public void CancellationBeforeSendHasNoDeviceSideEffect()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => DisplaySession.ReadMatchingReply(12, 2,
            () => throw new InvalidOperationException("Must not send"), () => [], cancellation.Token));
    }
}
