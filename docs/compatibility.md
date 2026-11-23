# 设备兼容性 · Display compatibility

## 支持的 Win207 型号 / Supported Win207 models

| 屏幕 / Display | USB VID:PID | 原生送屏尺寸 / Native output |
| --- | --- | --- |
| 1.6 寸 / 1.6-inch | `1CBE:0005` | 400 × 400 |
| 1.8 寸 / 1.8-inch | `1CBE:A018` | 400 × 400 |
| 2.1 寸圆屏 / 2.1-inch round | `1CBE:0021` | 480 × 480 |
| 2.1 寸圆屏 SLC / 2.1-inch round SLC | `1CBE:B021` | 480 × 480 |
| 2.1 寸圆屏 A021 / 2.1-inch round A021 | `1CBE:A021` | 480 × 480 |
| 2.8 寸方屏 / 2.8-inch square | `1CBE:0016` | 400 × 400 |
| 2.8 寸圆屏 / 2.8-inch round | `1CBE:0028` | 480 × 480 |
| 2.88 寸圆屏 / 2.88-inch round | `1CBE:0288` | 480 × 480 |
| 3.4 寸圆屏 / 3.4-inch round | `1CBE:0034` | 480 × 480 |
| 3.4 寸圆屏 SLC / 3.4-inch round SLC | `1CBE:B034` | 480 × 480 |
| 3.5 寸 / 3.5-inch | `1CBE:0035` | 640 × 480 |
| 3.92 寸 / 3.92-inch | `1CBE:0392` | 1080 × 1240 |
| 4 寸 / 4-inch | `1CBE:0040` | 720 × 720 |
| 4.5 寸 / 4.5-inch | `1CBE:0045` | 1024 × 768 |
| 4.6 寸 / 4.6-inch | `1CBE:0046` | 320 × 960 |
| 5 寸 / 5-inch | `1CBE:0055` | 720 × 1280 |
| 5.2 寸 / 5.2-inch | `1CBE:0050` | 720 × 1280 |
| 6.2 寸 / 6.2-inch | `1CBE:A062` | 368 × 960 |
| 6.2 寸 V2 / 6.2-inch V2 | `1CBE:B062` | 448 × 1280 |
| 6.2 寸 V3 / 6.2-inch V3 | `1CBE:C062` | 480 × 1280 |
| 6.5 寸 / 6.5-inch | `1CBE:A065` | 720 × 1472 |
| 6.5 寸 1568 / 6.5-inch 1568 | `1CBE:B065` | 720 × 1568 |
| 6.8 寸 / 6.8-inch | `1CBE:0068` | 1080 × 2320 |
| 6.8 寸 2288 / 6.8-inch 2288 | `1CBE:A068` | 1080 × 2288 |
| 7 寸 / 7-inch | `1CBE:0070` | 800 × 1280 |
| 8 寸 / 8-inch | `1CBE:0080` | 800 × 1280 |
| 8.8 寸 / 8.8-inch | `1CBE:0088` | 480 × 1920 |
| 9.2 寸 / 9.2-inch | `1CBE:0092` | 464 × 1920 |
| 9.2 寸 A092 / 9.2-inch A092 | `1CBE:A092` | 464 × 1920 |
| 10 寸 / 10-inch | `1CBE:0109` | 1200 × 1920 |
| 11.3 寸 / 11.3-inch | `1CBE:0113` | 440 × 1920 |
| 12.3 寸 / 12.3-inch | `1CBE:0123` | 720 × 1920 |
| 12.4 寸 / 12.4-inch | `1CBE:0124` | 2560 × 666 |
| 12.68 寸 / 12.68-inch | `1CBE:0126` | 1920 × 140 |
| 14.5 寸 / 14.5-inch | `1CBE:0145` | 2560 × 720 |

`1CBE:0035` 是 WinUSB/Win207 的 3.5 寸屏；`1A86:5722` 是下文使用原项目原生主题的串口 3.5 寸屏，两者不是同一协议。`1CBE:A021` 为保留的 2.1 寸圆屏变体。`0416:8061`、`0416:8052` 是带泵/风扇控制的独立控制器，不在本 SDK 的屏幕型号目录内。

`1CBE:0035` is the WinUSB/Win207 3.5-inch display. `1A86:5722` in the next section is the serial 3.5-inch display that uses native themes from the original project; they are different protocols. `1CBE:A021` is a retained 2.1-inch round variant. `0416:8061` and `0416:8052` are standalone pump/fan controllers and are not part of this SDK's display catalog.

同一尺寸可能存在多种面板、固件和 USB PID，请以设备管理器中的硬件 ID 为准。6.2 寸 `C062` 的原厂主题画布为 448 × 1280，但实际送屏尺寸为 480 × 1280；9.2 寸 `0092`/`A092` 的原厂主题画布为 480 × 1920，实际送屏尺寸为 464 × 1920；11.3 寸 `0113` 的原厂主题画布为 480 × 1920，实际送屏尺寸为 440 × 1920。`0124`、`0126`、`0145` 为原生横屏长条，按表中宽高直接送屏。4.5 寸为原生横屏 1024 × 768，其余方向在 Studio 的 **媒体** 页设置。

The same physical size may use different panels, firmware and USB PIDs. Match the hardware ID shown in Device Manager. The 6.2-inch `C062` uses a 448 × 1280 vendor theme canvas but a 480 × 1280 transmission raster. The 9.2-inch `0092`/`A092` use a 480 × 1920 vendor canvas but a 464 × 1920 transmission raster. The 11.3-inch `0113` uses a 480 × 1920 vendor canvas but a 440 × 1920 transmission raster. `0124`, `0126` and `0145` are native landscape strips and use the width and height listed above. The 4.5-inch panel is natively landscape at 1024 × 768; set other orientations on Studio's **Media** page.

表中的 PID 和原生尺寸按原厂设备定义整理。首次连接某个型号时，请按[首次送屏指南](first-display.md)核对驱动、命令密钥和实体画面。 / The PIDs and native sizes above follow the vendor device definitions. When connecting a model for the first time, follow the [first-output guide](first-display.md#english) to verify the driver, command key and physical output.

6.8 寸屏（`1CBE:0068`）在默认 20 FPS、无视频背景且使用默认布局的 HTML 实时输出中优先走 H.264 分块传输，需要 FFmpeg。缺少 FFmpeg、使用自定义布局或背景合成时会回退到逐帧图片路径，帧率会下降。

The 6.8-inch display (`1CBE:0068`) uses chunked H.264 for live HTML at the default 20 FPS when no video background or custom layout is applied. This path requires FFmpeg. Without FFmpeg, or when using a custom layout or background composition, Studio falls back to per-frame images and the frame rate is lower.

12.3 寸屏（`1CBE:0123`）的画面方向请在 Studio **媒体** 页设置。要从 Windows 桌面恢复 Studio 送屏，在 **设备指令** 左侧选择 `SWITCH_TO_MONITOR_MODE`，按序列号选择目标并点击 **切回副屏模式**。此型号的可用指令和参数范围见[指令参考](win207-protocol.md)。

Set orientation for the 12.3-inch display (`1CBE:0123`) on Studio's **Media** page. To resume Studio output from Windows desktop mode, select `SWITCH_TO_MONITOR_MODE` in the **Device commands** list, then choose the target by serial number and click **Return to USB display**. See the [command reference](win207-protocol.en.md) for available commands and parameter ranges.

在 12.3 寸屏上，SDK 的 `ShowImage` 和 CLI 的 `image` 会在显示 JPEG 前清除旧 PNG 覆盖层。

On the 12.3-inch display, the SDK's `ShowImage` and the CLI's `image` clear any existing PNG overlay before showing a JPEG.

旧款 WCH、CDC、HID 显示协议、6.2 寸旧 WinLcd 型号 `1CBE:0062` 以及旧版 4 寸 `1D6B:A040` 不在此实现的支持范围内。

Older WCH, CDC and HID display protocols are out of scope, as are the 6.2-inch legacy WinLcd model `1CBE:0062` and the legacy 4-inch `1D6B:A040`.

## 原生主题设备 / Native theme devices

| 屏幕 / Display | USB VID:PID | 接口 / Interface | 原生尺寸 / Native canvas |
| --- | --- | --- | --- |
| 3.5 寸 / 3.5-inch | `1A86:5722` | CH340 串口（`USB35INCH`） / CH340 serial | 480 × 320 |

3.5 寸屏不是 WinUSB/Win207 设备，而是通过 CH340 串口接收 RGB565 图像。Studio 识别到 `USB35INCH` 串口后，会隐藏 HTML、窗口、图片、网页和轮播等前景来源，只显示 **原生主题**。主题 `.data` 可从任意位置选择，Studio 在本机解析 JSON、渲染预览并将画面发送到串口，不依赖外部 usb_hid.exe 或原项目配置。

- 随 Studio 内置三个中性默认主题：`默认主题-深蓝`、`默认主题-深空`、`默认主题-灰岩`，均使用 480 × 320 中性背景，不包含第三方品牌、动漫或游戏素材。
- 默认主题基于 `35inchNEW\3.5` 中的中性 480 × 320 背景生成。该包 `config` 下部分 `.data` 为 224 × 64 画布，不适用于 3.5 寸屏，Studio 会拒绝画布不匹配的主题。
- 当前集成渲染器读取 JSON 格式 `.data`；旧版二进制 `.data` 需要先转换为 JSON。
- 预览和送屏使用主题内的示例值；硬件传感器实时数据尚未接入。
- 该设备不支持 Studio 的其它前景来源，因此这些选项不会显示。

The 3.5-inch display is not a WinUSB/Win207 device. It receives RGB565 images over a CH340 serial port. When Studio finds the `USB35INCH` serial device, it hides HTML, window, image, website and slideshow foreground sources and shows only **Native theme**. A `.data` theme can be selected from any location; Studio parses the JSON locally, renders the preview and sends the frame over serial without the external usb_hid.exe or its configuration.

- Three neutral factory themes ship with Studio: `默认主题-深蓝`, `默认主题-深空` and `默认主题-灰岩`. They use neutral 480 × 320 backgrounds and contain no third-party brand, anime or game artwork.
- The factory themes are built from neutral 480 × 320 backgrounds in `35inchNEW\3.5`. Some `.data` themes under that package's `config` folder use a 224 × 64 canvas and are not valid for the 3.5-inch display; Studio rejects themes whose canvas does not match.
- The integrated renderer reads JSON `.data` themes. Legacy binary `.data` files must be converted to JSON first.
- Preview and output use the sample values stored in the theme. Live hardware sensor data is not wired in yet.
- Other foreground sources are not supported by this device and are hidden.

可通过 Issues 的「设备兼容性」表单报告兼容性问题。 / Use the Device compatibility issue form to report compatibility issues.
