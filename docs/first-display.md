# 首次送屏

以 7 寸 `USB\VID_1CBE&PID_0070` 为例。其他型号和设置见[设备兼容性](compatibility.md)。

1. 解压对应 Release 的 `TURZX-Studio-win-x64.zip`，运行 `TURZX.WinUsbDisplay.Studio.exe`。可先用 Chrome 或 Edge 预览内置数字时钟，无需 USB 屏幕。
2. 接入屏幕，在设备管理器的「属性 → 详细信息 → 硬件 ID」确认 VID/PID，并在「驱动程序 → 驱动程序详细信息」检查设备接口是否使用 `winusb.sys`。如需驱动，从 [TURZX 官网](https://www.turzx.com/) 的下载和支持入口或设备供应商索取适用于该硬件 ID 的签名 WinUSB 驱动。已有可用驱动时可以直接继续。屏幕尺寸相同的产品也可能使用不同协议。
3. 通过设备供应商联系对应固件的维护者，提供硬件 ID 和从随附工具或设备资料查到的固件版本，索取该固件的 8 字节封包参数。项目发布包不附带该参数。暂时拿不到参数时，可以使用离线预览。请勿在公开 Issue 中粘贴参数。
4. 退出占用设备的原厂软件。在 PowerShell 中将 `WINUSB_DISPLAY_COMMAND_KEY` 设为维护者提供的 16 位十六进制字符串，从同一会话启动 Studio。选择屏幕后在「高级控制」读取固件版本，核对密钥对应的版本。
5. 在「媒体 → 前景来源」切换到「直接选择图片」，点击「浏览」选择 JPEG 或 PNG，清空背景，再点击「开始播放」。在实体屏上确认画面内容和方向，然后点击「停止播放」。记录 Studio 版本、固件版本、方向和结果。

从解压目录启动 Studio 的 PowerShell 示例：

```powershell
$env:WINUSB_DISPLAY_COMMAND_KEY = '<固件维护者提供的 16 位十六进制字符串>'
.\TURZX.WinUsbDisplay.Studio.exe
```

如果屏幕当前显示 Windows 桌面，在 Studio 的「设备指令」列表中选择 `SWITCH_TO_MONITOR_MODE`。在右侧刷新桌面模式设备，按序列号选中屏幕后点击「切回副屏模式」。恢复后屏幕会重新出现在 Studio 的 USB 设备列表中。[桌面模式与副屏模式的操作说明](win207-protocol.md)

设备管理器能看到已处于副屏模式的屏幕而 Studio 列表为空时，请向驱动提供方核对 WinUSB 接口 GUID。如果提供方确认使用自定义接口 GUID，关闭 Studio，在 PowerShell 设置环境变量后从该窗口重新启动。

```powershell
$env:WINUSB_DISPLAY_INTERFACE_GUID = '<驱动提供方给出的 GUID>'
.\TURZX.WinUsbDisplay.Studio.exe
```
