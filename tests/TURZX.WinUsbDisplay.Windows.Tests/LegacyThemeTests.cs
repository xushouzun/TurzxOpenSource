using System.Drawing;
using System.IO;
using TURZX.WinUsbDisplay.Studio.NativeTheme;
using Xunit;

namespace TURZX.WinUsbDisplay.Windows.Tests;

public class LegacyThemeTests
{
    [Fact]
    public void BinaryThemeParsesIntoTheCurrentModel()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-theme.data");
        using NativeThemeDocument document = NativeThemeDocument.Load(path);

        Assert.True(document.IsLandscape);
        Assert.Equal(1, document.DeviceIndex);
        Assert.NotNull(document.Background);
        Assert.Equal(480, document.Background!.Width);
        Assert.Equal(320, document.Background.Height);

        NativeThemeGroup group = Assert.Single(document.Groups);
        Assert.True(group.Enabled);
        NativeThemeItem item = Assert.Single(group.Items);
        Assert.Equal("CpuTemp", item.Name);
        Assert.Equal(18f, item.FontSize);
        Assert.Equal("42", item.Value);

        NativeThemeBar bar = Assert.Single(document.Bars);
        Assert.Equal(150, bar.Length);
        Assert.Equal(13, bar.Height);
        Assert.Equal(Color.FromArgb(255, 0, 200, 255), bar.FrontColor);

        NativeThemeArc arc = Assert.Single(document.Arcs);
        Assert.Equal(98, arc.Radius);
        Assert.Equal(-15, arc.StartPercent);

        NativeThemeTip tip = Assert.Single(document.Tips);
        Assert.Equal("CPU", tip.Content);
        Assert.Equal(100, tip.Width);
        Assert.Equal(20, tip.Height);
    }
}
