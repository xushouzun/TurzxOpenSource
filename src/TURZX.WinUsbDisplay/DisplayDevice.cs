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
        new("2.1 inch", 0x0021, 480, 480),
        new("2.1 inch SLC", 0xb021, 480, 480),
        new("12.3 inch", 0x0123, 720, 1920),
        new("7 inch", 0x0070, 800, 1280),
        new("8 inch", 0x0080, 800, 1280),
        new("5.2 inch", 0x0050, 720, 1280),
        new("5 inch", 0x0055, 720, 1280)
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
