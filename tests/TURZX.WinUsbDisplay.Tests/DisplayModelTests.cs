using TURZX.WinUsbDisplay;
using Xunit;

namespace TURZX.WinUsbDisplay.Tests;

/// <summary>验证内置显示设备型号目录。</summary>
public class DisplayModelTests
{
    /// <summary>验证已知型号使用 Win207 的 USB 厂商 ID。</summary>
    [Fact]
    public void VendorIdIsWin207()
    {
        Assert.Equal(0x1cbe, DisplayModel.VendorId);
    }

    /// <summary>验证型号目录条目数和 4.6 英寸型号。</summary>
    [Fact]
    public void CatalogHasExpectedSizeAnd46Inch()
    {
        Assert.Equal(9, DisplayModel.Known.Count);
        var model = Assert.Single(DisplayModel.Known, m => m.ProductId == 0x0046);
        Assert.Equal(320, model.Width);
        Assert.Equal(960, model.Height);
    }

    /// <summary>验证指定型号的尺寸与目录数据一致。</summary>
    [Theory]
    [InlineData(0x0088, 480, 1920)]
    [InlineData(0x0123, 720, 1920)]
    [InlineData(0x0021, 480, 480)]
    [InlineData(0xb021, 480, 480)]
    [InlineData(0x0050, 720, 1280)]
    [InlineData(0x0055, 720, 1280)]
    [InlineData(0x0070, 800, 1280)]
    [InlineData(0x0080, 800, 1280)]
    public void ModelDimensionsMatchCatalog(int pid, int width, int height)
    {
        var model = Assert.Single(DisplayModel.Known, m => m.ProductId == pid);
        Assert.Equal(width, model.Width);
        Assert.Equal(height, model.Height);
    }

    /// <summary>验证型号的宽高均为正偶数。</summary>
    [Fact]
    public void AllModelsHavePositiveEvenDimensions()
    {
        foreach (var model in DisplayModel.Known)
        {
            Assert.True(model.Width > 0 && model.Height > 0);
            Assert.Equal(0, model.Width % 2);
            Assert.Equal(0, model.Height % 2);
        }
    }
}
