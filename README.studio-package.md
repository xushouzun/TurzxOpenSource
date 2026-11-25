# TURZX WinUSB Display Studio

[Official website](https://www.turzx.com/) · Explore TURZX displays and more.

Extract the entire ZIP into one directory, then run `TURZX.WinUsbDisplay.Studio.exe`. The package includes the .NET runtime for Windows x64.

Supports Win207 USB displays and the 3.5-inch serial display, which uses `.data` theme files. See the [compatibility record](docs/compatibility.md) for the full model list.

- Install Chrome or Edge to try the built-in HTML scenes without a display. Scene files are in `Scenes/` in the extracted directory.
- Before connecting a Win207 display, follow the [first-output guide](docs/first-display.md) to check the WinUSB driver. Supported models and notes are listed in the [compatibility record](docs/compatibility.md).
- Studio renders `.data` theme files for the 3.5-inch serial display and sends the frame over CH340 serial. Three neutral factory themes are bundled.
- To transcode video, select an FFmpeg build with `libx264` under **Media → FFmpeg**.
- To restore Studio output while a Win207 display is in Windows desktop mode, select `SWITCH_TO_MONITOR_MODE` in the **Device commands** list. In the right pane, refresh the desktop-mode devices, select the display by serial number and click **Return to USB display**. Desktop mode also requires the bundled TURZX display driver; open **Advanced controls** and click **Install desktop-mode driver**, then approve the administrator prompt. Use **Uninstall desktop-mode driver** in the same section to remove it.
- Open **Documentation** in Studio or read the [Win207 protocol](docs/win207-protocol.md), [serial protocol](docs/serial-protocol.md) and [SDK guide](docs/developer-guide.md) directly.

## Package contents

- `TURZX.WinUsbDisplay.Studio.exe`: main application.
- `docs/`: first-output, compatibility, Win207 and serial display protocols, and SDK guides.
- `NativeThemes/`: factory themes for the 3.5-inch serial display.
- `Scenes/`: built-in HTML scenes.
- `Driver/`: desktop-mode display driver for Win207 displays.
- `licenses/`: runtime licenses and third-party notices.

## Troubleshooting

- **Win207 USB write timeout:** reconnect the display and make sure no other application is using it.
- **Win207 display not found:** install the matching WinUSB driver.

[License](LICENSE)
