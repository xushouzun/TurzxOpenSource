# Display compatibility

## Supported Win207 models

| Display | USB VID:PID | Native output |
| --- | --- | --- |
| 1.6-inch | `1CBE:0005` | 400 × 400 |
| 1.8-inch | `1CBE:A018` | 400 × 400 |
| 2.1-inch round | `1CBE:0021` | 480 × 480 |
| 2.1-inch round SLC | `1CBE:B021` | 480 × 480 |
| 2.1-inch round A021 | `1CBE:A021` | 480 × 480 |
| 2.8-inch square | `1CBE:0016` | 400 × 400 |
| 2.8-inch round | `1CBE:0028` | 480 × 480 |
| 2.88-inch round | `1CBE:0288` | 480 × 480 |
| 3.4-inch round | `1CBE:0034` | 480 × 480 |
| 3.4-inch round SLC | `1CBE:B034` | 480 × 480 |
| 3.5-inch | `1CBE:0035` | 640 × 480 |
| 3.92-inch | `1CBE:0392` | 1080 × 1240 |
| 4-inch | `1CBE:0040` | 720 × 720 |
| 4.5-inch | `1CBE:0045` | 1024 × 768 |
| 4.6-inch | `1CBE:0046` | 320 × 960 |
| 5-inch | `1CBE:0055` | 720 × 1280 |
| 5.2-inch | `1CBE:0050` | 720 × 1280 |
| 6.2-inch | `1CBE:A062` | 368 × 960 |
| 6.2-inch V2 | `1CBE:B062` | 448 × 1280 |
| 6.2-inch V3 | `1CBE:C062` | 480 × 1280 |
| 6.5-inch | `1CBE:A065` | 720 × 1472 |
| 6.5-inch 1568 | `1CBE:B065` | 720 × 1568 |
| 6.8-inch | `1CBE:0068` | 1080 × 2320 |
| 6.8-inch 2288 | `1CBE:A068` | 1080 × 2288 |
| 7-inch | `1CBE:0070` | 800 × 1280 |
| 8-inch | `1CBE:0080` | 800 × 1280 |
| 8.8-inch | `1CBE:0088` | 480 × 1920 |
| 9.2-inch | `1CBE:0092` | 464 × 1920 |
| 9.2-inch A092 | `1CBE:A092` | 464 × 1920 |
| 10-inch | `1CBE:0109` | 1200 × 1920 |
| 11.3-inch | `1CBE:0113` | 440 × 1920 |
| 12.3-inch | `1CBE:0123` | 720 × 1920 |
| 12.4-inch | `1CBE:0124` | 2560 × 666 |
| 12.68-inch | `1CBE:0126` | 1920 × 140 |
| 14.5-inch | `1CBE:0145` | 2560 × 720 |

`1CBE:0035` is the WinUSB/Win207 3.5-inch display. `1A86:5722` in the next section is the serial 3.5-inch display that uses native themes from the original project; they are different protocols. `1CBE:A021` is a retained 2.1-inch round variant. `0416:8061` and `0416:8052` are standalone pump/fan controllers and are not part of this SDK's display catalog.

The same physical size may use different panels, firmware and USB PIDs. Match the hardware ID shown in Device Manager. The 6.2-inch `C062` uses a 448 × 1280 vendor theme canvas but a 480 × 1280 transmission raster. The 9.2-inch `0092`/`A092` use a 480 × 1920 vendor canvas but a 464 × 1920 transmission raster. The 11.3-inch `0113` uses a 480 × 1920 vendor canvas but a 440 × 1920 transmission raster. `0124`, `0126` and `0145` are native landscape strips and use the width and height listed above. The 4.5-inch panel is natively landscape at 1024 × 768; set other orientations on Studio's **Media** page.

The PIDs and native sizes above follow the vendor device definitions. When connecting a model for the first time, follow the [first-output guide](first-display.en.md) to verify the driver, command key and physical output.

The 6.8-inch display (`1CBE:0068`) uses chunked H.264 for live HTML at the default 20 FPS when no video background or custom layout is applied. This path requires FFmpeg. Without FFmpeg, or when using a custom layout or background composition, Studio falls back to per-frame images and the frame rate is lower.

Set orientation for the 12.3-inch display (`1CBE:0123`) on Studio's **Media** page. To resume Studio output from Windows desktop mode, select `SWITCH_TO_MONITOR_MODE` in the **Device commands** list, then choose the target by serial number and click **Return to USB display**. See the [command reference](win207-protocol.en.md) for available commands and parameter ranges.

On the 12.3-inch display, the SDK's `ShowImage` and the CLI's `image` clear any existing PNG overlay before showing a JPEG.

Older WCH, CDC and HID display protocols are out of scope, as are the 6.2-inch legacy WinLcd model `1CBE:0062` and the legacy 4-inch `1D6B:A040`.

## Native theme devices

| Display | USB VID:PID | Interface | Native canvas |
| --- | --- | --- | --- |
| 3.5-inch | `1A86:5722` | CH340 serial (`USB35INCH`) | 480 × 320 |

The 3.5-inch display is not a WinUSB/Win207 device. It receives RGB565 images over a CH340 serial port. When Studio finds the `USB35INCH` serial device, it hides HTML, window, image, website and slideshow foreground sources and shows only **Native theme**. A `.data` theme can be selected from any location; Studio parses the JSON locally, renders the preview and sends the frame over serial without the external usb_hid.exe or its configuration.

- Three neutral factory themes ship with Studio: `默认主题-深蓝`, `默认主题-深空` and `默认主题-灰岩`. They use neutral 480 × 320 backgrounds and contain no third-party brand, anime or game artwork.
- The factory themes are built from neutral 480 × 320 backgrounds in `35inchNEW\3.5`. Some `.data` themes under that package's `config` folder use a 224 × 64 canvas and are not valid for the 3.5-inch display; Studio rejects themes whose canvas does not match.
- The integrated renderer reads JSON `.data` themes. Legacy binary `.data` files must be converted to JSON first.
- Brightness and orientation use the shared controls below the preview. The brightness percentage maps to screen brightness, where 0% is darkest and 100% is brightest. This display supports only 0° and 180°. Changes are sent immediately while playing; changing orientation resends the full frame.
- The startup sequence follows the original project: connection probe, wake, mirror, orientation, brightness, full image.
- Mirror and orientation use the original project's fixed 16-byte control packet (6-byte header + 10-byte data) written in one call. Splitting it into a 6-byte header plus a 16-byte payload desynchronizes the firmware, so later brightness and image updates do not take effect.
- Preview and output use the sample values stored in the theme. Live hardware sensor data is not wired in yet.
- Set `WINUSB_DISPLAY_NATIVE_THEME_SEND_DIR` to save the frame actually sent to the display as a PNG before each transfer, for comparison with the physical screen.
- Other foreground sources are not supported by this device and are hidden.

Use the Device compatibility issue form to report compatibility issues.
