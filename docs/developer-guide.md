# TURZX WinUsbDisplay DLL developer guide

`TURZX.WinUsbDisplay.dll` is a Win207 device-control library for Windows targeting `net10.0`. It uses the SetupAPI and WinUSB that ship with Windows and has no NuGet package dependencies. The device needs its WinUSB driver installed.

## 1. Build and reference

Download `TURZX-WinUsbDisplay-SDK.zip` from the matching repository release. Extract it and run `dotnet run --project examples/SdkDiscover` from the archive root to enumerate devices. The `lib` directory includes the device and optional media DLLs, each with its XML API documentation. You can also build from source.

Build only the device-control DLL:

```powershell
dotnet build src/TURZX.WinUsbDisplay/TURZX.WinUsbDisplay.csproj -c Release
```

Copy the produced `TURZX.WinUsbDisplay.dll` into the consuming project's `lib` directory. You may also copy `TURZX.WinUsbDisplay.xml` from the same directory for IntelliSense. The consuming project only needs to reference this DLL to enumerate devices, read firmware and configure the display. The desktop app, CLI, FFmpeg and browser are all optional components.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="TURZX.WinUsbDisplay">
      <HintPath>lib/TURZX.WinUsbDisplay.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
```

For source development use a `ProjectReference` to `src/TURZX.WinUsbDisplay/TURZX.WinUsbDisplay.csproj`. Studio and the CLI both reference the device SDK; add the media library when you need media processing. The media library builds as `TURZX.WinUsbDisplay.Media.dll`, whose public types live in the `TURZX.WinUsbDisplay.Media` namespace. Reference both DLLs when using the media library.

## 2. First connection

The command key is built in; `DisplaySettings.ReadCommandKey()` returns a copy of it. `device.Open` also accepts a custom 8-byte key.

```csharp
using System.Security.Cryptography;
using TURZX.WinUsbDisplay;

var devices = DisplayDevice.Discover(DisplaySettings.InterfaceGuid);
for (int index = 0; index < devices.Count; index++)
{
    var model = devices[index].Model;
    Console.WriteLine($"{index}: {model.Name} {model.Width}x{model.Height}");
}
if (args.Length != 1 || !int.TryParse(args[0], out int target) || target < 0 || target >= devices.Count)
    return;

byte[] key = DisplaySettings.ReadCommandKey();
try
{
    using DisplaySession screen = devices[target].Open(key);
    Console.WriteLine(screen.ReadFirmwareVersion());
    screen.SetBrightness(60);
    screen.SetRotation(0);
    // Use a complete 8-bit RGBA PNG no larger than 950,000 bytes.
    screen.ShowImage(File.ReadAllBytes("artwork.png"));
}
finally
{
    CryptographicOperations.ZeroMemory(key);
}
```

Keep the current connection target in your application as `DisplayDevice.Path`; list indices are only valid for that enumeration. If the path is missing after re-enumerating, ask the user to reselect the device. If the driver registers a custom interface GUID, pass it directly to `Discover` or set `InterfaceGuid` in the Studio settings file.

## 3. API and inputs

| API | Purpose and inputs |
| --- | --- |
| `DisplayDevice.Discover(guid)` | enumerates known models, returning interface paths and native width/height. Returns an empty list when no device is found |
| `device.Open(key)` | opens a session. The argument is 8 bytes. The session holds its own copy of the key and zeroes it on dispose |
| `ReadFirmwareVersion()` | reads the firmware version string |
| `SetBrightness(percent)` | 0–100. The 12.3 inch automatically applies its brightness cap |
| `SetRotation(quarterTurns)` | 0–3 map to 0°, 90°, 180°, 270°. Adjust after stopping the current video |
| `ShowImage(bytes)` | pass a complete JPEG or 8-bit RGBA PNG no larger than 950,000 bytes. Stops streamed video and the clock layer before sending |
| `ShowOverlay(png)` | pass a complete 8-bit RGBA PNG no larger than 950,000 bytes. Keeps the H.264 background playing so the firmware can composite transparent regions |
| `PlayVideo(stream, fps, loop, cancellation, onStarted)` | a readable Annex B H.264 stream. Seeking is required for looping. The call blocks, so run it on a background thread. The optional callback fires after the first block's reply. If you cancel during a read, retry with a new stream because the original read may still be pending. |
| `SetFrameRate(fps)` | sets 1–60 frames/second on its own. `PlayVideo` sets the frame rate when transferring video |
| `ReadDeviceDirectory(directory, cancellation)` | returns a file-name list and `IsComplete`, which indicates whether the directory was read completely. A partial read may still return some entries. If the display fails to create the directory, the SDK throws `DeviceResponseException` with `Failure` set to `DirectoryCreationFailed` |
| `PlayDeviceFile(path)` / `DeleteDeviceFile(path)` | plays or deletes a file on the display. Use an ASCII path starting with `/`, up to 484 bytes. Query the directory again to confirm deletion because the reply has no uniform success code |
| `ReadStorageCapacity()` | returns total, used and free capacity for the SD card and internal flash, in KiB |
| `ReadSupplyVoltageMillivolts()` | reads the supply voltage in millivolts on compatible displays |
| `StopPlayback()` | stops host streaming, device-stored H.264 and the device JPEG loop. Cancel and await any running transfer first |
| `StopVideo()` | sends the streamed-video stop command. If a continuous send is running, cancel and await that task first |
| `StopLocalVideo()`, `StopImageLoop()` | stop video or image loops stored on the device |
| `ShowDefaultClock(enabled)` | sets the clock from host local time and shows it, or stops the clock |
| `SwitchToDesktop()` | sets Windows desktop mode for the next boot. The caller issues a separate restart (11) to apply it |
| `ExchangeCommand(command, arguments, payload)` | generic encrypted send/receive. Arguments start at plaintext header offset 8, up to 492 bytes. Payload starts at USB offset 512; the caller fills length and flags per the protocol. Returns the device reply bytes |
| `ExchangeRawCommand(command, arguments)` | generic unencrypted send/receive. The number is at offset 0, arguments from offset 1, up to 63 bytes. Returns the device reply bytes |
| `ReadDiagnostic(command, raw)` | reads an unparsed diagnostic reply. With `raw=false`, it uses the encrypted channel and accepts only commands 10, 17, 50, 60, 100, 112, 122 and 201. With `raw=true`, it uses the unencrypted channel and accepts only 0x60 and 0x62 |
| `PlayImage(image, fps, cancellation, onStarted, onFrameSent, onTransfer)` | repeats one image at 1–60 FPS until cancelled. Callbacks report startup, each sent frame and transfer timing |
| `ShowJpegFrame(jpeg, onTransfer)` | sends one JPEG frame for continuous output without a video background; optionally reports USB write and reply timing |
| `SetStartupConfiguration(brightness, startupMode, rotation, keepAwakeOffline, offlineClockRotation)` | writes the persistent startup configuration. Brightness 0–100, startup mode 0–2, orientation 0–3, offline clock orientation 0–3 |
| `RestartDevice()` | sends command 11 to restart the display |
| `ListDeviceFiles(directory)` | returns file names in a device directory without the completeness flag |
| `SynchronizeClock()` | synchronizes the device clock with the host time |
| `SetVideoClockSync(enabled)` | sets whether the device waits for video timing sync |
| `ReadVerifiedQueryResponse(command)` | queries and validates the firmware (10), voltage (50) or storage (100) reply |
| `ExchangeMatchingCommand(command, arguments, payload, minimumLength, cancellation)` | sends a command and reads a reply matching the number and minimum length |

Use `DesktopModeDevice.Discover()` to enumerate devices in desktop mode. Select by `Serial`, call `SwitchToMonitorAsync()` to send one HID return report, then use `DisplayDevice.Discover(DisplaySettings.InterfaceGuid)` to check that the WinUSB interface with the same serial number has returned. See “Windows desktop mode and USB display mode” in the protocol reference for the Studio workflow.

The device library consumes encoded images or video directly. Containers such as MP4 must first be converted to Annex B H.264; HTML pages and window captures must first be rendered to PNG or JPEG. Produce these bytes yourself, or refer to `VideoConverter`, `HtmlCapture`, `LiveScene` and `FrameInput` in `TURZX.WinUsbDisplay.Media`.

### HTML capture output rotation

`HtmlCapture.OpenAsync(source, model, cancellation, fastCapture, pixelScale, outputRotation)` rotates captured output during browser compositing. `model` still defines the logical viewport seen by page scripts. For example, a logical viewport of 1280 × 800 with `pixelScale=1` and `outputRotation=3` produces 800 × 1280 output.

| `outputRotation` | Output orientation |
| --- | --- |
| `0` | No rotation |
| `1` | 90° counterclockwise |
| `2` | 180° |
| `3` | 90° clockwise |

When `outputRotation` is 1 or 3, the output width and height are swapped; `pixelScale` still determines pixel density. Nonzero rotation supports local HTML only. The page loads inside an `iframe` that preserves its logical dimensions, and relative CSS, script and image URLs still resolve against the original HTML address. The page must support running inside an `iframe`. A remote HTTP(S) URL with nonzero output rotation throws `NotSupportedException`; ordinary remote capture with `outputRotation=0` is unchanged. This parameter does not send a device rotation command.

Rotation applies to screenshots and screencasts alike: `ReadFrameAsync` and `ReadPngScreencastAsync` return PNG; `ReadJpegFrameAsync` and `ReadJpegScreencastAsync` return JPEG. `ReadFrameAsync` and `ReadJpegFrameAsync` capture on demand; the two Screencast methods stream frames continuously.

## 4. Sessions and error handling

- Give a single display to only one process at a time. Release the session with `using` after connecting.
- USB calls are synchronous. GUIs should put device reads/writes on a worker thread. A live foreground can alternate with `PlayVideo` in the same session; the library serializes each request/reply pair.
- To stop looping video, cancel the `CancellationToken` and await the transfer's return before disposing the session or switching content. A USB request already started during cancellation waits for its reply or the endpoint timeout.
- When opening a device, inspect `DisplayOpenException.Failure` for a sharing conflict (`InUse`) or denied access (`AccessDenied`). After cancelling video transmission, a failed device stop command raises `PlaybackStopException`.
- WinUSB errors surface as `Win32Exception` including the operation and Windows error code. Short writes, empty replies or invalid replies may surface as `IOException`. On device disconnect, dispose the old session and re-enumerate; do not blindly replay blocks that were already delivered.
- Check the reply length before parsing diagnostics. A model or firmware that does not support a command may time out or return a different layout.

Command numbers, argument offsets, byte order and packet examples are in the [Win207 device command reference](win207-protocol.md).
