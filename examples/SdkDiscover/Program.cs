using TURZX.WinUsbDisplay;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Device discovery requires Windows.");
    return 1;
}

// 使用 SDK 的接口 GUID 和自定义型号配置枚举设备，无需打开设备会话。
var devices = DisplayDevice.Discover(DisplaySettings.InterfaceGuid);
if (devices.Count == 0)
    Console.WriteLine("No matching Win207 devices found.");

foreach (var device in devices)
{
    var model = device.Model;
    Console.WriteLine($"{model.Name} ({DisplayModel.VendorId:X4}:{model.ProductId:X4}) {model.Width} x {model.Height}");
}

return 0;
