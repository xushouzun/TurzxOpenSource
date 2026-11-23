using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace TURZX.WinUsbDisplay.Windows.Tests;

public class DisplayFrameProcessorTests
{
    [Fact]
    public void SixPointEightHtmlUsesBufferedVideoAtHigherScale()
    {
        var sixEight = new DisplayModel("6.8", 0x0068, 1080, 2320);
        Assert.True(TURZX.WinUsbDisplay.Studio.MainWindow.UsesBufferedForegroundVideo(sixEight, 0));
        Assert.False(TURZX.WinUsbDisplay.Studio.MainWindow.UsesBufferedForegroundVideo(sixEight, 1));
        Assert.Equal(0.75, TURZX.WinUsbDisplay.Studio.MainWindow.BufferedForegroundPixelScale(sixEight));

        var seven = new DisplayModel("7", 0x0070, 800, 1280);
        Assert.True(TURZX.WinUsbDisplay.Studio.MainWindow.UsesBufferedForegroundVideo(seven, 1));
        Assert.Equal(0.5, TURZX.WinUsbDisplay.Studio.MainWindow.BufferedForegroundPixelScale(seven));
    }

    [Fact]
    public async Task FastHtmlCapturePreservesTheSelectedImageBackground() => await OnStaAsync(() =>
    {
        var model = new DisplayModel("7", 0x0070, 32, 32);
        byte[] transparent = DisplayFrameProcessor.CreateBlankOverlay(model);
        bool captureJpeg = TURZX.WinUsbDisplay.Studio.MainWindow.CanCaptureHtmlAsJpeg(
            false, true, false, true);
        byte[] captured = captureJpeg ? DisplayFrameProcessor.PrepareDirectJpegFrame(transparent, model) : transparent;
        int[] blue = Enumerable.Repeat(0x0000ff, model.Width * model.Height).ToArray();
        var background = BitmapSource.Create(model.Width, model.Height, 96, 96, PixelFormats.Bgr32,
            null, blue, model.Width * 4);
        byte[] frame = DisplayFrameProcessor.PrepareLiveHtmlFrame(captured, MediaLayout.Default,
            background, MediaLayout.Default, model, false, 0, null, preferJpeg: true);
        var output = new FormatConvertedBitmap(DisplayFrameProcessor.DecodeFrame(frame), PixelFormats.Bgr32, null, 0);
        byte[] pixel = new byte[4];
        output.CopyPixels(new System.Windows.Int32Rect(16, 16, 1, 1), pixel, 4, 0);
        Assert.InRange(pixel[0], (byte)220, (byte)255);
        Assert.InRange(pixel[1], (byte)0, (byte)35);
        Assert.InRange(pixel[2], (byte)0, (byte)35);
    });

    [Theory]
    [InlineData((byte)1, false)]
    [InlineData((byte)3, true)]
    public async Task RotatedJpegPlacesBothHalvesInTheExpectedOrientation(byte rotation, bool redOnTop) => await OnStaAsync(() =>
    {
        var model = new DisplayModel("7", 0x0070, 64, 96);
        var canvas = model.ForRotation(rotation);
        int[] colors = new int[canvas.Width * canvas.Height];
        for (int y = 0; y < canvas.Height; y++)
        for (int x = 0; x < canvas.Width; x++)
            colors[y * canvas.Width + x] = x < canvas.Width / 2 ? 0xff0000 : 0x0000ff;
        var bitmap = BitmapSource.Create(canvas.Width, canvas.Height, 96, 96, PixelFormats.Bgr32,
            null, colors, canvas.Width * 4);
        byte[] jpeg = DisplayFrameProcessor.PrepareLiveBitmapFrame(bitmap, MediaLayout.Default, null,
            MediaLayout.Default, model, false, rotation, null, preferJpeg: true);
        var decoded = new FormatConvertedBitmap(DisplayFrameProcessor.DecodeFrame(jpeg), PixelFormats.Bgr32, null, 0);
        byte[] sample = new byte[4];
        foreach (bool top in new[] { true, false })
        {
            decoded.CopyPixels(new System.Windows.Int32Rect(model.Width / 2, model.Height * (top ? 1 : 3) / 4, 1, 1), sample, 4, 0);
            bool red = top == redOnTop;
            Assert.InRange(sample[red ? 2 : 0], (byte)220, (byte)255);
            Assert.InRange(sample[red ? 0 : 2], (byte)0, (byte)35);
        }
    });

    [Theory]
    [InlineData((ushort)0x0123, (byte)0)]
    [InlineData((ushort)0x0123, (byte)1)]
    [InlineData((ushort)0x0123, (byte)2)]
    [InlineData((ushort)0x0123, (byte)3)]
    [InlineData((ushort)0x0070, (byte)0)]
    [InlineData((ushort)0x0070, (byte)1)]
    [InlineData((ushort)0x0070, (byte)2)]
    [InlineData((ushort)0x0070, (byte)3)]
    public async Task HtmlJpegFallbackKeepsNativeDimensionsAfterRotation(ushort product, byte rotation) => await OnStaAsync(() =>
    {
        var model = new DisplayModel("test", product, 8, 6);
        byte[] screenshot = DisplayFrameProcessor.CreateBlankOverlay(model.ForRotation(rotation));
        byte[] frame = DisplayFrameProcessor.PrepareLiveHtmlFrame(screenshot, MediaLayout.Default, null,
            MediaLayout.Default, model, preserveTransparency: false, rotation, crop: null, preferJpeg: true);

        Assert.Equal(new byte[] { 0xff, 0xd8, 0xff }, frame[..3]);
        var decoded = DisplayFrameProcessor.DecodeFrame(frame);
        Assert.Equal(model.Width, decoded.PixelWidth);
        Assert.Equal(model.Height, decoded.PixelHeight);
    });

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
