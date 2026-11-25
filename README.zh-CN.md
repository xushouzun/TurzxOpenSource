# TURZX WinUSB Display Studio

[English](README.md)

了解 TURZX USB 副屏及更多产品，欢迎访问 [TURZX 官方网站](https://www.turzx.com/)。

把 TURZX Win207 USB 副屏变成桌面仪表盘。框选网页或窗口、叠加视频背景、循环播放照片，在 Studio 预览后直接送屏。

**[下载 Windows 版](https://github.com/xushouzun/TurzxOpenSource/releases)** · **[试用示例场景](#示例场景)** · **[查设备兼容性](docs/compatibility.zh-CN.md)** · **[接入 .NET SDK](docs/developer-guide.zh-CN.md)**

| 数字时钟 | 霓虹星轨 | 像素烟花 |
| :---: | :---: | :---: |
| [![2.1 寸圆屏上的数字时钟](docs/images/round-240-clock.gif)](docs/images/round-240-clock.gif) | [![2.1 寸圆屏上的霓虹星轨](docs/images/round-240-stars.gif)](docs/images/round-240-stars.gif) | [![2.1 寸圆屏上的像素烟花](docs/images/round-240-fireworks.gif)](docs/images/round-240-fireworks.gif) |

以上为按 2.1 寸圆屏 480 × 480 分辨率渲染的循环 GIF 动画。

## 三步上手

1. 从 Releases 下载 Windows x64 ZIP，解压后运行 `TURZX.WinUsbDisplay.Studio.exe`。包内已包含 .NET 运行时。
2. 在「媒体 → 前景来源」选择「HTML 实时渲染」，再选择「数字时钟」。未连接屏幕也可预览，HTML 渲染需要 Chrome 或 Edge。
3. Win207 实机送屏需要对应的 WinUSB 驱动。按 [首次送屏指南](docs/first-display.zh-CN.md) 核对硬件 ID、安装驱动并确认单图输出。

## 设备兼容性

| 屏幕 | USB VID:PID | 默认输出尺寸 |
| --- | --- | --- |
| 2.1 寸圆屏 | `1CBE:0021` | 480 × 480 |
| 2.1 寸圆屏 SLC | `1CBE:B021` | 480 × 480 |
| 4 寸 | `1CBE:0040` | 720 × 720 |
| 4.5 寸 | `1CBE:0045` | 1024 × 768 |
| 4.6 寸 | `1CBE:0046` | 320 × 960 |
| 5 寸 | `1CBE:0055` | 720 × 1280 |
| 5.2 寸 | `1CBE:0050` | 720 × 1280 |
| 6.2 寸 | `1CBE:A062` | 368 × 960 |
| 6.2 寸 V2 | `1CBE:B062` | 448 × 1280 |
| 6.2 寸 V3 | `1CBE:C062` | 480 × 1280 |
| 6.5 寸 | `1CBE:A065` | 720 × 1472 |
| 6.5 寸 1568 | `1CBE:B065` | 720 × 1568 |
| 6.8 寸 | `1CBE:0068` | 1080 × 2320 |
| 6.8 寸 2288 | `1CBE:A068` | 1080 × 2288 |
| 7 寸 | `1CBE:0070` | 800 × 1280 |
| 8 寸 | `1CBE:0080` | 800 × 1280 |
| 8.8 寸 | `1CBE:0088` | 480 × 1920 |
| 9.2 寸 | `1CBE:0092` | 464 × 1920 |
| 9.2 寸 A092 | `1CBE:A092` | 464 × 1920 |
| 12.3 寸 | `1CBE:0123` | 720 × 1920 |

全部 35 个 Win207 PID 见[兼容性记录](docs/compatibility.zh-CN.md#支持的-win207-型号)。3.5 寸串口屏使用 `1A86:5722` CH340 串口，Studio 在本机渲染 `.data` 主题文件并内置八个默认主题，详见[支持的串口屏](docs/compatibility.zh-CN.md#支持的串口屏)和[串口协议](docs/serial-protocol.zh-CN.md)。

## 构建与运行

从源码构建需要 Windows 和 .NET 10 SDK。命令密钥已内置。转码视频需要带 `libx264` 的 FFmpeg，可在 **媒体 → FFmpeg** 中选择。

```powershell
dotnet build TURZX.WinUsbDisplay.slnx -c Release
dotnet run --project src/TURZX.WinUsbDisplay.Studio
```

CLI：

```powershell
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- list
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- image 0 .\artwork.jpg
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- video 0 .\motion.mp4 24 --loop
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- stop 0
```

## 功能

- 前景来源支持本地 HTML、HTTP(S) 网页、Windows 窗口、图片或图片轮播；清空前景来源即可只播放背景视频。
- HTML 使用独立的无头浏览器渲染，优先 Chrome，其次 Edge。
- 预览在本地合成各图层，视频转换为无音频的 Annex B H.264。
- 亮度和方向按 Win207 屏幕保存，新的实时采集默认 20 FPS。
- 界面与内置技术文档支持简体中文、English、日本語和 한국어。
- **高级控制**按型号提供固件、存储、供电电压查询和固件时钟控制；**设备指令**可发送 Win207 指令、查看应答并切回副屏模式。

### 无头输入

`frames` 从标准输入读取帧：4 字节小端长度加一张 RGBA PNG，错误信息写入标准错误。

```cmd
your-renderer.exe | dotnet run --project src/TURZX.WinUsbDisplay.Cli -- frames 0 8
```

## 示例场景

在「HTML 实时渲染」中选择预设。示例在本机渲染，透明区域可叠加在视频背景上。可在 `src/TURZX.WinUsbDisplay.Studio/Scenes` 修改或扩展。

| 预设 | HTML |
| --- | --- |
| 像素机器人 | default.html |
| 数字时钟 | clock.html |
| 霓虹星轨 | stars.html |
| 复古弹球 | pong.html |
| 霓虹赛车 | race.html |
| 像素烟花 | fireworks.html |

## .NET 接入

从对应版本的 [Releases](https://github.com/xushouzun/TurzxOpenSource/releases) 下载 `TURZX-WinUsbDisplay-SDK.zip`。设备控制引用 `lib/TURZX.WinUsbDisplay.dll`，媒体处理再引用 `TURZX.WinUsbDisplay.Media.dll`。用法见 [开发指南](docs/developer-guide.zh-CN.md)。

## 故障排查

- **Win207 USB 写入超时**：重新插拔屏幕，并确认没有其他程序控制该设备。
- **找不到 Win207 屏幕**：安装对应的 WinUSB 驱动。

本项目及其 Win207 实现采用 [MIT 许可证](LICENSE)。
