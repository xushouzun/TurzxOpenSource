using System.Drawing;
using System.IO;

namespace TURZX.WinUsbDisplay.Studio.NativeTheme;

/// <summary>解析 3.5 寸原生主题的二进制 .data 文件。</summary>
internal sealed class NativeThemeDocument : IDisposable
{
    internal bool IsLandscape { get; set; }
    internal int DeviceIndex { get; set; }
    internal bool UseOsdWithFontBack { get; set; }
    internal Bitmap? Background { get; set; }
    internal Bitmap? Osd { get; set; }
    internal Bitmap? OsdWithFontBack { get; set; }
    internal List<NativeThemeGroup> Groups { get; } = [];
    internal List<NativeThemeBar> Bars { get; } = [];
    internal List<NativeThemeArc> Arcs { get; } = [];
    internal List<NativeThemeTip> Tips { get; } = [];

    /// <summary>读取二进制主题文件。</summary>
    internal static NativeThemeDocument Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return NativeThemeReader.Read(stream);
    }

    public void Dispose()
    {
        Background?.Dispose();
        Osd?.Dispose();
        OsdWithFontBack?.Dispose();
        foreach (NativeThemeGroup group in Groups)
            group.Background?.Dispose();
    }
}

internal sealed class NativeThemeGroup
{
    internal bool Enabled { get; init; }
    internal int BaseX { get; init; }
    internal int BaseY { get; init; }
    internal Bitmap? Background { get; init; }
    internal List<NativeThemeItem> Items { get; } = [];
}

internal sealed class NativeThemeItem
{
    internal bool Enabled { get; init; }
    internal bool Long { get; init; }
    internal string Name { get; init; } = "";
    internal string Value { get; init; } = "";
    internal int Length { get; init; }
    internal int X { get; init; }
    internal int Y { get; init; }
    internal int Interval { get; init; }
    internal int Width { get; init; }
    internal int Height { get; init; }
    internal int OffsetX { get; init; }
    internal int OffsetY { get; init; }
    internal float FontSize { get; init; }
    internal Color Color { get; init; } = Color.White;
}

internal sealed class NativeThemeBar
{
    internal NativeThemeItem Item { get; init; } = new();
    internal int Length { get; init; }
    internal int Height { get; init; }
    internal int DisplayLength { get; init; }
    internal Color BackColor { get; init; }
    internal Color FrontColor { get; init; }
}

internal sealed class NativeThemeArc
{
    internal string Name { get; init; } = "";
    internal bool Enabled { get; init; }
    internal int PosX { get; init; }
    internal int PosY { get; init; }
    internal int OffsetX { get; init; }
    internal int OffsetY { get; init; }
    internal int Radius { get; init; }
    internal float Width { get; init; }
    internal int StartPercent { get; init; }
    internal Color BackColor { get; init; }
    internal Color FrontColor { get; init; }
}

internal sealed class NativeThemeTip
{
    internal string Content { get; init; } = "";
    internal int X { get; init; }
    internal int Y { get; init; }
    internal int Width { get; init; }
    internal int Height { get; init; }
    internal float Size { get; init; }
    internal Color Color { get; init; }
}
