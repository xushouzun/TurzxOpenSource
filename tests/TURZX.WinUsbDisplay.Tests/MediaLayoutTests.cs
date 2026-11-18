using TURZX.WinUsbDisplay;
using Xunit;

namespace TURZX.WinUsbDisplay.Tests;

/// <summary>验证设备媒体布局的默认值与相等性。</summary>
public class MediaLayoutTests
{
    /// <summary>验证默认布局按比例适应并居中。</summary>
    [Fact]
    public void DefaultIsFitCenterWithNoOffset()
    {
        Assert.True(MediaLayout.Default.IsDefault);
        Assert.False(MediaLayout.Default.Fill);
        Assert.Equal(0, MediaLayout.Default.OffsetX);
        Assert.Equal(0, MediaLayout.Default.OffsetY);
    }

    /// <summary>验证铺满选项使布局偏离默认值。</summary>
    [Fact]
    public void FillIsNotDefault()
    {
        Assert.False(new MediaLayout(Fill: true).IsDefault);
    }

    /// <summary>验证任意位置偏移都会改变默认布局。</summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, -1)]
    [InlineData(-3, 4)]
    public void AnyOffsetIsNotDefault(int x, int y)
    {
        Assert.False(new MediaLayout(OffsetX: x, OffsetY: y).IsDefault);
    }

    /// <summary>验证缩放会改变原生视频的布局。</summary>
    [Fact]
    public void ZoomChangesTheLayoutOfNativeVideo()
    {
        Assert.False((MediaLayout.Default with { ZoomPercent = 110 }).IsDefault);
        Assert.True((MediaLayout.Default with { ZoomPercent = 100 }).IsDefault);
    }

    /// <summary>验证拥有相同字段值的布局记录相等。</summary>
    [Fact]
    public void RecordsWithSameValuesAreEqual()
    {
        Assert.Equal(new MediaLayout(true, 2, -3), new MediaLayout(true, 2, -3));
        Assert.NotEqual(new MediaLayout(true, 2, -3), new MediaLayout(false, 2, -3));
    }
}