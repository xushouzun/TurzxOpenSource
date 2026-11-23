# TURZX WinUSB Display Studio

[简体中文](README.zh-CN.md)

Explore TURZX displays and more at the [official TURZX website](https://www.turzx.com/).

Turn a TURZX Win207 USB screen into a desktop dashboard. Crop a webpage or window, layer it over a video, or loop a set of photos. Preview the result in Studio before sending it to the display.

**[Download for Windows](./releases)** · **[Try the sample scenes](docs/scenes.en.md)** · **[Check hardware compatibility](docs/compatibility.en.md)** · **[Build with the .NET SDK](docs/developer-guide.en.md)**

| Digital clock | Neon star trails | Pixel fireworks |
| :---: | :---: | :---: |
| [![Digital clock on the 2.1-inch round display](docs/images/round-240-clock.gif)](docs/images/round-240-clock.gif) | [![Neon star trails on the 2.1-inch round display](docs/images/round-240-stars.gif)](docs/images/round-240-stars.gif) | [![Pixel fireworks on the 2.1-inch round display](docs/images/round-240-fireworks.gif)](docs/images/round-240-fireworks.gif) |

Looping GIF previews are rendered at the 2.1-inch round display’s 480 × 480 resolution and resized for this page.

## Quick start

1. Download the Windows x64 ZIP from Releases, extract it, and run `TURZX.WinUsbDisplay.Studio.exe`. The package includes the .NET runtime.
2. In **Media**, choose **Render HTML live** as the foreground source and select **Digital clock**. HTML preview works without a USB display and requires Chrome or Edge.
3. Physical output requires a matching WinUSB driver and a packet key from the firmware maintainer. Follow the [first-display guide](docs/first-display.en.md) to check the hardware ID and verify still-image output.

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

The table above lists common models. All 35 Win207 PIDs (including 1.6/1.8/2.8/2.88/3.4/3.5/3.92/10/11.3/12.4/12.68/14.5-inch variants) are listed in the [compatibility records](docs/compatibility.en.md#supported-win207-models).

The 3.5-inch native-theme display uses a `1A86:5722` CH340 serial interface and is not part of the WinUSB table above. Studio shows only the native theme source, renders JSON `.data` locally and sends the frame over serial. Three IP-neutral factory themes are bundled. See the [compatibility records](docs/compatibility.en.md#native-theme-devices).

See the [compatibility records](docs/compatibility.en.md) for model variants, orientation and command differences. Older WCH, CDC and HID display protocols are out of scope, as are the 6.2-inch legacy WinLcd model `1CBE:0062` and the legacy 4-inch `1D6B:A040`.

## Running

Building from source requires Windows and the .NET 10 SDK. Video transcoding requires FFmpeg with `libx264`. Use **Media → FFmpeg**, add FFmpeg to PATH, or set `WINUSB_DISPLAY_FFMPEG`. The built-in HTML presets work without FFmpeg.

Set `WINUSB_DISPLAY_COMMAND_KEY` to the 16 hexadecimal characters supplied by the firmware maintainer before launching Studio or the CLI from that PowerShell session. Studio can also read a key saved by an older version when the variable is unset. SDK callers can pass the 8 bytes directly. If the WinUSB driver uses a custom interface GUID, set `WINUSB_DISPLAY_INTERFACE_GUID`. [Win207 protocol reference](docs/win207-protocol.en.md)

```powershell
dotnet build TURZX.WinUsbDisplay.slnx -c Release
dotnet run --project src/TURZX.WinUsbDisplay.Studio
```

Studio automatically refreshes the display dropdown. Select a display, use **Browse** to open a media file, then click **Start playback**. Studio fits or fills the native display canvas with JPEG, PNG and common video formats and applies the position offsets from the preview controls. Video is converted to audio-free Annex B H.264. The 4.6-inch display uses its native 320 × 960 raster for preview and transmission. Studio also transcodes raw `.h264` and `.264` inputs to apply the selected display layout. The CLI `video` command sends those files directly. For CLI playback, use Annex B, match the display raster, and pass the source frame rate.

```powershell
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- list
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- info 0
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- image 0 .\artwork.jpg
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- video 0 .\motion.mp4 24 --loop
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- stop 0
```

Brightness and orientation default to 66% and 0°. Studio saves brightness and orientation per device. Brightness changes are sent automatically after a short delay. Orientation updates the preview immediately and takes effect on the device when you start output. Connecting or restoring saved values only updates the UI. Stop video before changing orientation. Use the top bar to switch between Simplified Chinese, English, Japanese, and Korean. Studio saves your language preference and displays the in-app documentation in the selected language. **Stop playback** cancels the current output. When idle, Studio can also stop video and image loops stored on the device. **Advanced controls** reads firmware and storage on demand, shows supply voltage for the 12.3-inch display, and offers firmware clock controls for the 4.6- and 12.3-inch displays. **Commands** provides protocol diagnostics and command send/receive.

In the CLI, use Ctrl+C to end a looping transfer or `stop` to stop device-stored playback while the session is idle.

## Screen preview

The left side of the Media page previews images, video and live foreground sources. The preview composites the selected layers locally. During slideshow playback, it displays the full-resolution composition once the device acknowledges the frame. During file-based video output, it plays an FFmpeg remux of the H.264 stream sent to the display.

Open **Docs** to search by number or command name and copy selections and call examples.

- DLL developer guide · [简体中文](docs/developer-guide.md) · [English](docs/developer-guide.en.md) · [日本語](docs/developer-guide.ja.md) · [한국어](docs/developer-guide.ko.md)
- Win207 protocol reference · [简体中文](docs/win207-protocol.md) · [English](docs/win207-protocol.en.md) · [日本語](docs/win207-protocol.ja.md) · [한국어](docs/win207-protocol.ko.md)

## Example scenes

Choose **Render HTML live** and select a preset from the sample scene dropdown. See the [list of built-in presets](docs/scenes.en.md). They render locally and their transparent regions can sit over a video background. Edit them under `src/TURZX.WinUsbDisplay.Studio/Scenes` or use them as a starting point for your own scenes.

## Live input and video background

In the **Foreground source** dropdown, choose local HTML, an HTTP(S) page, a Windows window, a media file, or an image slideshow. Clear the foreground source to play only a background video. HTML renders in a separate headless browser, preferring Chrome and falling back to Edge. Set `WINUSB_DISPLAY_BROWSER` to select the browser executable. The render size depends on the display and playback mode. Use transparent regions in a page or PNG to reveal the background. In Studio, minimizing the captured window holds its last frame until the window is restored. Some protected or hardware-accelerated windows may return a blank frame.

Both the desktop app and the headless CLI can loop a local video background. Compositing relies on the firmware's H.264 and PNG layer support. Adjust the capture frame rate to fit USB bandwidth. After leaving live output the device may keep showing the last foreground PNG.

Studio targets 20 FPS for new live capture configurations. On compatible devices, FFmpeg can encode an uncropped web page or window with the default layout and no background into H.264 for block transfer. Actual frame rates depend on the foreground content, firmware and USB acknowledgements. Slideshows change images at the configured interval.

### Headless programs

Any program can write binary frames to the CLI's standard input. Each frame is a 4-byte unsigned little-endian length followed by one complete 8-bit RGBA PNG. Prefer PNGs at the display size. Both `frames` and `image` accept up to 950,000 bytes per image, and `image` also accepts JPEG. The generator's standard output carries only frame data. Write logs to standard error. EOF or Ctrl+C ends the session.

```cmd
your-renderer.exe | dotnet run --project src/TURZX.WinUsbDisplay.Cli -- frames 0 8
your-renderer.exe | dotnet run --project src/TURZX.WinUsbDisplay.Cli -- frames 0 8 background.mp4 24
```

For example a Python generator can use `sys.stdout.buffer.write(struct.pack("<I", len(png)) + png)` and call `sys.stdout.buffer.flush()` after each frame.

### Device commands and protocol reference

**Device commands** shows Win207 presets, their parameters and the device reply. The console accepts decimal or hexadecimal input and supports direct typing of device paths and local file selection for applicable commands. Availability depends on the display model. **Documentation** includes the full protocol reference, a search field and copyable examples.

On a supported display, send command `150` to set Windows desktop mode, then send `11` separately to restart. To resume Studio output, select **Return to USB display** in the **Device commands** list. In the right pane, refresh the desktop-mode device list, select the display by serial number and click **Return to USB display**. See the [Win207 protocol reference](docs/win207-protocol.en.md) for the full workflow and model restrictions.

## .NET integration

Download `TURZX-WinUsbDisplay-SDK.zip` from the matching [release](./releases). Reference `lib/TURZX.WinUsbDisplay.dll` for device control and add `TURZX.WinUsbDisplay.Media.dll` for media processing. The archive includes API documentation and a discovery example. See the [developer guide](docs/developer-guide.en.md) for source builds and API usage.

## Troubleshooting

Studio and CLI share a per-display session lock within the same Windows user profile. Apps using a different driver or SDK are subject to Windows sharing rules.

SDK callers can catch `DisplayOpenException` and inspect `Failure` and `NativeErrorCode`. The CLI returns **2** for a display sharing violation and **3** when Windows denies access. Other failures return **1**, and cancellation returns **130**.

**Window is completely black:** a few GPU drivers present WPF content as a black window. Set the environment variable `WINUSB_DISPLAY_SOFTWARE_RENDER` to `1` and restart Studio to force software rendering for this process, without touching the registry:

```powershell
$env:WINUSB_DISPLAY_SOFTWARE_RENDER = "1"
dotnet run --project src/TURZX.WinUsbDisplay.Studio
```

This is equivalent to the registry value `HKCU\Software\Microsoft\Avalon.Graphics\DisableHWAcceleration = 1` but affects only this process; set it to `0` or remove it to restore hardware acceleration.

**USB write timeout:** during live compositing (PNG foreground + video background) the firmware pauses briefly. Studio already widens the endpoint timeouts (10 s write, 5 s read) to avoid aborting transfers. If it still times out, reseat that display's USB cable and make sure no other process is controlling it.

**Device not found:** confirm the WinUSB driver for the device is installed; if the driver uses a custom interface GUID, set `WINUSB_DISPLAY_INTERFACE_GUID`.

## Layout

Sources and asset notices are in [NOTICE](NOTICE.md). See [CONTRIBUTING](CONTRIBUTING.md) to report an issue or share a device result, and [SECURITY](SECURITY.md) to report a vulnerability privately.

- `src/TURZX.WinUsbDisplay` provides device enumeration, WinUSB transport and the Win207 device-control SDK.
- `src/TURZX.WinUsbDisplay.Media` provides optional HTML capture, video transcoding and headless frame input.
- `src/TURZX.WinUsbDisplay.Windows` provides WPF frame processing, window capture and Windows key storage. The SDK archive contains the device and Media libraries.
- `src/TURZX.WinUsbDisplay.Cli` provides the command-line entry point.
- `src/TURZX.WinUsbDisplay.Studio` provides the WPF desktop UI.
- `examples/SdkDiscover` demonstrates device enumeration and builds against source in the repository or against the DLL in the SDK archive.
- `tests/TURZX.WinUsbDisplay.Tests` provides offline unit tests for the pure logic.
- `docs` records the protocol so other languages can implement it.

The project, including its Win207 implementation, uses the [MIT license](LICENSE).
