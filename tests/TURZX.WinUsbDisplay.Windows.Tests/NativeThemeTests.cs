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
        Assert.Equal(8, themes.Length);
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
    public void RegionRgb565ReadsOnlyTheRequestedRectangle()
    {
        using var bitmap = new Bitmap(2, 1);
        bitmap.SetPixel(0, 0, Color.Black);
        bitmap.SetPixel(1, 0, Color.Red);
        Assert.Equal(new byte[] { 0x00, 0xF8 },
            NativeThemeSerialSession.ToRgb565(bitmap, new Rectangle(1, 0, 1, 1)));
    }

    [Fact]
    public void FrameDiffOnlyReturnsChangedTiles()
    {
        using var previous = new Bitmap(64, 64);
        using var current = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(previous))
            graphics.Clear(Color.Black);
        using (Graphics graphics = Graphics.FromImage(current))
        {
            graphics.Clear(Color.Black);
            graphics.FillRectangle(Brushes.White, 20, 20, 4, 4);
        }

        Rectangle region = Assert.Single(NativeThemeFrameDiff.FindRegions(previous, current));
        Assert.Equal(new Rectangle(16, 16, 16, 16), region);
        Assert.Empty(NativeThemeFrameDiff.FindRegions(previous, previous));
    }

    [Fact]
    public void RejectsFilesThatAreNotBinaryThemes()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("theme.data");
        File.WriteAllText(path, """{"isLanscape":true,"deviceIndex":1}""");

        Assert.Throws<InvalidDataException>(() => NativeThemeDocument.Load(path));
    }

    [Fact]
    public void TextGlyphIsClippedToItsCell()
    {
        var background = new Bitmap(120, 80);
        using (Graphics graphics = Graphics.FromImage(background))
            graphics.Clear(Color.Black);
        using var theme = new NativeThemeDocument
        {
            IsLandscape = true,
            Background = background,
            // 原工具把字形居中在 (posX + offsetX, posY + offsetY) 并裁剪到 (posX, posY, W, H)，
            // 不能按格子矩形居中，否则字会整体向右下移半格。
            Groups =
            {
                new NativeThemeGroup
                {
                    Enabled = true,
                    Items =
                    {
                        new NativeThemeItem
                        {
                            Enabled = true, Name = "CpuTemp", Value = "8", Length = 2,
                            X = 10, Y = 20, Interval = 11, Width = 10, Height = 13,
                            OffsetX = 5, OffsetY = 8, FontSize = 18, Color = Color.White
                        }
                    }
                }
            }
        };

        using Bitmap frame = NativeThemeRenderer.Render(theme, new TestNativeThemeDataSource(new NativeThemeData()));
        var cell = new Rectangle(10, 20, 10, 13);
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
            {
                if (cell.Contains(x, y))
                    continue;
                Color pixel = frame.GetPixel(x, y);
                Assert.True(pixel.R < 16 && pixel.G < 16 && pixel.B < 16,
                    $"Glyph pixel drawn outside its cell at ({x}, {y}).");
            }
    }

    [Fact]
    public void CustomDataSourceSuppliesTextAndBarValues()
    {
        var cpuTemp = new NativeThemeItem { Enabled = true, Name = "CpuTemp", Value = "42", Length = 2 };
        Assert.Equal("42", NativeThemeRenderer.ResolveText(cpuTemp, null));
        Assert.Equal("45", NativeThemeRenderer.ResolveText(cpuTemp,
            new NativeThemeData { CpuTemperatureCelsius = 45 }));
        Assert.Equal("99", NativeThemeRenderer.ResolveText(cpuTemp,
            new NativeThemeData { CpuTemperatureCelsius = 105 }));

        var cpuLoad = new NativeThemeItem { Enabled = true, Name = "CpuLoad", Value = "88", Length = 2 };
        Assert.Equal(" 5", NativeThemeRenderer.ResolveText(cpuLoad,
            new NativeThemeData { CpuLoadPercent = 5 }));
        Assert.Equal("99", NativeThemeRenderer.ResolveText(cpuLoad,
            new NativeThemeData { CpuLoadPercent = 100 }));

        var cpuFreq = new NativeThemeItem { Enabled = true, Name = "CpuFreq", Value = "4.8", Length = 3 };
        Assert.Equal("4.8", NativeThemeRenderer.ResolveText(cpuFreq,
            new NativeThemeData { CpuFrequencyMhz = 4800 }));

        var background = new Bitmap(120, 60);
        using (Graphics graphics = Graphics.FromImage(background))
            graphics.Clear(Color.Black);
        using var theme = new NativeThemeDocument
        {
            IsLandscape = true,
            Background = background,
            Bars =
            {
                new NativeThemeBar
                {
                    Item = new NativeThemeItem { Enabled = true, Name = "CpuLoad", X = 10, Y = 10 },
                    Length = 100, Height = 10, DisplayLength = 100,
                    BackColor = Color.Black, FrontColor = Color.White
                }
            }
        };

        using Bitmap frame = NativeThemeRenderer.Render(theme,
            new TestNativeThemeDataSource(new NativeThemeData { CpuLoadPercent = 50 }));
        Assert.Equal(Color.White.ToArgb(), frame.GetPixel(20, 15).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), frame.GetPixel(70, 15).ToArgb());
    }

    [Fact]
    public void DefaultDataSourceProvidesMockSensorValues()
    {
        INativeThemeDataSource? source = NativeThemeDataSources.Current;
        Assert.IsType<NativeThemeMockDataSource>(source);
        NativeThemeData data = Assert.IsType<NativeThemeData>(source!.Read());
        Assert.InRange(data.CpuLoadPercent!.Value, 20, 45);
        Assert.InRange(data.CpuTemperatureCelsius!.Value, 50, 60);
        Assert.InRange(data.RamLoadPercent!.Value, 50, 65);
    }

    private sealed class TestNativeThemeDataSource(NativeThemeData data) : INativeThemeDataSource
    {
        public NativeThemeData? Read() => data;
    }
}
