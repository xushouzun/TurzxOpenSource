# TURZX WinUSB Display Studio

[Official website](https://www.turzx.com/) · Explore TURZX displays and more.

Extract the entire ZIP into one directory, then run `TURZX.WinUsbDisplay.Studio.exe`. The package includes the .NET runtime for Windows x64.

- Install Chrome or Edge to try the [built-in HTML scenes](docs/scenes.en.md) without a display. Scene files are in `Scenes/` in the extracted directory.
- Before connecting a display, follow the [first-output guide](docs/first-display.en.md) to check the WinUSB driver and obtain the firmware command key. See the [compatibility record](docs/compatibility.en.md) for device results.
- The 3.5-inch native-theme display is rendered by Studio from JSON `.data` and sent over CH340 serial. Three neutral factory themes are bundled; no external usb_hid.exe is required.
- To transcode video, select an FFmpeg build with `libx264` under **Media → FFmpeg**.
- To restore Studio output while a display is in Windows desktop mode, select **Return to USB display** in the **Device commands** list. In the right pane, refresh the desktop-mode devices, select the display by serial number and click **Return to USB display**.
- Open **Documentation** in Studio or read the [protocol reference](docs/win207-protocol.en.md) and [SDK guide](docs/developer-guide.en.md) directly.

[License and notices](NOTICE.md) · [Reporting security issues](SECURITY.md)
