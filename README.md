# TURZX WinUSB Display Studio

[简体中文](README.zh-CN.md)

Explore TURZX displays and more at the [official TURZX website](https://www.turzx.com/).

Turn a TURZX Win207 USB screen into a desktop dashboard. Crop a webpage or window, layer it over a video, or loop a set of photos. Preview the result in Studio before sending it to the display.

**[Get the Windows preview](../../releases)** · **[Try the sample scenes](docs/scenes.md)** · **[Check hardware compatibility](docs/compatibility.md)** · **[Build with the .NET SDK](docs/developer-guide.en.md)**

| Digital clock | Neon star trails | Pixel fireworks |
| :---: | :---: | :---: |
| [![Digital clock on the 2.1-inch round display](docs/images/round-240-clock.gif)](docs/images/round-240-clock.gif) | [![Neon star trails on the 2.1-inch round display](docs/images/round-240-stars.gif)](docs/images/round-240-stars.gif) | [![Pixel fireworks on the 2.1-inch round display](docs/images/round-240-fireworks.gif)](docs/images/round-240-fireworks.gif) |

Looping GIF previews are rendered at the 2.1-inch round display’s 480 × 480 resolution and resized for this page.

## Quick start

1. Download the Windows x64 ZIP from Releases, extract it, and run `TURZX.WinUsbDisplay.Studio.exe`. The preview package includes the .NET runtime.
2. In **Media**, choose **Render HTML live** as the foreground source and select **Digital clock**. HTML preview works without a USB display and requires Chrome or Edge.
3. Physical output requires a matching WinUSB driver and a packet key from the firmware maintainer. Follow the [first-display guide](docs/first-display.md#english) to check the hardware ID and verify still-image output.

## Hardware tested

| Display | USB VID:PID | Default output size | Test results |
| --- | --- | --- | --- |
| 4.6 inch | `1CBE:0046` | 320 × 960 | `turzx_0001_0020`, Firmware query and Studio settings restore |
| 2.1-inch round | `1CBE:0021` | 480 × 480 | `turzx_0001_0023`, SDK enumeration, firmware query and device acknowledgements for 30 FPS H.264 blocks |
| 12.3 inch | `1CBE:0123` | 720 × 1920 | Firmware query and device acknowledgements for H.264 blocks and slideshow frames |
| 7 inch | `1CBE:0070` | 800 × 1280 | `turzx_0001_0017`, Studio still image, display switching and HTML over H.264 |
| 5 inch | `1CBE:0055` | 720 × 1280 | `turzx_0001_0029`, device acknowledgements for CLI image transfer and stop, plus writing, listing and deleting a test image in internal storage |

See the [compatibility records](docs/compatibility.md) for firmware, device acknowledgements and observed output. Older WCH, CDC and HID display protocols are out of scope.

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

Choose **Render HTML live** and select a preset from the sample scene dropdown. See the [list of built-in presets](docs/scenes.md). They render locally and their transparent regions can sit over a video background. Edit them under `src/TURZX.WinUsbDisplay.Studio/Scenes` or use them as a starting point for your own scenes.

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

### Commands and send/receive debugging

**Commands** lists the Win207 encrypted and raw-channel commands known from the original project, with their purpose and owning feature page. It can read firmware version, voltage, orientation, temperatures, speeds, storage and video-buffer replies, and can run the firmware clock, stop playback and switch desktop mode. Storage writes, cooling settings and factory commands are listed for reference.

The send/receive panel on the **Commands** page supports encrypted and raw-channel requests. Enter a command number and hexadecimal arguments to read the raw reply. **Docs** renders the Markdown with search and copyable call examples.

## .NET integration

Download `TURZX-WinUsbDisplay-SDK.zip` from the matching [release](../../releases). Reference `lib/TURZX.WinUsbDisplay.dll` for device control and add `TURZX.WinUsbDisplay.Media.dll` for media processing. The archive includes API documentation and a discovery example. See the [developer guide](docs/developer-guide.en.md) for source builds and API usage.

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
- `src/TURZX.WinUsbDisplay.Windows` shares WPF frame processing, window capture and Windows key storage between Studio and Probe. The SDK archive contains the device and Media libraries.
- `src/TURZX.WinUsbDisplay.Cli` provides the command-line entry point.
- `src/TURZX.WinUsbDisplay.Studio` provides the WPF desktop UI.
- `examples/SdkDiscover` demonstrates device enumeration and builds against source in the repository or against the DLL in the SDK archive.
- `tests/TURZX.WinUsbDisplay.Tests` provides offline unit tests for the pure logic.
- `docs` records the protocol so other languages can implement it.

The project, including its Win207 implementation, uses the [MIT license](LICENSE).

## Image-send probe

By default the probe queries firmware. With `--send` it shows a test pattern on the display with the given PID, or sends a given image. The probe prints each request's reply length, first byte and elapsed time. Device commands use `WINUSB_DISPLAY_COMMAND_KEY` when set, with Studio's saved key as the fallback.

`--capture` measures HTML capture and encoding without a USB display. The PID selects the target dimensions. `WINUSB_DISPLAY_PROBE_ROTATION` applies to host-side frame processing. Set the device to 0° before visually checking host rotation on the panel. Probe leaves the device orientation unchanged.

```powershell
dotnet run --project tools/TURZX.WinUsbDisplay.Probe -- 0046
dotnet run --project tools/TURZX.WinUsbDisplay.Probe -- 0046 .\artwork.png --send
```
