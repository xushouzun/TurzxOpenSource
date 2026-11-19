using Xunit;

namespace TURZX.WinUsbDisplay.Tests;

public class DesktopModeTests
{
    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(513)]
    public void ModeSwitchIsOneReportWithOriginalPayloadAndZeroPadding(int length)
    {
        byte[] report = DesktopModeDevice.BuildMonitorReport(length);
        Assert.Equal(length, report.Length);
        Assert.Equal(Convert.FromHexString("003566333735396466"), report[..9]);
        Assert.All(report.Skip(9), value => Assert.Equal((byte)0, value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(4097)]
    public void InvalidDescriptorLengthsCannotBeSent(int length) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopModeDevice.BuildMonitorReport(length));
}
