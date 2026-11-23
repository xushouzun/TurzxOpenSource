using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace TURZX.WinUsbDisplay.Studio.NativeTheme;

/// <summary>将原生主题渲染为 3.5 寸设备的静态预览帧。</summary>
internal static class NativeThemeRenderer
{
    private const int LandscapeWidth = 480;
    private const int LandscapeHeight = 320;

    /// <summary>按主题方向渲染一帧示例画面。</summary>
    internal static Bitmap Render(NativeThemeDocument theme)
    {
        int width = theme.Background!.Width;
        int height = theme.Background.Height;
        if (theme.IsLandscape && width < height)
            (width, height) = (LandscapeWidth, LandscapeHeight);

        var bitmap = new Bitmap(width, height);
        try
        {
            bitmap.SetResolution(72, 72);
            using Graphics graphics = Graphics.FromImage(bitmap);
            Configure(graphics);
            graphics.DrawImage(theme.Background, new Rectangle(0, 0, width, height),
                new Rectangle(0, 0, theme.Background.Width, theme.Background.Height), GraphicsUnit.Pixel);

            Bitmap? osd = theme.UseOsdWithFontBack ? theme.OsdWithFontBack ?? theme.Osd : theme.Osd;
            if (osd is not null)
                graphics.DrawImage(osd, new Rectangle(0, 0, Math.Min(width, osd.Width), Math.Min(height, osd.Height)),
                    new Rectangle(0, 0, osd.Width, osd.Height), GraphicsUnit.Pixel);

            foreach (NativeThemeTip tip in theme.Tips)
                DrawTip(graphics, tip);

            foreach (NativeThemeGroup group in theme.Groups)
            {
                if (!group.Enabled)
                    continue;
                if (group.Background is not null)
                    graphics.DrawImage(group.Background,
                        new Rectangle(group.BaseX, group.BaseY, group.Background.Width, group.Background.Height),
                        new Rectangle(0, 0, group.Background.Width, group.Background.Height), GraphicsUnit.Pixel);
                foreach (NativeThemeItem item in group.Items)
                    DrawItem(graphics, item);
            }

            foreach (NativeThemeArc arc in theme.Arcs)
                DrawArc(graphics, arc);

            foreach (NativeThemeBar bar in theme.Bars)
                DrawBar(graphics, bar);

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static void Configure(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    private static void DrawTip(Graphics graphics, NativeThemeTip tip)
    {
        if (string.IsNullOrEmpty(tip.Content) || tip.Size <= 0)
            return;
        using var font = CreateFont(tip.Size);
        using var brush = new SolidBrush(tip.Color);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(tip.Content, font, brush,
            new RectangleF(tip.X, tip.Y, Math.Max(1, tip.Width), Math.Max(1, tip.Height)), format);
    }

    private static void DrawItem(Graphics graphics, NativeThemeItem item)
    {
        if (!item.Enabled)
            return;
        string text = DisplayText(item);
        if (text.Length == 0 || item.FontSize <= 0)
            return;

        using var font = CreateFont(item.FontSize);
        using var brush = new SolidBrush(item.Color);
        if (item.Long || item.Interval <= 0)
        {
            graphics.DrawString(text, font, brush, item.X + item.OffsetX, item.Y + item.OffsetY);
            return;
        }

        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        int cellWidth = item.Width > 0 ? item.Width : Math.Max(1, item.Interval);
        int cellHeight = item.Height > 0 ? item.Height : (int)Math.Ceiling(item.FontSize);
        for (int index = 0; index < text.Length; index++)
        {
            float x = item.X + index * item.Interval + item.OffsetX;
            float y = item.Y + item.OffsetY;
            graphics.DrawString(text[index].ToString(), font, brush,
                new RectangleF(x, y, cellWidth, cellHeight), format);
        }
    }

    private static void DrawBar(Graphics graphics, NativeThemeBar bar)
    {
        if (!bar.Item.Enabled || bar.Length <= 0)
            return;
        int height = bar.Height > 0 ? bar.Height : 13;
        using var back = new SolidBrush(bar.BackColor);
        graphics.FillRectangle(back, bar.Item.X, bar.Item.Y, bar.Length, height);
        int frontLength = bar.DisplayLength > 0 ? Math.Min(bar.DisplayLength, bar.Length) : bar.Length;
        if (frontLength > 0)
        {
            using var front = new SolidBrush(bar.FrontColor);
            graphics.FillRectangle(front, bar.Item.X, bar.Item.Y, frontLength, height);
        }
    }

    private static void DrawArc(Graphics graphics, NativeThemeArc arc)
    {
        if (!arc.Enabled || arc.Radius <= 0 || arc.Width <= 0)
            return;
        const int rasterOrigin = 30;
        int x = arc.PosX + rasterOrigin - arc.OffsetX;
        int y = arc.PosY + rasterOrigin - arc.OffsetY;
        var bounds = new Rectangle(x, y, arc.Radius, arc.Radius);
        using var back = new Pen(arc.BackColor, arc.Width);
        using var front = new Pen(arc.FrontColor, arc.Width);
        graphics.DrawArc(back, bounds, 0, 360);
        float start = ((arc.StartPercent % 100) + 100) % 100 * 3.6f;
        graphics.DrawArc(front, bounds, start, 60);
    }

    private static Font CreateFont(float size) =>
        new("Arial", Math.Max(6f, size), FontStyle.Bold, GraphicsUnit.Pixel);

    private static string DisplayText(NativeThemeItem item)
    {
        string text = item.Value ?? "";
        if (text.Length == 0)
        {
            text = item.Name switch
            {
                "Time" => "23:58",
                "CpuFreq" => "4.8",
                _ => new string('8', Math.Max(1, Math.Min(item.Length, 16)))
            };
        }
        if (!item.Long && item.Length > 0 && text.Length > item.Length)
            text = text[..item.Length];
        return text.Replace('x', '8');
    }
}
