using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Threading;
using TURZX.WinUsbDisplay.Studio;
using TURZX.WinUsbDisplay.Studio.NativeTheme;
using Xunit;

namespace TURZX.WinUsbDisplay.Windows.Tests;

/// <summary>验证 3.5 寸原生主题设备的映射、解析和预览。</summary>
[Collection("StudioUi")]
public class NativeThemeTests
{
    [Fact]
    public void SelectingNativeThemeHidesWinUsbForegroundControls()
    {
        var native = new NativeThemeDevice("COM42", "USB35INCHIPSV2", "USB35INCHIPSV2");
        using var directory = new TemporaryDirectory();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow(new StudioPreferences(directory.File("settings.json")),
                    static () => [], () => [native]);
                try
                {
                    RunOnDispatcher(window, async () =>
                    {
                        await window.RefreshDevicesAsync();
                        for (int attempt = 0; attempt < 50 && window.NativeThemeImage.Source is null; attempt++)
                            await Task.Delay(50);
                    });
                    Assert.Single(window.WorkspaceDevice.Items);
                    Assert.Equal(Visibility.Visible, window.NativeThemeSettings.Visibility);
                    Assert.Equal(Visibility.Collapsed, window.LiveSettings.Visibility);
                    Assert.Equal(Visibility.Collapsed, window.LayoutBar.Visibility);
                    Assert.NotNull(window.NativeThemeImage.Source);
                    Assert.True(window.BrightnessValue.IsEnabled);
                    Assert.True(window.Rotation.IsEnabled);
                    Assert.Equal(Visibility.Collapsed, window.Rotation90.Visibility);
                    Assert.Equal(Visibility.Collapsed, window.Rotation270.Visibility);
                    Assert.True(window.NativeThemeChoice.Items.Count >= 1);
                    Assert.NotNull(window.NativeThemeChoice.SelectedItem);
                    Assert.Equal(Visibility.Collapsed, window.AdvancedNav.Visibility);
                    Assert.Equal(Visibility.Collapsed, window.CommandsNav.Visibility);
                }
                finally
                {
                    window.Close();
                }
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Native theme UI did not initialize.");
        if (failure is not null)
            throw failure;
    }

    private static void RunOnDispatcher(MainWindow window, Func<Task> check)
    {
        var dispatcher = window.Dispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var frame = new DispatcherFrame();
        Task pending = check().WaitAsync(TimeSpan.FromSeconds(10));
        _ = pending.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        pending.GetAwaiter().GetResult();
    }

    [Fact]
    public void NativeThemePathMapsToThreePointFiveDisplay()
    {
        var device = new NativeThemeDevice("COM9", "USB35INCHIPSV2", "USB35INCHIPSV2");
        var display = device.ToDisplayDevice();

        Assert.Equal("serial:COM9", display.Path);
        Assert.Equal("3.5 inch", display.Model.Name);
        Assert.Equal(480, display.Model.Width);
        Assert.Equal(320, display.Model.Height);

        var parsed = NativeThemeDevice.FromPath(display.Path);
        Assert.NotNull(parsed);
        Assert.Equal("COM9", parsed.PortName);
        Assert.Null(NativeThemeDevice.FromPath("\\\\?\\usb#vid_1cbe&pid_0068#test"));
    }

    [Fact]
    public void BundledFactoryThemesRender()
    {
        string? directory = FindNativeThemeDirectory();
        Assert.NotNull(directory);
        string[] themes = Directory.GetFiles(directory, "*.data");
        Assert.Equal(3, themes.Length);
        foreach (string path in themes)
        {
            using NativeThemeDocument theme = NativeThemeDocument.Load(path);
            using Bitmap frame = NativeThemeRenderer.Render(theme);
            Assert.True(frame.Width > 0 && frame.Height > 0);
            Assert.NotNull(NativeThemePreview.Render(path));
            if (Environment.GetEnvironmentVariable("WINUSB_DISPLAY_NATIVE_THEME_PREVIEW_DIR") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                frame.Save(Path.Combine(output, Path.GetFileNameWithoutExtension(path) + ".png"), ImageFormat.Png);
            }
        }
    }

    private static string? FindNativeThemeDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "TURZX.WinUsbDisplay.Studio", "NativeThemes");
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    [Fact]
    public void SerialHeaderMatchesOriginalEncoding()
    {
        Assert.Equal(new byte[] { 0, 0, 7, 125, 63, 197 },
            NativeThemeSerialSession.BuildRegisterHeader(197, 0, 0, 479, 319));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 255 },
            NativeThemeSerialSession.BuildRegisterHeader(255, 0, 0, 0, 0));
    }

    [Fact]
    public void ControlPacketsKeepTheOriginalSingleWriteFraming()
    {
        // 原项目把镜像/方向作为固定 16 字节包一次写出：6 字节头 + 10 字节数据。
        // 拆成 6 字节头加 16 字节载荷会让固件多读 6 字节，后续亮度和图像全部错位。
        var mirror = new byte[16];
        byte[] packet = NativeThemeSerialSession.BuildRegisterPacket(122, 0, 0, 0, 0, mirror);
        Assert.Equal(16, packet.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 122 }, packet[..6]);
        Assert.Equal(0, packet[6]);

        var rotation = new byte[16];
        rotation[6] = 102; // 横屏 2 + 100
        rotation[7] = 0x01; rotation[8] = 0xE0; // 480
        rotation[9] = 0x01; rotation[10] = 0x40; // 320
        packet = NativeThemeSerialSession.BuildRegisterPacket(121, 0, 0, 0, 0, rotation);
        Assert.Equal(16, packet.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 121 }, packet[..6]);
        Assert.Equal(new byte[] { 102, 0x01, 0xE0, 0x01, 0x40 }, packet[6..11]);

        // 无载荷的控制包仍然只有 6 字节，亮度/唤醒不受影响。
        Assert.Equal(new byte[] { 42, 0, 0, 0, 0, 110 },
            NativeThemeSerialSession.BuildRegisterPacket(110, 168, 0, 0, 0, null));
    }

    [Fact]
    public void BrightnessRegisterUsesInvertedValue()
    {
        // 原项目寄存器 110 越大越暗：0 最亮，255 最暗。
        Assert.Equal((byte)255, NativeThemeSerialSession.BrightnessRegisterValue(0));
        Assert.Equal((byte)87, NativeThemeSerialSession.BrightnessRegisterValue(168));
        Assert.Equal((byte)0, NativeThemeSerialSession.BrightnessRegisterValue(255));
    }

    [Fact]
    public void Rgb565UsesLittleEndian()
    {
        using var bitmap = new Bitmap(1, 1);
        bitmap.SetPixel(0, 0, Color.Red);
        Assert.Equal(new byte[] { 0x00, 0xF8 }, NativeThemeSerialSession.ToRgb565(bitmap));
    }

    [Fact]
    public void RejectsThemesForOtherCanvasSizes()
    {
        using var directory = new TemporaryDirectory();
        using var background = new Bitmap(224, 64);
        using var stream = new MemoryStream();
        background.Save(stream, ImageFormat.Png);
        string png = Convert.ToBase64String(stream.ToArray());
        string path = directory.File("wrong-size.data");
        File.WriteAllText(path, """
            {"isLanscape":true,"deviceIndex":1,"backImg":{"png":"__PNG__"},
             "groupList":[],"barList":[],"archList":[],"tipList":[]}
            """.Replace("__PNG__", png));

        Assert.Throws<InvalidDataException>(() => NativeThemeDocument.Load(path));
    }

    [Fact]
    public void JsonThemeRendersAtNativeCanvas()
    {
        using var directory = new TemporaryDirectory();
        using var background = new Bitmap(480, 320);
        using var stream = new MemoryStream();
        background.Save(stream, ImageFormat.Png);
        string png = Convert.ToBase64String(stream.ToArray());
        string path = directory.File("theme.data");
        File.WriteAllText(path, """
            {"isLanscape":true,"deviceIndex":1,"backImg":{"png":"__PNG__"},
             "groupList":[{"enabled":true,"baseX":0,"baseY":0,"backImg":null,
               "itemList":[{"enabled":true,"isLongStr":false,"itemName":"CpuLoad","strLen":2,
                 "posX":10,"posY":20,"valueStr":"42",
                 "fontType":{"size":20,"color":{"argb":-1},"offsetX":0,"offsetY":0,"interval":14,"W":14,"H":22}}]}],
             "barList":[],"archList":[],"tipList":[]}
            """.Replace("__PNG__", png));

        using NativeThemeDocument theme = NativeThemeDocument.Load(path);
        Assert.True(theme.IsLandscape);
        Assert.Single(theme.Groups);
        Assert.Equal("42", theme.Groups[0].Items[0].Value);

        using Bitmap frame = NativeThemeRenderer.Render(theme);
        Assert.Equal(480, frame.Width);
        Assert.Equal(320, frame.Height);
    }
}
