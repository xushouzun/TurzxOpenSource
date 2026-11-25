# TURZX WinUSB Display Studio

[简体中文](README.zh-CN.md)

Explore TURZX displays and more at the [official TURZX website](https://www.turzx.com/).

Turn a TURZX Win207 USB screen into a desktop dashboard. Crop a webpage or window, layer it over a video, or loop a set of photos. Preview the result in Studio before sending it to the display.

**[Download for Windows](https://github.com/xushouzun/TurzxOpenSource/releases)** · **[Sample scenes](#example-scenes)** · **[Hardware compatibility](docs/compatibility.md)** · **[.NET SDK](docs/developer-guide.md)**

| Digital clock | Neon star trails | Pixel fireworks |
| :---: | :---: | :---: |
| [![Digital clock on the 2.1-inch round display](docs/images/round-240-clock.gif)](docs/images/round-240-clock.gif) | [![Neon star trails on the 2.1-inch round display](docs/images/round-240-stars.gif)](docs/images/round-240-stars.gif) | [![Pixel fireworks on the 2.1-inch round display](docs/images/round-240-fireworks.gif)](docs/images/round-240-fireworks.gif) |

Looping GIFs are rendered at the 2.1-inch round display’s 480 × 480 resolution.

## Quick start

1. Download the Windows x64 ZIP from Releases, extract it and run `TURZX.WinUsbDisplay.Studio.exe`. The .NET runtime is included.
2. In **Media**, choose **Render HTML live** and select **Digital clock**. Preview requires Chrome or Edge and works without a display.
3. For Win207 displays, physical output requires a matching WinUSB driver. Follow the [first-display guide](docs/first-display.md) to check the hardware ID and confirm still-image output.

## Display compatibility

| Display | USB VID:PID | Default output size |
| --- | --- | --- |
| 2.1-inch round | `1CBE:0021` | 480 × 480 |
| 2.1-inch round SLC | `1CBE:B021` | 480 × 480 |
| 4 inch | `1CBE:0040` | 720 × 720 |
| 4.5 inch | `1CBE:0045` | 1024 × 768 |
| 4.6 inch | `1CBE:0046` | 320 × 960 |
| 5 inch | `1CBE:0055` | 720 × 1280 |
| 5.2 inch | `1CBE:0050` | 720 × 1280 |
| 6.2 inch | `1CBE:A062` | 368 × 960 |
| 6.2 inch V2 | `1CBE:B062` | 448 × 1280 |
| 6.2 inch V3 | `1CBE:C062` | 480 × 1280 |
| 6.5 inch | `1CBE:A065` | 720 × 1472 |
| 6.5 inch 1568 | `1CBE:B065` | 720 × 1568 |
| 6.8 inch | `1CBE:0068` | 1080 × 2320 |
| 6.8 inch 2288 | `1CBE:A068` | 1080 × 2288 |
| 7 inch | `1CBE:0070` | 800 × 1280 |
| 8 inch | `1CBE:0080` | 800 × 1280 |
| 8.8 inch | `1CBE:0088` | 480 × 1920 |
| 9.2 inch | `1CBE:0092` | 464 × 1920 |
| 9.2 inch A092 | `1CBE:A092` | 464 × 1920 |
| 12.3 inch | `1CBE:0123` | 720 × 1920 |

All 35 Win207 PIDs are listed in the [compatibility record](docs/compatibility.md#supported-win207-models). The 3.5-inch serial display uses a `1A86:5722` CH340 serial interface; Studio renders `.data` theme files locally and ships three factory themes. See [supported serial displays](docs/compatibility.md#supported-serial-displays) and the [serial protocol](docs/serial-protocol.md).

## Build and run

Building from source requires Windows and the .NET 10 SDK. The command key is built in. Video transcoding requires FFmpeg with `libx264`; select it in **Media → FFmpeg**.

```powershell
dotnet build TURZX.WinUsbDisplay.slnx -c Release
dotnet run --project src/TURZX.WinUsbDisplay.Studio
```

CLI:

```powershell
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- list
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- image 0 .\artwork.jpg
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- video 0 .\motion.mp4 24 --loop
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- stop 0
```

## Features

- Foreground sources: local HTML, an HTTP(S) page, a Windows window, an image or an image slideshow. Clear the foreground source to play only a background video.
- HTML renders in a separate headless browser, preferring Chrome and falling back to Edge.
- The preview composites layers locally. Video is converted to audio-free Annex B H.264.
- Brightness and orientation are saved per Win207 display. New live captures default to 20 FPS.
- Interface and in-app documentation are available in Simplified Chinese, English, Japanese and Korean.
- **Advanced controls** provides firmware, storage and supply-voltage queries plus firmware clock controls, depending on the model. **Device commands** sends Win207 commands, shows replies and returns a display from Windows desktop mode.

### Headless input

`frames` reads frames from standard input: a 4-byte little-endian length followed by one RGBA PNG. Errors go to standard error.

```cmd
your-renderer.exe | dotnet run --project src/TURZX.WinUsbDisplay.Cli -- frames 0 8
```

## Example scenes

Choose **Render HTML live** and select a preset. Scenes render locally and their transparent regions can sit over a video background. Edit them under `src/TURZX.WinUsbDisplay.Studio/Scenes`.

| Preset | HTML |
| --- | --- |
| Pixel robot | default.html |
| Digital clock | clock.html |
| Neon trails | stars.html |
| Retro Pong | pong.html |
| Neon racing | race.html |
| Pixel fireworks | fireworks.html |

## .NET integration

Download `TURZX-WinUsbDisplay-SDK.zip` from the matching [release](https://github.com/xushouzun/TurzxOpenSource/releases). Reference `lib/TURZX.WinUsbDisplay.dll` for device control and add `TURZX.WinUsbDisplay.Media.dll` for media processing. See the [developer guide](docs/developer-guide.md).

## Troubleshooting

- **Win207 USB write timeout:** reconnect the display and make sure no other application is controlling it.
- **Win207 display not found:** install the matching WinUSB driver.

The project, including its Win207 implementation, uses the [MIT license](LICENSE).
