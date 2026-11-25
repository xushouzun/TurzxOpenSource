using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;

namespace TURZX.WinUsbDisplay.Studio.NativeTheme;

/// <summary>将原生主题渲染为 3.5 寸设备的静态预览帧。</summary>
internal static class NativeThemeRenderer
{
    /// <summary>按主题方向渲染一帧示例画面。</summary>
    internal static Bitmap Render(NativeThemeDocument theme, INativeThemeDataSource? dataSource = null)
    {
        dataSource ??= NativeThemeDataSources.Current;
        NativeThemeData? data = dataSource?.Read();
        int width = theme.Background!.Width;
        int height = theme.Background.Height;

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
                foreach (NativeThemeItem item in group.Items)
                    DrawItem(graphics, item, width, height, data);
            }

            foreach (NativeThemeArc arc in theme.Arcs)
                DrawArc(graphics, arc, data);

            foreach (NativeThemeBar bar in theme.Bars)
                DrawBar(graphics, bar, data);

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
        graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
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

    private static void DrawItem(Graphics graphics, NativeThemeItem item, int canvasWidth, int canvasHeight,
        NativeThemeData? data)
    {
        if (!item.Enabled)
            return;
        string text = ResolveText(item, data);
        if (text.Length == 0 || item.FontSize <= 0)
            return;

        using var font = CreateFont(item.FontSize);
        using var brush = new SolidBrush(item.Color);
        // 长文本与原工具一致：从 posX/posY 按左上角绘制，不叠加字体偏移。
        if (item.Long || item.Interval <= 0)
        {
            graphics.DrawString(text, font, brush, item.X, item.Y);
            return;
        }

        int cellWidth = item.Width > 0 ? item.Width : Math.Max(1, item.Interval);
        int cellHeight = item.Height > 0 ? item.Height : (int)Math.Ceiling(item.FontSize);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        // 原工具把每个字形居中绘制在 (posX + i*interval + offsetX, posY + offsetY)，
        // 并裁剪到 (posX + i*interval, posY, W, H)；不能改成按单元格矩形居中，否则整体右下移半格。
        bool redrawFirst = item.Name == "CpuFreq" && text.Length > 1;
        for (int step = 0; step < text.Length + (redrawFirst ? 1 : 0); step++)
        {
            int index = step < text.Length ? step : 0;
            int x = item.X + index * item.Interval;
            Rectangle cell = Rectangle.Intersect(new Rectangle(x, item.Y, cellWidth, cellHeight),
                new Rectangle(0, 0, canvasWidth, canvasHeight));
            if (cell.Width <= 0 || cell.Height <= 0)
                continue;
            GraphicsState state = graphics.Save();
            graphics.SetClip(cell);
            graphics.DrawString(text[index].ToString(), font, brush,
                x + item.OffsetX, item.Y + item.OffsetY, format);
            graphics.Restore(state);
        }
    }

    private static void DrawBar(Graphics graphics, NativeThemeBar bar, NativeThemeData? data)
    {
        if (!bar.Item.Enabled || bar.Length <= 0)
            return;
        int height = bar.Height > 0 ? bar.Height : 13;
        using var back = new SolidBrush(bar.BackColor);
        graphics.FillRectangle(back, bar.Item.X, bar.Item.Y, bar.Length, height);
        int frontLength = GetBarPercent(bar.Item.Name, data) is { } percent
            ? bar.Length * percent / 100
            : bar.DisplayLength > 0 ? Math.Min(bar.DisplayLength, bar.Length) : bar.Length;
        if (frontLength > 0)
        {
            using var front = new SolidBrush(bar.FrontColor);
            graphics.FillRectangle(front, bar.Item.X, bar.Item.Y, frontLength, height);
        }
    }

    private static void DrawArc(Graphics graphics, NativeThemeArc arc, NativeThemeData? data)
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
        float sweep = GetArcPercent(arc.Name, data) is { } percent
            ? percent * 3.6f
            : 60f;
        if (sweep > 0)
            graphics.DrawArc(front, bounds, start, sweep);
    }

    /// <summary>优先使用外部数据源，未提供时回退到主题存档值和示例占位。</summary>
    internal static string ResolveText(NativeThemeItem item, NativeThemeData? data)
    {
        string text = FormatText(item, data) ?? DisplayText(item);
        if (!item.Long && item.Length > 0 && text.Length > item.Length)
            text = text[..item.Length];
        return text;
    }

    /// <summary>把主题条目名映射到传感器快照；未提供的指标返回 null。</summary>
    private static string? FormatText(NativeThemeItem item, NativeThemeData? data)
    {
        if (data is null)
            return null;
        return item.Name switch
        {
            "CpuLoad" => data.CpuLoadPercent is { } cpuLoad ? FormatPercent(cpuLoad, item.Length) : null,
            "CpuTemp" => data.CpuTemperatureCelsius is { } cpuTemp ? FormatTemperature(cpuTemp, item.Length) : null,
            "CpuFreq" => data.CpuFrequencyMhz is { } cpuFreq ? FormatFrequency(cpuFreq) : null,
            "CpuFan" => data.CpuFanRpm is { } cpuFan ? FormatInteger(cpuFan, 5) : null,
            "CpuPower" => data.CpuPowerWatts is { } cpuPower ? FormatInteger(cpuPower, 3) : null,
            "GpuLoad" => data.GpuLoadPercent is { } gpuLoad ? FormatPercent(gpuLoad, item.Length) : null,
            "GpuTemp" => data.GpuTemperatureCelsius is { } gpuTemp ? FormatTemperature(gpuTemp, item.Length) : null,
            "GpuFan" => data.GpuFanRpm is { } gpuFan ? FormatInteger(gpuFan, 5) : null,
            "GpuRam" => data.GpuMemoryUsedMb is { } gpuRam ? FormatInteger(gpuRam, 5) : null,
            "GpuRamLoad" => data.GpuMemoryLoadPercent is { } gpuRamLoad ? FormatPercent(gpuRamLoad, item.Length) : null,
            "RamLoad" => data.RamLoadPercent is { } ramLoad ? FormatPercent(ramLoad, item.Length) : null,
            "RamValue" => data.RamUsedMb is { } ram ? FormatInteger(ram, 5) : null,
            "RamTemp" => data.RamTemperatureCelsius is { } ramTemp ? FormatTemperature(ramTemp, item.Length) : null,
            "HDDLoad" => data.DiskLoadPercent is { } diskLoad ? FormatPercent(diskLoad, item.Length) : null,
            "HDDTemp" => data.DiskTemperatureCelsius is { } diskTemp ? FormatTemperature(diskTemp, item.Length) : null,
            "Volume" or "VulomeLoad" => data.VolumePercent is { } volume ? FormatPercent(volume, item.Length) : null,
            "InternetUp" => data.NetworkUploadMbps is { } upload ? FormatNetworkRate(upload) : null,
            "InternetDown" => data.NetworkDownloadMbps is { } download ? FormatNetworkRate(download) : null,
            "Time" => data.Time?.ToString("HH:mm", CultureInfo.InvariantCulture),
            "Date" => data.Time?.ToString("yyyy/M/d", CultureInfo.InvariantCulture),
            "Weather" => data.Weather,
            "Model" => data.Model,
            _ => null
        };
    }

    private static int? GetBarPercent(string name, NativeThemeData? data)
    {
        if (data is null)
            return null;
        double? value = name switch
        {
            "CpuLoad" => data.CpuLoadPercent,
            "CpuTemp" => data.CpuTemperatureCelsius,
            "RamLoad" => data.RamLoadPercent,
            "GpuLoad" => data.GpuLoadPercent,
            "GpuTemp" => data.GpuTemperatureCelsius,
            "Volume" or "VulomeLoad" => data.VolumePercent,
            // 原工具把 8000 MHz 视为满条。
            "CpuFreq" => data.CpuFrequencyMhz / 80d,
            "GpuRamLoad" => data.GpuMemoryLoadPercent,
            "HDDLoad" => data.DiskLoadPercent,
            _ => null
        };
        return value is { } number ? ToPercent(number) : null;
    }

    private static int? GetArcPercent(string name, NativeThemeData? data)
    {
        if (data is null)
            return null;
        double? value = name switch
        {
            "CpuLoad" => data.CpuLoadPercent,
            "RamLoad" => data.RamLoadPercent,
            "GpuLoad" => data.GpuLoadPercent,
            "GpuRamLoad" => data.GpuMemoryLoadPercent,
            "Volume" or "VulomeLoad" => data.VolumePercent,
            "HDDLoad" => data.DiskLoadPercent,
            _ => null
        };
        return value is { } number ? ToPercent(number) : null;
    }

    private static int ToPercent(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0;
        return (int)Math.Clamp(value, 0d, 100d);
    }

    private static int ToInteger(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0;
        return (int)Math.Clamp(value, 0d, int.MaxValue);
    }

    private static string FormatPercent(double value, int length)
    {
        int percent = ToPercent(value);
        // 原工具对两位百分比条目的 100 显示为 99，避免占用第三格。
        if (length == 2 && percent > 99)
            percent = 99;
        return percent.ToString(CultureInfo.InvariantCulture).PadLeft(2, ' ');
    }

    private static string FormatTemperature(double value, int length)
    {
        int temperature = ToInteger(value);
        if (length == 2 && temperature > 99)
            temperature = 99;
        return temperature.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');
    }

    private static string FormatInteger(double value, int width) =>
        ToInteger(value).ToString(CultureInfo.InvariantCulture).PadLeft(width, ' ');

    private static string FormatFrequency(double mhz)
    {
        if (double.IsNaN(mhz) || double.IsInfinity(mhz) || mhz < 0)
            mhz = 0;
        return Math.Round(mhz / 1000d, 1).ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static string FormatNetworkRate(double rate)
    {
        if (double.IsNaN(rate) || double.IsInfinity(rate) || rate < 0)
            rate = 0;
        int tenths = (int)(Math.Min(rate, 99.9) * 10);
        if (tenths == 0 && rate > 0.01)
            tenths = 1;
        return tenths.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0').PadLeft(3, ' ').Insert(2, ".");
    }

    private static Font CreateFont(float size) =>
        new("Arial", Math.Max(6f, size), FontStyle.Bold, GraphicsUnit.Point);

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
