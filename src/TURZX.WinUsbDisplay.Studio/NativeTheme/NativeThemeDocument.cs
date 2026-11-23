using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;

namespace TURZX.WinUsbDisplay.Studio.NativeTheme;

/// <summary>解析原项目 JSON 格式的 3.5 寸原生主题。</summary>
internal sealed class NativeThemeDocument : IDisposable
{
    internal bool IsLandscape { get; private init; }
    internal int DeviceIndex { get; private init; }
    internal bool UseOsdWithFontBack { get; private init; }
    internal Bitmap? Background { get; private init; }
    internal Bitmap? Osd { get; private init; }
    internal Bitmap? OsdWithFontBack { get; private init; }
    internal List<NativeThemeGroup> Groups { get; } = [];
    internal List<NativeThemeBar> Bars { get; } = [];
    internal List<NativeThemeArc> Arcs { get; } = [];
    internal List<NativeThemeTip> Tips { get; } = [];

    /// <summary>读取 JSON 主题文件。</summary>
    internal static NativeThemeDocument Load(string path)
    {
        using JsonDocument document = Parse(path);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Native theme must contain a JSON object.");

        var theme = new NativeThemeDocument
        {
            IsLandscape = ReadBool(root, "isLanscape"),
            DeviceIndex = ReadInt(root, "deviceIndex"),
            UseOsdWithFontBack = ReadBool(root, "useOsdWithFontBack"),
            Background = ReadBitmap(root, "backImg"),
            Osd = ReadBitmap(root, "osdImg"),
            OsdWithFontBack = ReadBitmap(root, "osdWithFontBack")
        };
        if (theme.Background is null)
            throw new InvalidDataException("Native theme has no background image.");
        int backgroundWidth = theme.Background.Width;
        int backgroundHeight = theme.Background.Height;
        if (!((backgroundWidth == 480 && backgroundHeight == 320)
            || (backgroundWidth == 320 && backgroundHeight == 480)))
            throw new InvalidDataException("Native theme canvas must be 480x320 or 320x480 for the 3.5-inch display.");

        foreach (JsonElement group in ReadArray(root, "groupList"))
        {
            var parsed = new NativeThemeGroup
            {
                Enabled = ReadBool(group, "enabled"),
                BaseX = ReadInt(group, "baseX"),
                BaseY = ReadInt(group, "baseY"),
                Background = ReadBitmap(group, "backImg")
            };
            foreach (JsonElement item in ReadArray(group, "itemList"))
                parsed.Items.Add(ParseItem(item));
            theme.Groups.Add(parsed);
        }

        foreach (JsonElement bar in ReadArray(root, "barList"))
        {
            theme.Bars.Add(new NativeThemeBar
            {
                Item = ParseItem(bar),
                Length = ReadInt(bar, "length"),
                Height = ReadInt(bar, "height"),
                DisplayLength = ReadInt(bar, "displayLen"),
                BackColor = ReadColor(bar, "backColor"),
                FrontColor = ReadColor(bar, "frontColor")
            });
        }

        foreach (JsonElement arc in ReadArray(root, "archList"))
        {
            theme.Arcs.Add(new NativeThemeArc
            {
                Enabled = ReadBool(arc, "enabled"),
                PosX = ReadInt(arc, "posX"),
                PosY = ReadInt(arc, "posY"),
                OffsetX = ReadInt(arc, "offsetX"),
                OffsetY = ReadInt(arc, "offsetY"),
                Radius = ReadInt(arc, "archR"),
                Width = ReadFloat(arc, "archWidth"),
                StartPercent = ReadInt(arc, "startPer"),
                BackColor = ReadColor(arc, "backArchColor"),
                FrontColor = ReadColor(arc, "frontArchColor")
            });
        }

        foreach (JsonElement tip in ReadArray(root, "tipList"))
        {
            theme.Tips.Add(new NativeThemeTip
            {
                Content = ReadString(tip, "content"),
                X = ReadInt(tip, "posX"),
                Y = ReadInt(tip, "posY"),
                Width = ReadInt(tip, "W"),
                Height = ReadInt(tip, "H"),
                Size = ReadFloat(tip, "size") is var size && size > 0 ? size : ReadFloat(tip, "font", "size"),
                Color = ReadColor(tip, "color")
            });
        }

        return theme;
    }

    private static JsonDocument Parse(string path)
    {
        string text = File.ReadAllText(path).TrimStart();
        if (text.Length == 0 || text[0] != '{')
            throw new InvalidDataException("Only JSON .data native themes are supported.");
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Native theme JSON is invalid.", error);
        }
    }

    private static NativeThemeItem ParseItem(JsonElement item)
    {
        JsonElement font = Property(item, "fontType") ?? default;
        return new NativeThemeItem
        {
            Enabled = ReadBool(item, "enabled"),
            Long = ReadBool(item, "isLongStr"),
            Name = ReadString(item, "itemName"),
            Value = ReadString(item, "valueStr"),
            Length = ReadInt(item, "strLen"),
            X = ReadInt(item, "posX"),
            Y = ReadInt(item, "posY"),
            Interval = ReadInt(font, "interval"),
            Width = ReadInt(font, "W"),
            Height = ReadInt(font, "H"),
            OffsetX = ReadInt(font, "offsetX"),
            OffsetY = ReadInt(font, "offsetY"),
            FontSize = ReadFloat(font, "size"),
            Color = ReadColor(font, "color")
        };
    }

    private static IEnumerable<JsonElement> ReadArray(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    private static JsonElement? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? value : null;

    private static string ReadString(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? "" : "";

    private static int ReadInt(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out int result) ? result : 0;

    private static float ReadFloat(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetSingle(out float result) ? result : 0;

    private static float ReadFloat(JsonElement element, string parent, string name) =>
        Property(element, parent) is { } value ? ReadFloat(value, name) : 0;

    private static bool ReadBool(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.True };

    private static Color ReadColor(JsonElement element, string name) =>
        Property(element, name) is { } value && Property(value, "argb") is { ValueKind: JsonValueKind.Number } argb
            && argb.TryGetInt32(out int result) ? System.Drawing.Color.FromArgb(result) : System.Drawing.Color.White;

    private static Bitmap? ReadBitmap(JsonElement element, string name)
    {
        if (Property(element, name) is not { } value)
            return null;
        string? base64 = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object when Property(value, "png") is { ValueKind: JsonValueKind.String } png => png.GetString(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(base64))
            return null;
        byte[] bytes = Convert.FromBase64String(base64);
        using var stream = new MemoryStream(bytes);
        using var temporary = new Bitmap(stream);
        return temporary.Clone(new Rectangle(0, 0, temporary.Width, temporary.Height), PixelFormat.Format32bppArgb);
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
