namespace TURZX.WinUsbDisplay;

/// <summary>描述媒体在设备画布中的缩放和位置。</summary>
/// <param name="Fill">为 true 时等比铺满并裁剪，为 false 时等比缩放至画布内。</param>
/// <param name="OffsetX">水平偏移量，单位为设备像素，正值向右。</param>
/// <param name="OffsetY">垂直偏移量，单位为设备像素，正值向下。</param>
public sealed record MediaLayout(bool Fill = false, int OffsetX = 0, int OffsetY = 0)
{
    /// <summary>获取无偏移的默认等比居中布局。</summary>
    public static MediaLayout Default { get; } = new();

    /// <summary>以适应或铺满后的尺寸为基准，缩放范围为 25% 至 400%。</summary>
    public int ZoomPercent { get; init; } = 100;

    /// <summary>获取限制在有效范围内、以画布中心为基准的缩放倍率。</summary>
    public double ZoomFactor => Math.Clamp(ZoomPercent, 25, 400) / 100d;

    /// <summary>指示布局是否为无偏移且无额外缩放的默认等比居中布局。</summary>
    public bool IsDefault => !Fill && OffsetX == 0 && OffsetY == 0 && ZoomFactor == 1;
}
