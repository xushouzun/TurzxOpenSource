using System.Formats.Nrbf;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;

namespace TURZX.WinUsbDisplay.Studio.NativeTheme;

/// <summary>读取 3.5 寸原生主题的二进制 .data 文件（.NET BinaryFormatter / NRBF）。</summary>
internal static class NativeThemeReader
{
    private static readonly object TypeGate = new();
    private static readonly Dictionary<string, Type> ElementTypes = new(StringComparer.Ordinal);
    private static readonly Lazy<ModuleBuilder> TypeModule = new(() =>
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("NativeThemeTypes"), AssemblyBuilderAccess.Run);
        return assembly.DefineDynamicModule("NativeThemeTypes");
    });

    /// <summary>解析二进制主题对象并转换为当前主题模型。</summary>
    internal static NativeThemeDocument Read(Stream stream)
    {
        ClassRecord root;
        try
        {
            root = NrbfDecoder.Decode(stream) as ClassRecord
                ?? throw new InvalidDataException("Native theme root is not an object.");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new InvalidDataException("Not a native theme .data file.", error);
        }

        string typeName = root.TypeName?.FullName ?? "";
        if (!typeName.EndsWith("ThemeConfig", StringComparison.Ordinal))
            throw new InvalidDataException("Unsupported native theme object.");

        var theme = new NativeThemeDocument
        {
            IsLandscape = Bool(root, Field("isLanscape")),
            DeviceIndex = Int(root, Field("deviceIndex")),
            UseOsdWithFontBack = Bool(root, Field("useOsdWithFontBack")),
            Background = Bitmap(root, Field("backImg")),
            Osd = Bitmap(root, Field("osdImg")),
            OsdWithFontBack = Bitmap(root, Field("osdWithFontBack"))
        };
        if (theme.Background is null)
            throw new InvalidDataException("Native theme has no background image.");
        int width = theme.Background.Width;
        int height = theme.Background.Height;
        if (!((width == 480 && height == 320) || (width == 320 && height == 480)))
            throw new InvalidDataException("Native theme canvas must be 480x320 or 320x480 for the 3.5-inch display.");

        foreach (ClassRecord group in List(root, Field("groupList")))
        {
            var parsed = new NativeThemeGroup
            {
                Enabled = Bool(group, Field("enabled")),
                BaseX = Int(group, Field("baseX")),
                BaseY = Int(group, Field("baseY")),
                Background = Bitmap(group, Field("backImg"))
            };
            foreach (ClassRecord item in List(group, Field("itemList")))
                parsed.Items.Add(Item(item, inherited: false));
            theme.Groups.Add(parsed);
        }

        foreach (ClassRecord bar in List(root, Field("barList")))
        {
            theme.Bars.Add(new NativeThemeBar
            {
                Item = Item(bar, inherited: true),
                Length = Int(bar, Field("length")),
                Height = Int(bar, Field("height")),
                DisplayLength = Int(bar, Field("displayLen")),
                BackColor = Color(bar, Field("backColor")),
                FrontColor = Color(bar, Field("frontColor"))
            });
        }

        foreach (ClassRecord arc in List(root, Field("archList")))
        {
            theme.Arcs.Add(new NativeThemeArc
            {
                Name = String(arc, Field("itemName"), Inherited("itemName")),
                Enabled = Bool(arc, Field("enabled"), Inherited("enabled")),
                PosX = Int(arc, Field("posX"), Inherited("posX")),
                PosY = Int(arc, Field("posY"), Inherited("posY")),
                OffsetX = Int(arc, Field("offsetX")),
                OffsetY = Int(arc, Field("offsetY")),
                Radius = Int(arc, Field("archR")),
                Width = Single(arc, Field("archWidth")),
                StartPercent = Int(arc, Field("startPer")),
                BackColor = Color(arc, Field("backArchColor")),
                FrontColor = Color(arc, Field("frontArchColor"))
            });
        }

        foreach (ClassRecord tip in List(root, Field("tipList")))
        {
            theme.Tips.Add(new NativeThemeTip
            {
                Content = String(tip, Field("content")),
                X = Int(tip, Field("posX")),
                Y = Int(tip, Field("posY")),
                Width = Int(tip, Field("W")),
                Height = Int(tip, Field("H")),
                Size = Single(tip, Field("size")),
                Color = Color(tip, Field("color"))
            });
        }

        return theme;
    }

    private static NativeThemeItem Item(ClassRecord record, bool inherited)
    {
        string[] Member(string name) => inherited ? [Field(name), Inherited(name)] : [Field(name)];
        ClassRecord? font = Class(record, Member("fontType"));
        return new NativeThemeItem
        {
            Enabled = Bool(record, Member("enabled")),
            Long = Bool(record, Member("isLongStr")),
            Name = String(record, Member("itemName")),
            Value = String(record, "valueStr", Field("valueStr")),
            Length = Int(record, Member("strLen")),
            X = Int(record, Member("posX")),
            Y = Int(record, Member("posY")),
            Interval = font is null ? 0 : Int(font, Field("interval")),
            Width = font is null ? 0 : Int(font, Field("W")),
            Height = font is null ? 0 : Int(font, Field("H")),
            OffsetX = font is null ? 0 : Int(font, Field("offsetX")),
            OffsetY = font is null ? 0 : Int(font, Field("offsetY")),
            FontSize = font is null ? 0 : Single(font, Field("size")),
            Color = font is null ? System.Drawing.Color.White : Color(font, Field("color"))
        };
    }

    private static IEnumerable<ClassRecord> List(ClassRecord owner, string member)
    {
        if (Class(owner, member) is not { } list)
            yield break;
        int size = Int(list, "_size");
        if (Raw(list, "_items") is not ArrayRecord array)
            yield break;
        Array values = GetArray(array);
        for (int index = 0; index < Math.Min(size, values.Length); index++)
        {
            if (values.GetValue(index) is ClassRecord item)
                yield return item;
        }
    }

    private static Array GetArray(ArrayRecord array)
    {
        string name = (array.TypeName?.FullName ?? "").Replace("[]", "", StringComparison.Ordinal);
        Type element = name switch
        {
            "System.Byte" => typeof(byte),
            "System.String" => typeof(string),
            _ => ElementType(name)
        };
        return array.GetArray(element.MakeArrayType(), allowNulls: true);
    }

    private static Type ElementType(string name)
    {
        lock (TypeGate)
        {
            if (ElementTypes.TryGetValue(name, out Type? type))
                return type;
            var typeBuilder = TypeModule.Value.DefineType(name, TypeAttributes.Public | TypeAttributes.Class);
            type = typeBuilder.CreateType()!;
            ElementTypes[name] = type;
            return type;
        }
    }

    private static System.Drawing.Bitmap? Bitmap(ClassRecord owner, string member)
    {
        if (Class(owner, member) is not { } bitmap
            || Raw(bitmap, "Data") is not SZArrayRecord<byte> data)
            return null;
        byte[] bytes = data.GetArray();
        if (bytes.Length == 0)
            return null;
        using var stream = new MemoryStream(bytes);
        using var image = new System.Drawing.Bitmap(stream);
        return image.Clone(new System.Drawing.Rectangle(0, 0, image.Width, image.Height),
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    }

    private static System.Drawing.Color Color(ClassRecord owner, string member)
    {
        if (Class(owner, member) is not { } color)
            return System.Drawing.Color.White;
        int value = Raw(color, "value") is { } raw
            ? unchecked((int)Convert.ToInt64(raw, CultureInfo.InvariantCulture)) : 0;
        int known = Int(color, "knownColor");
        if (value != 0)
            return System.Drawing.Color.FromArgb(value);
        return known > 0 && Enum.IsDefined(typeof(System.Drawing.KnownColor), known)
            ? System.Drawing.Color.FromKnownColor((System.Drawing.KnownColor)known)
            : System.Drawing.Color.White;
    }

    private static ClassRecord? Class(ClassRecord owner, params string[] members) =>
        Raw(owner, members) as ClassRecord;

    private static object? Raw(ClassRecord owner, params string[] members)
    {
        foreach (string member in members)
        {
            if (owner.HasMember(member))
                return owner.GetRawValue(member);
        }
        return null;
    }

    private static bool Bool(ClassRecord owner, params string[] members) =>
        Raw(owner, members) is { } value && Convert.ToBoolean(value, CultureInfo.InvariantCulture);

    private static int Int(ClassRecord owner, params string[] members) =>
        Raw(owner, members) is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;

    private static float Single(ClassRecord owner, params string[] members) =>
        Raw(owner, members) is { } value ? Convert.ToSingle(value, CultureInfo.InvariantCulture) : 0;

    private static string String(ClassRecord owner, params string[] members) =>
        Raw(owner, members) is { } value ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";

    private static string Field(string name) => $"<{name}>k__BackingField";

    private static string Inherited(string name) => $"TItem+<{name}>k__BackingField";
}
