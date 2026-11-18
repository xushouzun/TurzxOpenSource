using TURZX.WinUsbDisplay.Studio;
using Xunit;

namespace TURZX.WinUsbDisplay.Windows.Tests;

public class DisplayRotationTests
{
    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    public void Panel0123KeepsFirmwareNativeWhileRotatingPixels(byte rotation)
    {
        var model = new DisplayModel("12.3", 0x0123, 720, 1920);
        Assert.Equal((byte)0, MainWindow.FirmwareRotation(model, rotation));
        Assert.Equal(rotation, MainWindow.MediaFrameRotation(model, rotation));
    }

    [Theory]
    [InlineData((ushort)0x0046, (byte)1, (byte)0, (byte)3)]
    [InlineData((ushort)0x0055, (byte)3, (byte)0, (byte)1)]
    [InlineData((ushort)0x0070, (byte)2, (byte)2, (byte)2)]
    [InlineData((ushort)0x0021, (byte)1, (byte)1, (byte)1)]
    public void OtherPanelsKeepTheirRotationMapping(ushort product, byte rotation, byte firmware, byte pixels)
    {
        var model = new DisplayModel("test", product, 720, 1280);
        Assert.Equal(firmware, MainWindow.FirmwareRotation(model, rotation));
        Assert.Equal(pixels, MainWindow.MediaFrameRotation(model, rotation));
    }
}
