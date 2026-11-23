using System.Text.RegularExpressions;

namespace TURZX.WinUsbDisplay;

/// <summary>描述 Win207 设备型号及原生像素尺寸。</summary>
/// <param name="Name">设备选择器中显示的型号名称。</param>
/// <param name="ProductId">用于匹配枚举结果的 USB 产品 ID。</param>
/// <param name="Width">原生像素宽度。</param>
/// <param name="Height">原生像素高度。</param>
public sealed record DisplayModel(string Name, ushort ProductId, int Width, int Height)
{
    /// <summary>按所选屏幕方向获取逻辑画布尺寸。</summary>
    public DisplayModel ForRotation(int quarterTurns) =>
        (quarterTurns & 1) == 0 ? this : this with { Width = Height, Height = Width };

    /// <summary>本库支持的 Win207 型号所使用的 USB 厂商 ID。</summary>
    public const ushort VendorId = 0x1cbe;

    /// <summary>获取内置的受支持型号列表。</summary>
    public static IReadOnlyList<DisplayModel> Known { get; } = Array.AsReadOnly<DisplayModel>(
    [
        new("4.6 inch", 0x0046, 320, 960),
        new("8.8 inch", 0x0088, 480, 1920),
        new("9.2 inch", 0x0092, 464, 1920),
        new("9.2 inch A092", 0xa092, 464, 1920),
        new("2.1 inch", 0x0021, 480, 480),
        new("2.1 inch SLC", 0xb021, 480, 480),
        new("12.3 inch", 0x0123, 720, 1920),
        new("7 inch", 0x0070, 800, 1280),
        new("8 inch", 0x0080, 800, 1280),
        new("5.2 inch", 0x0050, 720, 1280),
        new("5 inch", 0x0055, 720, 1280),
        new("4 inch", 0x0040, 720, 720),
        new("4.5 inch", 0x0045, 1024, 768),
        new("6.2 inch", 0xa062, 368, 960),
        new("6.2 inch V2", 0xb062, 448, 1280),
        // C062 的原厂主题画布为 448 × 1280，但送屏前会扩展到 480 × 1280。
        new("6.2 inch V3", 0xc062, 480, 1280),
        new("6.5 inch", 0xa065, 720, 1472),
        new("6.5 inch 1568", 0xb065, 720, 1568),
        new("6.8 inch", 0x0068, 1080, 2320),
        new("6.8 inch 2288", 0xa068, 1080, 2288),
        new("1.6 inch", 0x0005, 400, 400),
        new("1.8 inch", 0xa018, 400, 400),
        new("2.8 inch square", 0x0016, 400, 400),
        new("2.8 inch round", 0x0028, 480, 480),
        new("2.88 inch round", 0x0288, 480, 480),
        new("2.1 inch A021", 0xa021, 480, 480),
        new("3.4 inch round", 0x0034, 480, 480),
        new("3.4 inch SLC", 0xb034, 480, 480),
        new("3.5 inch", 0x0035, 640, 480),
        new("3.92 inch", 0x0392, 1080, 1240),
        new("10 inch", 0x0109, 1200, 1920),
        new("11.3 inch", 0x0113, 440, 1920),
        new("12.4 inch", 0x0124, 2560, 666),
        new("12.68 inch", 0x0126, 1920, 140),
        new("14.5 inch", 0x0145, 2560, 720)
    ]);
}

/// <summary>标识已枚举的 WinUSB 接口及其设备型号。</summary>
/// <param name="Path">用于打开设备的 WinUSB 接口路径。</param>
/// <param name="Model">与接口 USB 产品 ID 匹配的型号。</param>
public sealed record DisplayDevice(string Path, DisplayModel Model)
{
    /// <summary>按给定的驱动接口 GUID 枚举匹配的 Win207 设备。</summary>
    public static IReadOnlyList<DisplayDevice> Discover(Guid interfaceGuid)
    {
        var models = DisplayModel.Known.ToDictionary(model => model.ProductId);

        var devices = new List<DisplayDevice>();
        foreach (string path in WinUsbTransport.Enumerate(interfaceGuid))
        {
            var match = Regex.Match(path, @"#vid_1cbe&pid_([0-9a-f]{4})(?:&|#)", RegexOptions.IgnoreCase);
            if (match.Success && models.TryGetValue(Convert.ToUInt16(match.Groups[1].Value, 16), out var model))
                devices.Add(new DisplayDevice(path, model));
        }

        return devices.OrderBy(device => device.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>获取设备枚举使用的默认 WinUSB 接口类 GUID。</summary>
    public static Guid WinUsbDeviceInterface { get; } = new("dee824ef-729b-4a0e-9c14-b7117d33a817");

    /// <summary>使用固件的八字节命令密钥打开独立会话。</summary>
    public DisplaySession Open(ReadOnlySpan<byte> commandKey) => new(Path, commandKey, Model.ProductId);
}
