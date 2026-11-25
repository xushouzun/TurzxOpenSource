# TURZX WinUSB Display Studio

[TURZX 官方网站](https://www.turzx.com/) · 了解 TURZX 副屏及更多产品。

将整个 ZIP 解压到同一目录，然后运行 `TURZX.WinUsbDisplay.Studio.exe`。此包适用于 Windows x64，已包含 .NET 运行时。

支持 Win207 系列 USB 副屏，以及使用 `.data` 主题文件的 3.5 寸串口屏。完整型号见[设备兼容性](docs/compatibility.zh-CN.md)。

- 离线预览时，安装 Chrome 或 Edge 后即可试用内置 HTML 场景。场景文件位于解压目录的 `Scenes/`。
- 连接 Win207 屏幕前，按[首次送屏指南](docs/first-display.zh-CN.md)检查 WinUSB 驱动。支持的型号和说明见[设备兼容性](docs/compatibility.zh-CN.md)。
- 3.5 寸串口屏由 Studio 直接渲染 `.data` 主题文件并通过 CH340 串口送屏；内置三个中性默认主题。
- 转码视频时，在 Studio 的「媒体 → FFmpeg」选择带 `libx264` 的 FFmpeg。
- Win207 屏幕处于 Windows 桌面模式时，在「设备指令」列表选择 `SWITCH_TO_MONITOR_MODE`。在右侧刷新桌面模式设备，按序列号选中屏幕后点击「切回副屏模式」，恢复 Studio 送屏。桌面模式还需要随包提供的 TURZX 显示驱动，可在 **高级控制** 页的桌面模式驱动区域点击「安装桌面模式驱动」并在管理员提示中确认；需要移除时点击「卸载桌面模式驱动」。
- 在 Studio 的「技术文档」页查看协议与 SDK 指南，也可直接打开 [Win207 协议](docs/win207-protocol.zh-CN.md)、[串口协议](docs/serial-protocol.zh-CN.md) 和 [SDK 指南](docs/developer-guide.zh-CN.md)。

## 目录

- `TURZX.WinUsbDisplay.Studio.exe`：主程序。
- `docs/`：首次送屏、设备兼容性、Win207 与串口屏协议、SDK 指南。
- `NativeThemes/`：3.5 寸串口屏默认主题。
- `Scenes/`：内置 HTML 场景。
- `Driver/`：Win207 屏幕桌面模式显示驱动。
- `licenses/`：运行时许可证与第三方声明。

## 故障排查

- **Win207 USB 写入超时**：重新插拔屏幕，并确认没有其他程序占用设备。
- **找不到 Win207 屏幕**：安装对应的 WinUSB 驱动。

[许可证](LICENSE)
