# TURZX WinUSB Display Studio

[TURZX 官方网站](https://www.turzx.com/) · 了解 TURZX 副屏及更多产品。

将整个 ZIP 解压到同一目录，然后运行 `TURZX.WinUsbDisplay.Studio.exe`。此包适用于 Windows x64，已包含 .NET 运行时。

- 离线预览时，安装 Chrome 或 Edge 后即可试用[内置 HTML 场景](docs/scenes.md)。场景文件位于解压目录的 `Scenes/`。
- 连接实体屏前，按[首次送屏指南](docs/first-display.md)检查 WinUSB 驱动并获取固件封包参数。各型号的设置说明见[设备兼容性](docs/compatibility.md)。
- 3.5 寸原生主题屏由 Studio 直接渲染 JSON `.data` 并通过 CH340 串口送屏；内置三个中性默认主题，不依赖外部 usb_hid.exe。
- 转码视频时，在 Studio 的「媒体 → FFmpeg」选择带 `libx264` 的 FFmpeg。
- 屏幕处于 Windows 桌面模式时，在「设备指令」列表选中「切回副屏模式」。在右侧刷新桌面模式设备，按序列号选中屏幕后点击「切回副屏模式」，恢复 Studio 送屏。
- 在 Studio 的「技术文档」页查看协议与 SDK 指南，也可直接打开[协议文档](docs/win207-protocol.md)和 [SDK 指南](docs/developer-guide.md)。

[许可证和来源](NOTICE.zh-CN.md) · [安全问题报告](SECURITY.zh-CN.md)
