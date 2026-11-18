# TURZX WinUSB Display Studio

[TURZX 官方网站 / Official website](https://www.turzx.com/) · 了解 TURZX 副屏及更多产品 / Explore TURZX displays and more.

## 简体中文

将整个 ZIP 解压到同一目录，然后运行 `TURZX.WinUsbDisplay.Studio.exe`。此包适用于 Windows x64，已包含 .NET 运行时。

- 离线预览时，安装 Chrome 或 Edge 后即可试用[内置 HTML 场景](docs/scenes.md)。场景文件位于解压目录的 `Scenes/`。
- 连接实体屏前，按[首次送屏指南](docs/first-display.md)检查 WinUSB 驱动并获取固件封包参数。各型号的实测情况见[兼容性记录](docs/compatibility.md)。
- 转码视频时，在 Studio 的「媒体 → FFmpeg」选择带 `libx264` 的 FFmpeg。
- 在 Studio 的「技术文档」页查看协议与 SDK 指南，也可直接打开[协议文档](docs/win207-protocol.md)和 [SDK 指南](docs/developer-guide.md)。

## English

Extract the entire ZIP into one directory, then run `TURZX.WinUsbDisplay.Studio.exe`. The package includes the .NET runtime for Windows x64.

- Install Chrome or Edge to try the [built-in HTML scenes](docs/scenes.md) without a display. Scene files are in `Scenes/` in the extracted directory.
- Before connecting a display, follow the [first-output guide](docs/first-display.md#english) to check the WinUSB driver and obtain the firmware command key. See the [compatibility record](docs/compatibility.md) for device results.
- To transcode video, select an FFmpeg build with `libx264` under **Media → FFmpeg**.
- Open **Documentation** in Studio or read the [protocol reference](docs/win207-protocol.en.md) and [SDK guide](docs/developer-guide.en.md) directly.

[License and notices / 许可证和来源](NOTICE.md) · [Reporting security issues / 安全问题报告](SECURITY.md)
