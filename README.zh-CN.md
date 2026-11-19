# TURZX WinUSB Display Studio

[English](README.md)

了解 TURZX USB 副屏及更多产品，欢迎访问 [TURZX 官方网站](https://www.turzx.com/)。

把 TURZX USB 副屏变成桌面仪表盘。框选网页或窗口、叠加视频背景、循环播放照片，在 Studio 预览后直接送屏。

**[下载 Windows 版](../../releases)** · **[试用示例场景](docs/scenes.md)** · **[查设备兼容性](docs/compatibility.md)** · **[接入 .NET SDK](docs/developer-guide.md)**

| 数字时钟 | 霓虹星轨 | 像素烟花 |
| :---: | :---: | :---: |
| [![2.1 寸圆屏上的数字时钟](docs/images/round-240-clock.gif)](docs/images/round-240-clock.gif) | [![2.1 寸圆屏上的霓虹星轨](docs/images/round-240-stars.gif)](docs/images/round-240-stars.gif) | [![2.1 寸圆屏上的像素烟花](docs/images/round-240-fireworks.gif)](docs/images/round-240-fireworks.gif) |

以上为按 2.1 寸圆屏 480 × 480 分辨率渲染、缩小展示的循环 GIF 动画。

## 三步上手

1. 从 Releases 下载 Windows x64 ZIP，解压后运行 `TURZX.WinUsbDisplay.Studio.exe`。预览包已包含 .NET 运行时。
2. 在「媒体 → 前景来源」选择「HTML 实时渲染」，再从示例场景中选择「数字时钟」。未连接 USB 屏幕也可预览，HTML 渲染需要 Chrome 或 Edge。
3. 实机送屏需要对应的 WinUSB 驱动和固件维护者提供的封包参数。按 [首次送屏指南](docs/first-display.md) 核对硬件 ID、安装驱动并验证单图输出。

## 设备兼容性

| 屏幕 | USB VID:PID | 默认输出尺寸 |
| --- | --- | --- |
| 4.6 寸 | `1CBE:0046` | 320 × 960 |
| 2.1 寸圆屏 | `1CBE:0021` | 480 × 480 |
| 12.3 寸 | `1CBE:0123` | 720 × 1920 |
| 7 寸 | `1CBE:0070` | 800 × 1280 |
| 5 寸 | `1CBE:0055` | 720 × 1280 |

各型号的使用说明见[兼容性记录](docs/compatibility.md)。旧款 WCH、CDC、HID 显示协议不在此实现的支持范围内。

## 运行

从源码构建需要 Windows 和 .NET 10 SDK。转码视频需要带 `libx264` 的 FFmpeg，可在 **媒体 → FFmpeg** 中选择，也可加入 PATH 或设置 `WINUSB_DISPLAY_FFMPEG`。内置 HTML 预设可在没有 FFmpeg 时预览。

在 PowerShell 中将 `WINUSB_DISPLAY_COMMAND_KEY` 设为固件提供的 16 位十六进制值，再从同一会话启动 Studio 或 CLI。未设置时，Studio 仍可读取旧版保存的密钥。SDK 调用方可以直接传入 8 字节密钥。WinUSB 驱动使用自定义接口 GUID 时，设置 `WINUSB_DISPLAY_INTERFACE_GUID`。[Win207 协议说明](docs/win207-protocol.md)

```powershell
dotnet build TURZX.WinUsbDisplay.slnx -c Release
dotnet run --project src/TURZX.WinUsbDisplay.Studio
```

Studio 会自动更新屏幕下拉列表。选择屏幕，点击 **浏览** 打开媒体文件，然后点 **开始播放**。Studio 按预览区设置将 JPEG、PNG 和常见视频缩放、裁切或加黑边至屏幕原生尺寸，并应用位置偏移。视频会转成无音频的 Annex B H.264。4.6 寸屏的预览和发送均使用设备原生的 320 × 960。Studio 也会转码裸 `.h264` 或 `.264`，以应用所选布局。CLI 的 `video` 命令直接发送这两种文件。使用 CLI 时，请采用 Annex B、匹配设备输出尺寸，并传入源视频帧率。

```powershell
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- list
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- info 0
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- image 0 .\artwork.jpg
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- video 0 .\motion.mp4 24 --loop
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- stop 0
```

首页默认亮度为 66%，方向为 0°。Studio 按设备保存亮度和方向。亮度改动后短暂等待便会自动下发。方向改动会立即更新预览，并在开始送屏时应用到设备。连接或恢复已保存值时只更新界面。请在调整方向前停止视频。顶栏可切换简体中文、English、日本語和 한국어，语言偏好会自动保存，技术文档也随界面语言切换。**停止播放** 会取消当前输出。Studio 空闲时也可停止设备内视频和图片循环。**高级控制**可按需读取固件版本和存储容量，12.3 寸屏幕可读取供电电压，4.6 和 12.3 寸屏幕可显示或停止固件时钟。**设备指令**提供完整诊断与收发。

在 CLI 中按 Ctrl+C 结束循环传输，或在会话空闲时运行 `stop` 停止设备内播放。

## 屏幕预览

媒体页左侧可在本地预览图片、视频和实时前景，也可叠加视频背景。图片轮播在设备确认收到帧后显示完整分辨率的合成画面。输出视频文件时，预览使用 FFmpeg 重新封装发给设备的 H.264 码流。

打开 **技术文档**，可查找编号或命令名，复制选中内容及调用示例。

- DLL 开发指南 · [简体中文](docs/developer-guide.md) · [English](docs/developer-guide.en.md) · [日本語](docs/developer-guide.ja.md) · [한국어](docs/developer-guide.ko.md)
- Win207 协议说明 · [简体中文](docs/win207-protocol.md) · [English](docs/win207-protocol.en.md) · [日本語](docs/win207-protocol.ja.md) · [한국어](docs/win207-protocol.ko.md)

## 示例场景

在「HTML 实时渲染」的示例场景下拉中选择像素机器人、数字时钟、霓虹星轨、复古弹球、霓虹赛车或像素烟花。示例在本机渲染，透明区域可搭配视频背景。可在 `src/TURZX.WinUsbDisplay.Studio/Scenes` 修改示例或以它们为起点制作自己的场景。

## 实时输入与视频背景

在 **前景来源** 下拉框中可选择本地 HTML、HTTP(S) 网页、Windows 窗口、媒体文件或图片轮播。清空前景来源即可单独播放背景视频。HTML 使用独立的无头浏览器渲染，优先使用 Chrome，其次使用 Edge。可以通过 `WINUSB_DISPLAY_BROWSER` 指定浏览器可执行文件。网页渲染尺寸由设备和播放模式决定。需要叠加背景时，网页或 PNG 应保留透明区域。Studio 在被捕捉窗口最小化期间保留最后一帧，窗口恢复后继续取帧。部分受保护或硬件加速窗口可能返回空白画面。

桌面端与无头 CLI 均可循环播放本地视频背景。合成依赖固件的 H.264 与 PNG 图层支持。调整捕捉帧率可以适配 USB 带宽。退出实时输出后，设备可能继续显示最后一张前景 PNG。

Studio 新配置的实时采集目标为 20 FPS。部分设备在无背景、无裁剪且使用默认布局时，可用 FFmpeg 把网页或窗口帧编码为 H.264 后分块送屏。实际帧率取决于前景内容、固件和 USB 应答；轮播按设置的间隔换图。

### 无头程序

任意程序可向 CLI 的标准输入写二进制帧。每帧先写 4 字节无符号小端长度，再写一张完整的 8 位 RGBA PNG。推荐用与屏幕同尺寸的 PNG。`frames` 和 `image` 每张图片最多接受 950,000 字节，`image` 也接受 JPEG。生成程序的标准输出只传帧数据，日志写入标准错误。EOF 或 Ctrl+C 结束会话。

```cmd
your-renderer.exe | dotnet run --project src/TURZX.WinUsbDisplay.Cli -- frames 0 8
your-renderer.exe | dotnet run --project src/TURZX.WinUsbDisplay.Cli -- frames 0 8 background.mp4 24
```

例如 Python 生成程序可用 `sys.stdout.buffer.write(struct.pack("<I", len(png)) + png)`，每帧写完后调用 `sys.stdout.buffer.flush()`。

### 设备指令与协议参考

**设备指令**显示 Win207 指令预设、参数和设备回包。收发面板支持十进制和十六进制参数，适用指令可直接填设备路径或选择本地文件。指令能否发送取决于设备型号。**技术文档**中可查阅完整协议、搜索指令并复制调用示例。

支持桌面模式的屏幕可先发 `150` 设置 Windows 桌面模式，再单独发 `11` 重启。要恢复 Studio 送屏，在 **设备指令** 列表选中 **切回副屏模式**。在右侧刷新桌面模式设备列表，按序列号选中屏幕后点击 **切回副屏模式**。完整步骤和型号限制见 [Win207 设备指令技术说明](docs/win207-protocol.md)。

## .NET 接入

从对应版本的 [Releases](../../releases) 下载 `TURZX-WinUsbDisplay-SDK.zip`。设备控制只需引用 `lib/TURZX.WinUsbDisplay.dll`，需要媒体处理时再引用 `TURZX.WinUsbDisplay.Media.dll`。包内提供 API 文档和设备枚举示例。源码构建和 API 用法见 [开发指南](docs/developer-guide.md)。

## 故障排查

同一 Windows 用户下，Studio 和 CLI 共用按屏幕区分的会话锁。其他驱动或 SDK 的程序由 Windows 的共享规则决定能否打开设备。

SDK 可捕获 `DisplayOpenException`，并检查 `Failure` 和 `NativeErrorCode`。屏幕打开时发生共享冲突，CLI 返回 **2**。Windows 拒绝访问时返回 **3**，其他错误返回 **1**，取消返回 **130**。

**窗口全黑、看不到界面**：少数显卡驱动会把 WPF 内容渲染成黑屏。把环境变量 `WINUSB_DISPLAY_SOFTWARE_RENDER` 设为 `1` 后重启 Studio，即可强制本进程使用软件渲染，无需修改注册表：

```powershell
$env:WINUSB_DISPLAY_SOFTWARE_RENDER = "1"
dotnet run --project src/TURZX.WinUsbDisplay.Studio
```

它与注册表 `HKCU\Software\Microsoft\Avalon.Graphics\DisableHWAcceleration = 1` 等效，但只影响本进程；设为 `0` 或删除该变量即恢复硬件加速。

**USB 写入超时**：实时合成（PNG 前景 + 视频背景）时固件会短暂停顿。Studio 已放宽端点超时（写 10 秒、读 5 秒）以避免中断传输。若仍超时，重新插拔该屏幕的 USB 线，并确认没有其他进程同时控制它。

**找不到设备**：确认安装了设备对应的 WinUSB 驱动；驱动使用自定义接口 GUID 时，用 `WINUSB_DISPLAY_INTERFACE_GUID` 指定。

## 目录

来源和随包素材说明见 [NOTICE](NOTICE.md)。提交问题或实测结果请参阅[贡献指南](CONTRIBUTING.md)，私下报告漏洞请参阅[安全政策](SECURITY.md)。

- `src/TURZX.WinUsbDisplay` 提供设备枚举、WinUSB 通信和 Win207 设备控制 SDK。
- `src/TURZX.WinUsbDisplay.Media` 提供可选的 HTML 捕捉、视频转码和无头帧输入。
- `src/TURZX.WinUsbDisplay.Windows` 提供 WPF 图像处理、窗口捕捉和 Windows 密钥存储。SDK 发布包包含设备库和 Media 库。
- `src/TURZX.WinUsbDisplay.Cli` 提供命令行入口。
- `src/TURZX.WinUsbDisplay.Studio` 提供 WPF 桌面界面。
- `examples/SdkDiscover` 演示设备枚举，在仓库中引用源码项目，在 SDK 包中引用 DLL。
- `tests/TURZX.WinUsbDisplay.Tests` 提供纯逻辑的离线单元测试。
- `docs` 记录协议，便于其他语言实现。

本项目及其 Win207 实现采用 [MIT 许可证](LICENSE)。
