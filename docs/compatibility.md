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

### Win207 notes

`1CBE:0035` is the WinUSB/Win207 3.5-inch display. `1CBE:A021` is a 2.1-inch round variant.

The same size may use different panels, firmware and PIDs; match the hardware ID in Device Manager. The 6.2-inch `C062`, 9.2-inch `0092`/`A092` and 11.3-inch `0113` use vendor theme canvases that differ from their output sizes; Studio outputs the listed sizes. `0124`, `0126` and `0145` are native landscape strips. The 4.5-inch panel is natively landscape at 1024 × 768; set other orientations on the **Media** page.

The 6.8-inch display (`1CBE:0068`) uses chunked H.264 for live HTML only at 20 FPS, 0°, no background and the default layout; FFmpeg is required. Other configurations use per-frame images at a lower frame rate.

Set the 12.3-inch display's orientation on the **Media** page. To resume Studio output from Windows desktop mode, select `SWITCH_TO_MONITOR_MODE` in **Device commands**, choose the target by device serial number and click **Return to USB display**. Windows also needs the bundled TURZX display driver to use the panel as a monitor in desktop mode; install it from the desktop-mode driver section in **Advanced controls** when it is missing.

## Supported serial displays

| Display | USB VID:PID | Interface | Native canvas |
| --- | --- | --- | --- |
| 3.5-inch | `1A86:5722` | CH340 serial (`USB35INCH`) | 480 × 320 |

The 3.5-inch serial display (`1A86:5722`) is separate from the WinUSB/Win207 `1CBE:0035` and uses CH340, not WinUSB/Win207. Studio recognizes it by `USB35INCH`, hides other foreground sources and shows only the **Native theme** source. Select any `.data` theme; Studio renders it locally and sends the frame over serial. See the [serial protocol](serial-protocol.md) for the register packet and frame format.

- Eight factory themes ship with Studio: `默认主题-深蓝`, `默认主题-深空`, `默认主题-灰岩`, `默认主题-极简`, `默认主题-智显` and `默认主题-地球` use 480 × 320, while `默认主题-笔记` and `默认主题-仪表` use 320 × 480. Other canvas sizes are rejected.
- Studio reads the original `.data` themes produced by the 3.5-inch display tool.
- Brightness and orientation are on the **Media** page. Brightness is 0–100% (0% darkest, 100% brightest); orientation is 0° or 180°. Changes apply during playback; orientation changes resend the frame.
- Preview and output use simulated mock values for named metrics and theme values for custom text; live sensor data is not read.
