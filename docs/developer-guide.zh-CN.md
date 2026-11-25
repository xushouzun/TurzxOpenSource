# TURZX WinUsbDisplay DLL 开发指南

`TURZX.WinUsbDisplay.dll` 是 Windows 上的 Win207 设备控制类库，目标框架为 `net10.0`。它使用 Windows 自带的 SetupAPI 和 WinUSB，无 NuGet 包依赖。设备需要安装相应的 WinUSB 驱动。

## 1. 构建和引用

从仓库对应版本的 Releases 下载 `TURZX-WinUsbDisplay-SDK.zip`。解压后在包根目录运行 `dotnet run --project examples/SdkDiscover` 可枚举设备。包内 `lib` 目录包含设备 DLL、可选媒体 DLL 及各自的 XML API 文档。也可以从源码构建。

只构建设备控制 DLL：

```powershell
dotnet build src/TURZX.WinUsbDisplay/TURZX.WinUsbDisplay.csproj -c Release
```

将生成的 `TURZX.WinUsbDisplay.dll` 复制到消费项目的 `lib` 目录。可把同目录的 `TURZX.WinUsbDisplay.xml` 一并复制，用于 IntelliSense。消费项目只需要引用这个 DLL 即可枚举设备、读取固件和设置屏幕。桌面端、CLI、FFmpeg 和浏览器均为可选组件。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="TURZX.WinUsbDisplay">
      <HintPath>lib/TURZX.WinUsbDisplay.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
```

源码开发可以使用 `ProjectReference` 指向 `src/TURZX.WinUsbDisplay/TURZX.WinUsbDisplay.csproj`。Studio 和 CLI 都引用设备 SDK；需要媒体处理时再引用媒体库。媒体库编译为 `TURZX.WinUsbDisplay.Media.dll`，公开类型位于 `TURZX.WinUsbDisplay.Media` 命名空间。使用媒体库时同时引用两份 DLL。

## 2. 第一次连接

命令密钥已内置，`DisplaySettings.ReadCommandKey()` 返回其副本；`device.Open` 也接受自定义的 8 字节密钥。

```csharp
using System.Security.Cryptography;
using TURZX.WinUsbDisplay;

var devices = DisplayDevice.Discover(DisplaySettings.InterfaceGuid);
for (int index = 0; index < devices.Count; index++)
{
    var model = devices[index].Model;
    Console.WriteLine($"{index}: {model.Name} {model.Width}x{model.Height}");
}
if (args.Length != 1 || !int.TryParse(args[0], out int target) || target < 0 || target >= devices.Count)
    return;

byte[] key = DisplaySettings.ReadCommandKey();
try
{
    using DisplaySession screen = devices[target].Open(key);
    Console.WriteLine(screen.ReadFirmwareVersion());
    screen.SetBrightness(60);
    screen.SetRotation(0);
    // 使用完整的 8 位 RGBA PNG，且文件不超过 950,000 字节。
    screen.ShowImage(File.ReadAllBytes("artwork.png"));
}
finally
{
    CryptographicOperations.ZeroMemory(key);
}
```

建议在应用里以 `DisplayDevice.Path` 保留当前连接目标，列表索引只用于本次枚举。若重新枚举后找不到该路径，要求重新选设备。驱动注册了自定义接口 GUID 时，可以将其直接传入 `Discover`，或在 Studio 设置文件的 `InterfaceGuid` 字段中配置。

## 3. API 和输入

| API | 用途及输入 |
| --- | --- |
| `DisplayDevice.Discover(guid)` | 枚举已知型号，返回接口路径和原生宽高。找不到设备时返回空列表 |
| `device.Open(key)` | 打开会话。参数为 8 字节。会话持有自己的参数副本，释放时清零 |
| `ReadFirmwareVersion()` | 读取固件版本字符串 |
| `SetBrightness(percent)` | 0–100。12.3 寸自动应用其亮度上限 |
| `SetRotation(quarterTurns)` | 0–3 对应 0°、90°、180°、270°。应在停止当前视频后调整 |
| `ShowImage(bytes)` | 传入不超过 950,000 字节的完整 JPEG 或 8 位 RGBA PNG。发送前停止流式视频和时钟层 |
| `ShowOverlay(png)` | 传入不超过 950,000 字节的完整 8 位 RGBA PNG，保留正在传输的 H.264 背景。透明区域由固件合成 |
| `PlayVideo(stream, fps, loop, cancellation, onStarted)` | 可读的 Annex B H.264 流。循环播放要求流可定位。调用会阻塞，适合在后台线程执行。可选回调在首块应答后触发。在读取期间取消后，请用新流重试，因为原流的读取可能尚未结束。 |
| `SetFrameRate(fps)` | 单独设置 1–60 帧/秒。通过 `PlayVideo` 传视频时方法会设置帧率 |
| `ReadDeviceDirectory(directory, cancellation)` | 返回文件名列表和 `IsComplete`（目录是否完整读取）。未读完时仍可能返回部分条目。设备创建目录失败时抛出 `DeviceResponseException`，其 `Failure` 为 `DirectoryCreationFailed` |
| `PlayDeviceFile(path)` / `DeleteDeviceFile(path)` | 播放或删除设备存储中的文件。使用以 `/` 开头的 ASCII 路径，最长 484 字节。删除回包未定义统一成功码，需重新查询目录确认结果 |
| `ReadStorageCapacity()` | 返回 SD 卡与内部 Flash 的总量、已用和可用容量（KiB） |
| `ReadSupplyVoltageMillivolts()` | 读取兼容设备的供电电压，单位为毫伏 |
| `StopPlayback()` | 停止主机推流、设备内 H.264 和设备内 JPEG 循环。先取消并等待正在运行的传输 |
| `StopVideo()` | 发送流式视频停止指令。已有持续发送时，先取消并等待该任务 |
| `StopLocalVideo()`、`StopImageLoop()` | 停止设备存储中的视频或图片循环 |
| `ShowDefaultClock(enabled)` | 从主机本地时间校时并显示时钟，或停止时钟 |
| `SwitchToDesktop()` | 设置下次启动进入 Windows 桌面模式，调用方另行重启（11）后生效 |
| `ExchangeCommand(command, arguments, payload)` | 通用加密收发。参数从明文头偏移 8 开始，最多 492 字节。载荷从 USB 偏移 512 开始，长度和标志由调用者按协议填写。返回设备应答字节 |
| `ExchangeRawCommand(command, arguments)` | 通用非加密收发。编号在偏移 0，参数从偏移 1 开始，最多 63 字节。返回设备应答字节 |
| `ReadDiagnostic(command, raw)` | 读取未解析的诊断应答。`raw=false` 使用加密通道，仅接受命令 10、17、50、60、100、112、122、201；`raw=true` 使用非加密通道，仅接受 0x60 和 0x62 |
| `PlayImage(image, fps, cancellation, onStarted, onFrameSent, onTransfer)` | 以 1–60 帧/秒循环发送同一张图片，直到取消。回调可报告启动、每帧发送和传输耗时 |
| `ShowJpegFrame(jpeg, onTransfer)` | 发送单帧 JPEG，适用于没有视频背景的连续输出。可记录 USB 写入和应答耗时 |
| `SetStartupConfiguration(brightness, startupMode, rotation, keepAwakeOffline, offlineClockRotation)` | 写入持久化启动配置。亮度 0–100，启动模式 0–2，方向 0–3，离线时钟方向 0–3 |
| `RestartDevice()` | 发送命令 11，重启显示设备 |
| `ListDeviceFiles(directory)` | 返回设备目录中的文件名，不包含完整性状态 |
| `SynchronizeClock()` | 用主机时间同步设备时钟 |
| `SetVideoClockSync(enabled)` | 设置设备是否等待视频同步时序 |
| `ReadVerifiedQueryResponse(command)` | 查询并校验固件（10）、电压（50）或存储（100）的应答 |
| `ExchangeMatchingCommand(command, arguments, payload, minimumLength, cancellation)` | 发送命令并读取与编号匹配、达到最小长度的应答 |

桌面模式设备可通过 `DesktopModeDevice.Discover()` 枚举。按 `Serial` 选择设备后调用 `SwitchToMonitorAsync()` 发送一次 HID 回切报告，再用 `DisplayDevice.Discover(DisplaySettings.InterfaceGuid)` 核对同序列号的 WinUSB 接口是否重新出现。Studio 中的操作步骤见协议文档的「Windows 桌面模式与副屏模式」。

设备库直接使用编码后的图片或视频。MP4 等容器需先转成 Annex B H.264，HTML 和 Windows 窗口需先渲染成 PNG 或 JPEG。可自行生成这些字节，也可参考 `TURZX.WinUsbDisplay.Media` 中的 `VideoConverter`、`HtmlCapture`、`LiveScene` 和 `FrameInput`。

### HTML 捕捉的输出旋转

`HtmlCapture.OpenAsync(source, model, cancellation, fastCapture, pixelScale, outputRotation)` 在浏览器合成阶段旋转捕捉输出。`model` 仍定义页面脚本看到的逻辑视口；例如逻辑尺寸为 1280 × 800、`pixelScale` 为 1、`outputRotation` 为 3 时，输出为 800 × 1280。

| `outputRotation` | 输出方向 |
| --- | --- |
| `0` | 不旋转 |
| `1` | 逆时针 90° |
| `2` | 180° |
| `3` | 顺时针 90° |

`outputRotation` 为 1 或 3 时，输出宽高互换；`pixelScale` 继续决定像素密度。非零旋转只支持本地 HTML：页面在保留逻辑尺寸的 `iframe` 中加载，CSS、脚本和图片等相对资源仍相对于原 HTML 地址解析。页面需能在 `iframe` 中运行。远程 HTTP(S) URL 的非零输出旋转抛出 `NotSupportedException`；`outputRotation=0` 的普通远程捕捉不受影响。此参数不向设备发送方向命令。

旋转同样适用于 PNG 和 JPEG：`ReadFrameAsync`、`ReadPngScreencastAsync` 返回 PNG，`ReadJpegFrameAsync`、`ReadJpegScreencastAsync` 返回 JPEG。`ReadFrameAsync` 和 `ReadJpegFrameAsync` 按需截图，两个 Screencast 方法连续推帧。

## 4. 会话和错误处理

- 同一台屏幕同时只交给一个进程控制。连接后用 `using` 释放会话。
- USB 调用是同步的。GUI 将设备读写放在工作线程。实时前景可以在同一会话中与 `PlayVideo` 交替发送，各请求与应答由库串行配对。
- 停止循环视频时先取消 `CancellationToken` 并等待传输返回，再释放会话或切换内容。取消期间已开始的 USB 请求会等应答或端点超时。
- 打开设备时，可通过 `DisplayOpenException.Failure` 区分共享冲突（`InUse`）和访问拒绝（`AccessDenied`）。取消视频传输后，如果设备端停播命令失败，SDK 会抛出 `PlaybackStopException`。
- WinUSB 错误返回 `Win32Exception`，包含操作和 Windows 错误码。短写、空应答或无效应答可能返回 `IOException`。设备断开时释放旧会话并重新枚举，避免盲目重放已经送达的块。
- 解析诊断应答前先检查应答长度。型号或固件不支持某项指令时，可能超时或返回不同布局。

指令编号、参数偏移、字节序和封包示例见 [Win207 设备指令技术说明](win207-protocol.zh-CN.md)。
