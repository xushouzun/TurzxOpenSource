# 首次送屏

以 7 寸 `USB\VID_1CBE&PID_0070` 为例。其他型号和设置见[设备兼容性](compatibility.zh-CN.md)。

1. 从 Releases 下载并解压 `TURZX-Studio-win-x64.zip`，直接运行 `TURZX.WinUsbDisplay.Studio.exe`。预览、布局和离线配置无需连接屏幕。
2. 接入屏幕，在设备管理器的「属性 → 详细信息 → 硬件 ID」确认 VID/PID，并在「驱动程序 → 驱动程序详细信息」确认设备已绑定 `winusb.sys`。如需驱动，从 [TURZX 官网](https://www.turzx.com/) 或设备供应商获取适用于该硬件 ID 的签名 WinUSB 驱动。已有可用驱动时可直接进入下一步。相同尺寸的型号也可能使用不同协议。
3. 关闭正在占用屏幕的原厂软件。在「媒体 → 前景来源」切换到「直接选择图片」，点击「浏览」选择 JPEG 或 PNG，清空背景，再点击「开始播放」。在实体屏上确认画面内容和方向，然后点击「停止播放」。

如果屏幕当前显示 Windows 桌面，在「设备指令」中选择 `SWITCH_TO_MONITOR_MODE`，在右侧刷新桌面模式设备，按序列号选择屏幕并点击「切回副屏模式」。桌面模式需要随包提供的 TURZX 显示驱动；若 Windows 未出现显示器，可在 **高级控制** 页的桌面模式驱动区域点击「安装桌面模式驱动」。详见[桌面模式与副屏模式](win207-protocol.zh-CN.md)。

设备管理器能看到已处于副屏模式的屏幕而 Studio 列表为空时，请向驱动提供方核对 WinUSB 接口 GUID。使用自定义 GUID 时，在 `%LOCALAPPDATA%\WinUsbDisplay\studio-settings.json` 中设置 `InterfaceGuid` 字段；CLI 使用 `--interface-guid <GUID>` 参数。
