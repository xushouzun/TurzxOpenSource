using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace TURZX.WinUsbDisplay.Windows.Tests;

public class DisplayFrameProcessorTests
{
    [Fact]
    public async Task BlankOverlayIsNativeSizeRgbaAndFullyTransparent() => await OnStaAsync(() =>
    {
        var model = new DisplayModel("test", 0, 8, 6);
        byte[] png = DisplayFrameProcessor.CreateBlankOverlay(model);
        Assert.Equal((byte)8, png[24]);
        Assert.Equal((byte)6, png[25]);
        var frame = DisplayFrameProcessor.DecodeFrame(png);
        Assert.Equal(8, frame.PixelWidth);
        Assert.Equal(6, frame.PixelHeight);
        var rgba = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[8 * 6 * 4];
        rgba.CopyPixels(pixels, 8 * 4, 0);
        Assert.All(pixels, value => Assert.Equal((byte)0, value));
    });

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    public async Task RotatedLiveFramesRemainAtNativeDeviceDimensions(byte rotation) => await OnStaAsync(() =>
    {
        var model = new DisplayModel("test", 0, 8, 6);
        var canvas = model.ForRotation(rotation);
        var pixels = new byte[canvas.Width * canvas.Height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 2] = 255;
            pixels[i + 3] = 255;
        }
        var source = BitmapSource.Create(canvas.Width, canvas.Height, 96, 96,
            PixelFormats.Bgra32, null, pixels, canvas.Width * 4);

        byte[] png = DisplayFrameProcessor.PrepareLiveBitmapFrame(source, MediaLayout.Default, null,
            MediaLayout.Default, model, preserveTransparency: true, rotation, crop: null);

        var output = DisplayFrameProcessor.DecodeFrame(png);
        Assert.Equal(model.Width, output.PixelWidth);
        Assert.Equal(model.Height, output.PixelHeight);
        Assert.InRange(png.Length, 1, DisplaySession.MaxEncodedFrameBytes);
        var rgba = new FormatConvertedBitmap(output, PixelFormats.Bgra32, null, 0);
        var actual = new byte[model.Width * model.Height * 4];
        rgba.CopyPixels(actual, model.Width * 4, 0);
        for (int i = 0; i < actual.Length; i += 4)
        {
            Assert.Equal((byte)255, actual[i + 2]);
            Assert.Equal((byte)255, actual[i + 3]);
        }
    });

    [Fact]
    public async Task SmallCropAtBottomRightIncludesLastPixel() => await OnStaAsync(() =>
    {
        byte[] pixels = Enumerable.Range(0, 4 * 2 * 4).Select(value => (byte)value).ToArray();
        var source = BitmapSource.Create(4, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 4 * 4);

        var cropped = DisplayFrameProcessor.CropCapture(source, new Rect(.99, .99, .01, .01));

        Assert.Equal(1, cropped.PixelWidth);
        Assert.Equal(1, cropped.PixelHeight);
        byte[] actual = new byte[4];
        cropped.CopyPixels(actual, 4, 0);
        Assert.Equal(pixels[^4..], actual);
    });

    [Fact]
    public void PremultipliedOverlayPreservesTransparentPixelsAndBlendsIntoReusableBackground()
    {
        byte[] foreground = [0, 0, 0, 0, 10, 20, 30, 255, 30, 20, 10, 128];
        byte[] original = foreground.ToArray();
        byte[] background = [1, 2, 3, 255, 100, 110, 120, 255, 100, 80, 60, 255];

        byte[] result = DisplayFrameProcessor.BlendVideoOverlay(foreground, background);

        Assert.Same(background, result);
        Assert.Equal(new byte[] { 1, 2, 3, 255, 10, 20, 30, 255, 80, 60, 40, 255 }, result);
        Assert.Equal(original, foreground);
    }

    private static async Task OnStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
