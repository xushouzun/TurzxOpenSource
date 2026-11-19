using Xunit;

namespace TURZX.WinUsbDisplay.Tests;

public class DeviceFilePlaybackReplyTests
{
    [Theory]
    [InlineData((byte)110, "6EC8000000000000C8")]
    [InlineData((byte)113, "71C8")]
    public void OriginalSuccessRepliesDoNotQueryAgain(byte command, string reply) =>
        Assert.True(DisplaySession.IsDeviceFilePlaybackAccepted(command, Convert.FromHexString(reply),
            () => throw new InvalidOperationException("A confirmed response needs no status query.")));

    [Theory]
    [InlineData((byte)110, "6FC8000000000000C8")]
    [InlineData((byte)113, "6EC8")]
    public void UnrelatedRepliesCannotConfirmPlayback(byte command, string reply) =>
        Assert.False(DisplaySession.IsDeviceFilePlaybackAccepted(command, Convert.FromHexString(reply),
            () => throw new InvalidOperationException("An unrelated response needs no status query.")));

    [Theory]
    [InlineData("700000000000000001", true)]
    [InlineData("700000000000000000", false)]
    [InlineData("6F0000000000000001", false)]
    public void HeaderAcknowledgmentRequiresMatchingPlayingState(string status, bool expected)
    {
        int queries = 0;
        bool result = DisplaySession.IsDeviceFilePlaybackAccepted(110, [110, 200, 0, 0, 0, 0, 0, 0, 0], () =>
        {
            queries++;
            return Convert.FromHexString(status);
        });
        Assert.Equal(expected, result);
        Assert.Equal(1, queries);
    }
}
