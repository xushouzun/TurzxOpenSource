# 首次送屏 / First device output

以 7 寸 `USB\VID_1CBE&PID_0070` 为例。已记录的实测固件为 `turzx_0001_0017`。其他型号和固件见[兼容性记录](compatibility.md)。

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

设备管理器能看到屏幕而 Studio 列表为空时，请向驱动提供方核对 WinUSB 接口 GUID。如果提供方确认使用自定义接口 GUID，关闭 Studio，在 PowerShell 设置环境变量后从该窗口重新启动。

```powershell
$env:WINUSB_DISPLAY_INTERFACE_GUID = '<驱动提供方给出的 GUID>'
.\TURZX.WinUsbDisplay.Studio.exe
```

## English

Use the 7-inch `USB\VID_1CBE&PID_0070` as the starting point. Firmware `turzx_0001_0017` has recorded device results. See the [compatibility record](compatibility.md) for other models.

1. Extract `TURZX-Studio-win-x64.zip` from the release and launch `TURZX.WinUsbDisplay.Studio.exe`. Preview the built-in Digital Clock with Chrome or Edge before connecting a display.
2. In Device Manager, check **Properties → Details → Hardware Ids** for the VID and PID, and **Driver → Driver Details** for `winusb.sys`. If a driver is needed, ask the [TURZX download or support channel](https://www.turzx.com/en/) or your device supplier for a signed WinUSB driver matching the hardware ID.
3. Ask the supplier to put you in touch with the firmware maintainer. Provide the hardware ID and the firmware version shown by the vendor tool or device documentation, and request the matching 8-byte command packet key. The release requires a separately supplied key for physical output. Offline preview is available while you obtain it. Keep the key out of public reports.
4. Close vendor software that is using the display. Set `WINUSB_DISPLAY_COMMAND_KEY` to the supplied 16 hexadecimal characters in PowerShell, then launch Studio from that session. Select the display and read the firmware version in **Advanced controls** to check it against the version supplied with the key.
5. In **Media → Foreground source**, switch to **Choose image**, then use **Browse** to select a JPEG or PNG. Clear the background and click **Start playback**. Check the image and orientation on the physical display, then click **Stop playback**. Record the Studio version, firmware version, orientation and result.

If Device Manager sees the display while Studio's list stays empty, ask the driver supplier which WinUSB interface GUID it registers. For a custom GUID, use the PowerShell example above and launch Studio from that same session.
