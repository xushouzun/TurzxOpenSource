using System.Buffers.Binary;
using System.Text;
using static TURZX.WinUsbDisplay.Studio.StudioLanguage;
namespace TURZX.WinUsbDisplay.Studio;

/// <summary>保存 Studio 中可选设备命令的定义。</summary>
/// <param name="Code">设备协议的命令字节。</param>
/// <param name="ProtocolName">协议参考中使用的命令名称。</param>
/// <param name="PurposeKey">用途说明的翻译键。</param>
/// <param name="RouteKey">发送路径说明的翻译键。</param>
/// <param name="Raw">为 true 时使用裸包通道。</param>
/// <param name="Query">指示命令是否用于查询。</param>
internal sealed record DeviceCommand(int Code, string ProtocolName, string PurposeKey, string RouteKey,
    bool Raw = false, bool Query = false)
{
    /// <summary>设备命令的用途说明。</summary>
    public string Purpose => T(PurposeKey);
    /// <summary>命令使用的协议发送路径。</summary>
    public string Route => T(RouteKey);
    /// <summary>显示用的命令编号标签。</summary>
    public string CodeLabel => Raw ? T("Raw", $"0x{Code:X2}") : $"{Code}";
    /// <summary>返回命令在选择器中的显示文本。</summary>
    public override string ToString() => $"{CodeLabel,-8}  {ProtocolName}  ·  {Purpose}";
}

/// <summary>提供设备命令目录、请求字段规则和应答格式化。</summary>
internal static class DeviceCommands
{
    /// <summary>供 Studio 命令选择器使用的预设列表。</summary>
    internal static IReadOnlyList<DeviceCommand> All { get; } =
    [
        new(10, "GET_FIRMWARE_VERSION", "FirmwareVersion", "ReadFromAdvancedOrCommands", Query: true),
        new(11, "RESTART_DEVICE", "RestartDevice", "AdvancedControls"),
        new(12, "SHUT_DOWN_DEVICE", "ShutDownDevice", "MaintenanceCommand"),
        new(13, "SET_DISPLAY_ORIENTATION", "DisplayOrientation", "HomeDisplayControls"),
        new(14, "SET_DISPLAY_BRIGHTNESS", "DisplayBrightness", "HomeDisplayControls"),
        new(15, "SET_VIDEO_FRAME_RATE", "PlaybackFrameRate", "MediaAndLiveInput"),
        new(17, "GET_H264_BLOCK_SIZE", "H264BlockSize", "ReadHere", Query: true),
        new(38, "CREATE_DEVICE_FILE", "CreateADeviceFile", "StorageMaintenanceCommand"),
        new(39, "WRITE_DEVICE_FILE_BLOCK", "WriteAFileBuffer", "StorageMaintenanceCommand"),
        new(40, "WRITE_DEVICE_FILE", "WriteADeviceFile", "StorageMaintenanceCommand"),
        new(42, "DELETE_DEVICE_PATH", "DeleteADeviceFile", "OnDeviceMedia"),
        new(49, "SET_VIDEO_CLOCK_SYNC", "VideoClockSynchronization", "FirmwareClockCommand"),
        new(50, "GET_SUPPLY_VOLTAGE", "ReadSupplyVoltage", "ReadHere", Query: true),
        new(51, "SET_DEVICE_CLOCK", "SetAndShowTheFirmwareClock", "RunHere"),
        new(52, "STOP_DEVICE_CLOCK", "HideTheFirmwareClock", "RunHere"),
        new(60, "GET_ORIENTATION_STATE", "ReadOrientationOnTheEncryptedChannel", "ReadHere", Query: true),
        new(61, "SET_PUMP_SPEED", "SetPumpSpeed", "CoolingCommand"),
        new(62, "GET_PUMP_SPEED", "ReadPumpSpeed", "CoolingCommand"),
        new(98, "GET_DEVICE_FILE_SIZE", "DeviceFileSize", "StorageQueryWithAPath"),
        new(99, "LIST_DEVICE_DIRECTORY", "DeviceDirectoryListing", "OnDeviceMedia"),
        new(100, "GET_STORAGE_CAPACITY", "DeviceStorageCapacity", "ReadHere", Query: true),
        new(101, "DISPLAY_JPEG", "SendJPEG", "MediaPage"),
        new(102, "DISPLAY_PNG_OVERLAY", "SendPNGOverlay", "MediaAndLiveInput"),
        new(110, "START_LOCAL_H264_PLAYBACK", "PlayOnDeviceH264", "OnDeviceMedia"),
        new(111, "STOP_LOCAL_H264_PLAYBACK", "StopOnDeviceVideo", "RunHere"),
        new(112, "GET_LOCAL_H264_PLAYBACK_STATE", "ReadOnDeviceVideoState", "ReadHere", Query: true),
        new(113, "DISPLAY_LOCAL_JPEG", "DisplayOnDeviceJPEG", "OnDeviceMedia"),
        new(114, "STOP_LOCAL_JPEG_LOOP", "StopOnDeviceImageLoop", "RunHere"),
        new(121, "WRITE_H264_STREAM_BLOCK", "SendH264Block", "VideoBackgroundAndVideoOutput"),
        new(122, "GET_H264_QUEUE_DEPTH", "VideoQueueDepth", "ReadHere", Query: true),
        new(123, "STOP_H264_STREAM", "StopStreamedVideo", "MediaAndThisPage"),
        new(125, "SET_DEVICE_CONFIGURATION", "WriteFirmwareConfiguration", "AdvancedControls"),
        new(150, "SWITCH_TO_DESKTOP_MODE", "SwitchToDeviceDesktopMode", "RunHere"),
        new(201, "GET_DEVICE_POSITION_INDEX", "ReadDevicePositionIndex", "ReadHere", Query: true),
        new(246, "TEST_USB_THROUGHPUT", "USBWriteThroughputTest", "FactoryCommand"),
        new(251, "SYNC_PUMP_AND_FAN_OUTPUTS", "SynchronizePumpAndFans", "CoolingCommand"),
        new(252, "WRITE_RGB_ANIMATION", "SendRGBAnimation", "LightingCommand"),
        new(253, "RUN_FACTORY_H264_TEST", "H264FactoryTest", "FactoryCommand"),
        new(0x11, "WRITE_LED_PIXEL_BLOCK", "SendLEDPixelBlock", "RawLEDCommand", Raw: true),
        new(0x60, "GET_COOLING_TEMPERATURES", "ReadPumpAndFanTemperatures", "ReadHere", Raw: true, Query: true),
        new(0x61, "SET_COOLING_SPEEDS", "SetPumpAndFanSpeeds", "RawCoolingCommand", Raw: true),
        new(0x62, "GET_COOLING_SPEEDS", "ReadPumpAndFanSpeeds", "ReadHere", Raw: true, Query: true)
    ];

    /// <summary>按命令和通道判断应填写的请求字段。</summary>
    internal static (bool Arguments, bool Payload) GetRequestFields(byte command, bool raw) =>
        raw ? (command is not (0x60 or 0x62), false) : command switch
        {
            10 or 11 or 12 or 17 or 50 or 52 or 60 or 100 or 111 or 112 or 114 or 123 or 150 or 201
                => (false, false),
            13 or 14 or 15 or 38 or 42 or 49 or 51 or 98 or 99 or 110 or 113 or 122 or 125 or 251
                => (true, false),
            _ => (true, true)
        };

    /// <summary>返回设备命令参数输入框的占位提示键。</summary>
    internal static string GetArgumentsPlaceholderKey(byte command, bool raw) => (raw, command) switch
    {
        (false, 13) => "OrientationArgumentsHint",
        (false, 14) => "BrightnessArgumentsHint",
        (false, 15) => "FrameRateArgumentsHint",
        (false, 38 or 42 or 98 or 99 or 110 or 113) => "DevicePathArgumentsHint",
        (false, 39) => "FileBufferArgumentsHint",
        (false, 40) => "DeviceFileArgumentsHint",
        (false, 49) => "VideoClockSyncArgumentsHint",
        (false, 51) => "DeviceClockArgumentsHint",
        (false, 101 or 102) => "ImageLengthArgumentsHint",
        (false, 121) => "H264BlockArgumentsHint",
        (false, 122) => "H264QueueArgumentsHint",
        (false, 125) => "StartupConfigurationArgumentsHint",
        (false, 251) => "PumpAndFanArgumentsHint",
        (false, 252) => "RgbAnimationArgumentsHint",
        (true, 0x11) => "LedPixelPayloadHint",
        (true, 0x61) => "CoolingSpeedArgumentsHint",
        (false, 61 or 62 or 246 or 253) => "UnknownArgumentsHint",
        _ => "HexArgumentsHint"
    };

    /// <summary>返回设备命令负载输入框的占位提示键。</summary>
    internal static string GetPayloadPlaceholderKey(byte command, bool raw) => (raw, command) switch
    {
        (false, 39) => "FileBufferPayloadHint",
        (false, 40) => "DeviceFilePayloadHint",
        (false, 101) => "JpegPayloadHint",
        (false, 102) => "PngPayloadHint",
        (false, 121) => "H264PayloadHint",
        (false, 252) => "RgbAnimationPayloadHint",
        (false, 61 or 62 or 246 or 253) => "UnknownPayloadHint",
        _ => "HexPayloadHint"
    };

    /// <summary>判断是否为命令提供 SDK 快捷操作。</summary>
    internal static bool CanExecute(DeviceCommand? command) =>
        command is { Raw: false, Code: 51 or 52 or 111 or 114 or 123 or 150 };

    /// <summary>通过 SDK 执行所选预设命令。</summary>
    internal static string Run(DisplaySession session, DeviceCommand command)
    {
        switch (command.Code)
        {
            case 51: session.ShowDefaultClock(true); break;
            case 52: session.ShowDefaultClock(false); break;
            case 111: session.StopLocalVideo(); break;
            case 114: session.StopImageLoop(); break;
            case 123: session.StopVideo(); break;
            case 150: session.SwitchToDesktop(); break;
            default: throw UiError(new InvalidOperationException("Use the corresponding feature page to send this command."), "ErrorUseCommandFeaturePage");
        }
        return T("DeviceResponded");
    }

    /// <summary>将已知查询的原始响应转换为状态文本。</summary>
    internal static string? DescribeQueryReply(byte command, bool raw, byte[] data)
    {
        return (raw, command) switch
        {
            (false, 10) when data.Length >= 40 => Encoding.UTF8.GetString(data.AsSpan(8, 32)).TrimEnd('\0'),
            (false, 50) when data.Length >= 12 => T("RawSupplyVoltage", BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8))),
            (false, 98) when data.Length >= 12 => T("DeviceFileSizeBytes", BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8))),
            (false, 100) when data.Length >= 32 => DescribeStorage(new StorageCapacity(ReadVolume(8), ReadVolume(20))),
            (true, 0x60) when data.Length >= 3 => T("PumpCFanC", data[1], data[2]),
            (true, 0x62) when data.Length >= 5 => T("PumpFanRaw", (data[1] << 8) | data[2], (data[3] << 8) | data[4]),
            (false, 17) when data.Length >= 12 => T("H264BlockSizeDetails", BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8))),
            (false, 112) when data.Length >= 9 => data[8] == 0 ? T("OnDeviceVideoStopped") : T("OnDeviceVideoPlaying"),
            (false, 60) when data.Length >= 10 => T("OrientationSensor", data[8], data[9]),
            (false, 122) when data.Length >= 9 => T("QueueDepthBlocks", data[8]),
            _ => null
        };

        // 从查询应答的指定位置读取存储卷容量。
        StorageVolume ReadVolume(int offset) => new(
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8)));
    }

    /// <summary>格式化设备返回的 SD 卡和闪存容量。</summary>
    internal static string DescribeStorage(StorageCapacity capacity)
    {
        // 为命令组合用途和字段说明。
        static string Describe(StorageVolume volume) =>
            T("StorageCapacityDetails", volume.TotalKiB, volume.UsedKiB, volume.FreeKiB);

        return $"SD {Describe(capacity.SdCard)}\nFlash {Describe(capacity.Flash)}";
    }
}
