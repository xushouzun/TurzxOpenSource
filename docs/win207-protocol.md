# TURZX Win207 设备指令技术说明

本文记录本项目实现的 Win207 主机端协议。指令是否可用取决于型号和固件版本。显示名称采用英文大写蛇形命名。读取用 `GET_`，配置用 `SET_`，传输用 `WRITE_`。设备按指令编号和通道识别请求。

偏移从 0 开始，范围含首尾。`u8` 是 1 字节无符号整数，`u16be`、`u32be` 是 2、4 字节大端无符号整数，`u32le` 是 4 字节小端无符号整数。「头」指加密前的 500 字节明文头，「应答」指从 IN 端点读取的实际字节。加密通道编号以十进制表示，裸通道编号以十六进制表示。

## 连接和调用

- Windows 通过设备驱动注册的接口 GUID 枚举。已验证的默认 GUID 为 `DEE824EF-729B-4A0E-9C14-B7117D33A817`，可用 `WINUSB_DISPLAY_INTERFACE_GUID` 覆盖。
- WinUSB OUT 端点为 `0x01`，IN 端点为 `0x81`。每次先发送一条请求，再读取它的应答，避免不同指令错配。Studio 的 OUT 写超时是 10000 毫秒、IN 读超时是 5000 毫秒（实时 PNG 等大块载荷在固件合成背景时会短暂停顿，超时过短会中断传输、卡住端点或丢弃慢回包；正常控制回包很快，超时只是上限），读取缓冲区为 512 字节。
- 需要从固件维护者获得该固件的 8 字节封包参数。在 Windows 启动 Studio 或 CLI 前，将 `WINUSB_DISPLAY_COMMAND_KEY` 设为对应的 16 位十六进制字符串。SDK 也可直接接收 8 字节参数。
- 应答解析前检查实际长度。Studio 对固件、存储和供电的结构化查询同时检查应答 0 是否回显请求编号，避免解析其他指令留下的应答。收到 USB 应答与设备成功执行是两个状态，某些指令另有应答字段。

## 加密通道封包

| 位置 | 内容 |
| --- | --- |
| 明文 0 | 指令编号 |
| 明文 1 | 保留为 0 |
| 明文 2–3 | 固定标识 `1A 6D` |
| 明文 4–7 | 当前 UTC 时间与会话创建时的本地昨天零点之间的毫秒差低 32 位，小端。沿用原 Win207 主机的时间基准 |
| 明文 8–499 | 指令参数，空余部分填 0 |
| USB 请求 0–503 | 500 字节明文以 DES-CBC 加密，PKCS#7 填充。IV 和密钥均为固件参数 |
| USB 请求 504–509 | 六个 0 字节 |
| USB 请求 510–511 | 固定尾标 `A1 1A` |
| USB 请求 512 起 | 可选原始载荷。媒体载荷不参与头部加密 |

DES-CBC 是现有固件的兼容格式。设备应答保持明文。命令 101、102、121 的明文 8–11 存载荷字节数（大端）。单字节控制参数一般放在明文 8。

## 通用收发接口

指令 101（JPEG）、102（PNG）和 40（小文件写入）支持浏览本地文件。Studio 自动填写字节长度，40 还需输入设备上的绝对 ASCII 目标路径，文件须小于 100 KiB。选中文件后仍需手动发送。指令 121 的 **浏览 H.264** 会在媒体页选择 `.h264` 或 `.264` 作为背景视频。整段播放由播放器负责分块和缓冲队列控制。只播视频时清空前景。已存入设备的文件可在 **高级控制 → 设备内媒体** 中选择。

收发面板打开设备会话时需要封包参数，裸通道也使用同一会话。发送前请停止所选设备的 Studio 播放。通用收发会为加密查询 10、50 和 100 检查命令回显和最短应答长度（分别为 40、12 和 32 字节），并在不匹配时重试。其余请求保留实际回包，具体状态按指令定义判读。

在 Studio 的 **设备指令 → 指令收发** 中选择通道并输入指令号。编号可用十进制或 `0x` 前缀十六进制表示。加密通道和裸通道的编号彼此独立，例如加密十进制 `60` 查询方向，裸十六进制 `0x60` 查询温度。

| 输入或返回 | 加密 Win207 | 裸通道 |
| --- | --- | --- |
| 指令编号 | 0–255，写入明文头偏移 0 | 0–255，写入请求偏移 0 |
| 参数 | 0–492 字节，从明文头偏移 8 写入，余下填零 | 0–63 字节，从偏移 1 写入，余下填零 |
| 载荷 | 可选，从 USB 偏移 512 追加。按指令表填写长度、标志等头部参数，通用接口不推断字段 | 无独立载荷，请求固定 64 字节 |
| 返回 | IN 端点实际返回的全部字节，保留零字节，不填充到 512 字节 | 同左 |
| 无回包或传输失败 | 空回包、短写以 I/O 错误报告。驱动错误包含操作名和 Windows 错误码 | 同左 |

预览的左列是十六进制字节偏移，中间是字节值，右列是可打印 ASCII，其余字符显示为点。收到 USB 数据只说明设备有应答。应答 0 可能回显指令编号，位置和成功码仍以各指令为准。例如 110 的原实现检查应答 8 为 200，113 检查应答 1 为 200。未定义成功码的指令保留原始字节供检查。

| 示例 | 通道与编号 | 参数 | 读回值 |
| --- | --- | --- | --- |
| 查询固件 | 加密 10 | 留空 | 检查应答长度至少 40，偏移 0 为 10，8–39 为 UTF-8 版本文本 |
| 查询 H.264 块大小 | 加密 17 | 留空 | 应答至少 12 字节，8–11 为大端块长，0 表示默认值 |
| 设置亮度为设备值 60 | 加密 14 | `3C` | 原主机未定义统一成功码，回包保留供检查。需应用型号亮度上限时使用 SDK `SetBrightness` |
| 查询散热温度 | 裸 0x60 | 留空 | 应答至少 3 字节，1、2 为泵和风扇温度，单位 °C |

```csharp
byte[] firmware = screen.ExchangeCommand(10);
byte[] temperatures = screen.ExchangeRawCommand(0x60);
Console.WriteLine(Convert.ToHexString(firmware));
Console.WriteLine(Convert.ToHexString(temperatures));
```

## 基础控制和查询

| 编号 | 名称 | 参数和应答 | Studio 入口 |
| --- | --- | --- | --- |
| 10 | GET_FIRMWARE_VERSION | 应答 0 应为 10，8–39 为 UTF-8 固件版本，去掉尾部 NUL | 高级控制 · 设备指令 |
| 11 | RESTART_DEVICE | 头参数全零，请求 512 字节。重启会中断会话，原主机未校验专用成功码 | 高级控制 |
| 12 | SHUT_DOWN_DEVICE | 头参数全零，请求 512 字节。关闭设备，原主机未校验专用成功码 | 总览查阅 |
| 13 | SET_DISPLAY_ORIENTATION | 头 8 为 u8，0、1、2、3 对应 0°、90°、180°、270°。其余参数填 0。Studio 会按设备型号结合主机端帧旋转与固件方向。设置结果以实体面板为准，部分固件的 60 回包无法验证方向 | 媒体 · 方向 |
| 14 | SET_DISPLAY_BRIGHTNESS | 头 8 为 u8，范围 0–100。12.3 寸发送 floor(百分比 × 80 ÷ 100)。原主机未定义统一成功码 | 首页 · 屏幕快控 |
| 15 | SET_VIDEO_FRAME_RATE | 头 8 为 u8，单位帧/秒，Studio 接受 1–60。在写入首个 H.264 块之前设置 | 媒体、实时输入 |
| 49 | SET_VIDEO_CLOCK_SYNC | 头 8 为 u8，0 禁用视频时钟同步，1 启用。日期时间使用 51 设置 | 总览查阅 |
| 50 | GET_SUPPLY_VOLTAGE | 无请求参数。应答至少 12 字节，8–11 为 u32be 供电读数。原设备详情页在指定 12.3 寸型号以 mV 显示 | 高级控制 · 设备指令 |
| 51 | SET_DEVICE_CLOCK | 明文 8–15 依次为年份高、低字节，月、日、时、分、秒及模式。原主机模式 0/1 为禁用/启用，2 为仅校时。原主机检查应答 0 为 51。SDK 使用模式 1 显示时钟，模式 2 仅同步主机本地时间，并保留原主机的字段布局。 | 高级控制 · 设备指令 |
| 52 | STOP_DEVICE_CLOCK | 头 8 为 0，停止设备时钟层。原主机未定义统一成功码 | 高级控制 · 设备指令 |
| 60 | GET_ORIENTATION_STATE | 无请求参数。应答至少 10 字节，8 为当前方向，9 为传感器方向，两者各为 u8。裸通道 0x60 用于查询温度。注意：在 turzx_0001_0020 固件上实测，无论 13 下发何值，应答 8、9 始终为 0，因此该回包不能用来确认手动方向是否生效 | 设备指令 |
| 61 | SET_PUMP_SPEED | 原枚举中的加密指令定义，主机的泵和风扇设置走裸通道 | 总览查阅 |
| 62 | GET_PUMP_SPEED | 原枚举中的加密指令定义，主机的转速查询走裸通道 | 总览查阅 |
| 125 | SET_DEVICE_CONFIGURATION | 原主机头 8–14 依次写入 u8 亮度、启动模式、0、方向、休眠延迟、离线模式、离线时钟方向。模式与延迟单位由固件定义 | 高级控制 |
| 150 | SWITCH_TO_DESKTOP_MODE | 头参数全零，切换到设备桌面模式。依赖型号支持，原主机未检查专用成功码 | 设备指令 |
| 201 | GET_DEVICE_POSITION_INDEX | 头参数全零。SLC 的原主机将应答 10–11 作为 u16be 泵读数 | 设备指令 |

## 图片、视频和图层

| 编号 | 名称 | 参数和应答 | Studio 入口 |
| --- | --- | --- | --- |
| 17 | GET_H264_BLOCK_SIZE | 无请求参数。应答至少 12 字节，8–11 为 u32be 字节数。0 使用 198 KiB，Studio 拒绝大于 4 MiB 的块大小 | 设备指令 |
| 101 | DISPLAY_JPEG | 头 8–11 为 u32be 字节数，USB 偏移 512 起为完整 JPEG。应答状态码未统一定义 | 媒体 |
| 102 | DISPLAY_PNG_OVERLAY | 头 8–11 为 u32be 字节数，USB 偏移 512 起为完整 PNG。Alpha 由固件合成，实时 PNG 可与 121 交替发送 | 媒体、实时输入 |
| 110 | START_LOCAL_H264_PLAYBACK | 明文 8–11 为大端路径长度，路径 ASCII 从 16 起。原主机检查应答 8 为 200 | 高级控制 · 设备内媒体 |
| 111 | STOP_LOCAL_H264_PLAYBACK | 头参数全零，停止设备存储中的 H.264 播放。主机推流用 123 停止 | 设备指令 |
| 112 | GET_LOCAL_H264_PLAYBACK_STATE | 无请求参数。应答至少 9 字节，8 为 u8，0 表示停止，非零表示播放中 | 设备指令 |
| 113 | DISPLAY_LOCAL_JPEG | 路径参数同 110。原主机检查应答 1 为 200 | 高级控制 · 设备内媒体 |
| 114 | STOP_LOCAL_JPEG_LOOP | 头参数全零，停止设备内 JPEG 循环。固件未提供可移植的图层清除应答 | 设备指令 |
| 121 | WRITE_H264_STREAM_BLOCK | 头 8–11 为块长 u32be，12 在末块为 1，其余块为 0。USB 偏移 512 起是 Annex B 数据。应答至少 9 字节，8 为队列深度 u8 | 媒体、实时输入 |
| 122 | GET_H264_QUEUE_DEPTH | 常规查询头参数全零。应答至少 9 字节，8 为队列深度 u8。同步扩展见下文 | 设备指令 |
| 123 | STOP_H264_STREAM | 头参数全零。主机停止发送后下发，避免后续数据块重启视频 | 媒体、设备指令 |

视频流程为停止旧流、设置帧率、读取块大小、等待队列深度降至 1 或更低，再逐块发送 Annex B 码流。文件传输的块间隔约 30 毫秒，实时编码流按块大小和缓冲时间发送。队列深度大于 3 时通常等待降至 2 或更低。单次发送完成后设备继续消费剩余队列。实时图层可在完整请求与应答之间插入 102，背景仍由 121 持续传输。原主机另有 122 同步扩展，头 8–15 是大端 64 位同步时间（毫秒），16–19 是大端 32 位总帧数。Studio 使用普通查询。

## 文件、工厂测试和灯效

| 编号 | 名称 | 参数或已知用途 | Studio 入口 |
| --- | --- | --- | --- |
| 38 | CREATE_DEVICE_FILE | 路径布局同 110，原主机检查应答 8 为 200 | 总览查阅 |
| 39 | WRITE_DEVICE_FILE_BLOCK | 头 8–11 为缓冲容量 u32be，12–15 为本块有效长度 u32be，16 为末块标志 u8。原主机每次发送完整 1 MiB 缓冲，末块按有效长度解释 | 总览查阅 |
| 40 | WRITE_DEVICE_FILE | 头 8–11 为路径长度 u32be，12–15 为文件长度 u32be，ASCII 路径从 16 起。偏移 512 起是文件。原主机用于小于 100 KiB 的文件 | 总览查阅 |
| 42 | DELETE_DEVICE_PATH | 删除文件或目录。头 8–11 为路径字节数 u32be，16 起为 ASCII 路径。原主机未解析成功码 | 高级控制 · 设备内媒体 |
| 98 | GET_DEVICE_FILE_SIZE | 头 8–11 为路径字节数 u32be，ASCII 路径从 16 起。应答至少 12 字节，8–11 为 u32le。原主机按字节使用该值 | 总览查阅 |
| 99 | LIST_DEVICE_DIRECTORY | 路径布局同 98。SDK 最多请求 20 次并拼接 UTF-8 回包，解析 `file:` 后以 `/` 分隔的文件名。`nodir-createdone` 表示创建了空目录，`result:nodir-createfailed` 表示设备创建目录失败 | 高级控制 · 设备内媒体 |
| 100 | GET_STORAGE_CAPACITY | 应答 8、12、16 为 SD 总量、已用、可用。20、24、28 为 Flash 对应字段。字段为小端 32 位，单位 KiB | 高级控制 · 设备指令 |
| 246 | TEST_USB_THROUGHPUT | 原测速函数发送零填充缓冲区 | 总览查阅 |
| 251 | SYNC_PUMP_AND_FAN_OUTPUTS | 原主机明文 8 起写 16 字节参数，参数内 0–2 为 FF 0F A1，4–5 为大端泵值，6–8 为三路风扇值 | 总览查阅 |
| 252 | WRITE_RGB_ANIMATION | 头 12–15 为载荷长度 u32be。USB 偏移 512–513 为总帧数 u16be，514 为帧间隔 u8，515 起为动画数据。间隔单位和帧编码由固件定义 | 总览查阅 |
| 253 | RUN_FACTORY_H264_TEST | 原枚举保留的工厂测试指令 | 总览查阅 |

## 裸通道

裸通道使用同一端点，但请求是 64 字节缓冲，不带 DES 封包。编号放在请求 0。

| 编号 | 名称 | 请求和应答 | Studio 入口 |
| --- | --- | --- | --- |
| 0x11 | WRITE_LED_PIXEL_BLOCK | 请求 1 为 LED 起始索引 u8，RGB 数据从 4 起。原主机依次发索引 0、15、30，数据最多 45、45、15 字节，空余填 0 | 总览查阅 |
| 0x60 | GET_COOLING_TEMPERATURES | 请求 1–63 填 0。应答至少 3 字节，1、2 是泵和风扇温度 u8，单位 °C | 设备指令 |
| 0x61 | SET_COOLING_SPEEDS | 请求 1–2、3–4 为泵和风扇目标 u16be，5–63 填 0。原主机默认原始满量程为 3500 | 总览查阅 |
| 0x62 | GET_COOLING_SPEEDS | 请求 1–63 填 0。应答至少 5 字节，1–2、3–4 为泵和风扇转速 u16be。数值换算以型号为准 | 设备指令 |

## 封包和应答示例

以下为加密前头部，时间戳固定为 1 毫秒。每个加密头补零至 500 字节，再按前文处理。

| 操作 | 明文头起始字节 | 解释 |
| --- | --- | --- |
| 读取版本 | `0A 00 1A 6D 01 00 00 00` | 十进制 10 对应 0A，参数均为 0 |
| 设置亮度 60 | `0E 00 1A 6D 01 00 00 00 3C` | 偏移 8 的 3C 是十进制 60 |
| 旋转 180° | `0D 00 1A 6D 01 00 00 00 02` | 偏移 8 为方向代码 2 |
| 发送 1024 字节 PNG | `66 00 1A 6D 01 00 00 00 00 00 04 00` | 长度按大端写入，USB 请求共 1536 字节 |
| 发送 512 字节 H.264 末块 | `79 00 1A 6D 01 00 00 00 00 00 02 00 01` | 偏移 12 为末块标志 |

应答解码示例：

- 17 应答 8–11 为 `00 03 18 00` 时，H.264 块长为 202752 字节，即 198 KiB。
- 100 应答 8–11 为 `00 10 00 00` 时，SD 总容量字段为 4096 KiB。此字段为小端。
- 裸通道温度查询发送 `60` 加 63 个零字节。应答偏移 1、2 若为 `23 28`，对应 35 °C、40 °C。
- 裸通道转速应答偏移 1–4 若为 `06 D6 03 E8`，对应原始值 1750 和 1000。

## 可直接调用的示例

在源码仓库根目录运行以下 CLI 命令。先用 `list` 获取设备索引。SDK ZIP 中可运行 `dotnet run --project examples/SdkDiscover` 枚举设备。

```powershell
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- list
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- info 0
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- brightness 0 60
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- image 0 artwork.png
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- video 0 motion.mp4 24 --loop
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- stop 0
```

SDK 可在同一个会话中调用设备方法。诊断返回原始应答，按上表解释。

```csharp
using TURZX.WinUsbDisplay;

var devices = DisplayDevice.Discover(DisplaySettings.InterfaceGuid);
if (devices.Count == 0)
    throw new InvalidOperationException("未找到 Win207 屏幕。");
using var screen = devices[0].Open(DisplaySettings.ReadCommandKey());
Console.WriteLine(screen.ReadFirmwareVersion());
screen.SetRotation(2);
byte[] status = screen.ReadDiagnostic(60);
if (status.Length >= 10)
    Console.WriteLine($"方向 {status[8]}，传感器 {status[9]}");
byte[] temperatures = screen.ReadDiagnostic(0x60, raw: true);
```

参考资料：[Microsoft WinUSB 指南](https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/using-winusb-api-to-communicate-with-a-usb-device)，[FFmpeg 格式文档](https://ffmpeg.org/ffmpeg-formats.html)。

直接引用设备控制 DLL 的项目配置及完整示例见 [DLL 开发指南](developer-guide.md)。
