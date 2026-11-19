# 设备兼容性 · Display compatibility

| USB VID:PID | 原生分辨率 / Native size |
| --- | --- |
| `1CBE:0046` | 320 × 960 |
| `1CBE:0055` | 720 × 1280 |
| `1CBE:0070` | 800 × 1280 |
| `1CBE:0021` | 480 × 480 |
| `1CBE:0123` | 720 × 1920 |

12.3 寸屏（`1CBE:0123`）的画面方向请在 Studio **媒体** 页设置。要从 Windows 桌面恢复 Studio 送屏，在 **设备指令** 左侧选择 `SWITCH_TO_MONITOR_MODE`，按序列号选择目标并点击 **切回副屏模式**。此型号的可用指令和参数范围见[指令参考](win207-protocol.md)。

Set orientation for the 12.3-inch display (`1CBE:0123`) on Studio's **Media** page. To resume Studio output from Windows desktop mode, select `SWITCH_TO_MONITOR_MODE` in the **Device commands** list, then choose the target by serial number and click **Return to USB display**. See the [command reference](win207-protocol.en.md) for available commands and parameter ranges.

在 12.3 寸屏上，SDK 的 `ShowImage` 和 CLI 的 `image` 会在显示 JPEG 前清除旧 PNG 覆盖层。

On the 12.3-inch display, the SDK's `ShowImage` and the CLI's `image` clear any existing PNG overlay before showing a JPEG.

可通过 Issues 的「设备兼容性」表单报告兼容性问题。 / Use the Device compatibility issue form to report compatibility issues.
