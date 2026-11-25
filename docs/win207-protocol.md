# TURZX Win207 device command reference

This reference describes the Win207 host protocol implemented by this project. Command support depends on the model and firmware. Command names use upper snake case: `GET_` for reads, `SET_` for configuration, and `WRITE_` for transfers. The device identifies requests by command number and channel.

Offsets are zero-based and ranges are inclusive. `u8` is a 1-byte unsigned integer; `u16be`, `u32be` are 2- and 4-byte big-endian unsigned integers; `u32le` is a 4-byte little-endian unsigned integer. "Header" means the 500-byte plaintext header before encryption; "reply" means the raw bytes read from the IN endpoint. Encrypted channel numbers are decimal; raw channel numbers are hexadecimal.

## Connection and calling

- Windows enumerates by the interface GUID registered by the device driver. The default GUID is `DEE824EF-729B-4A0E-9C14-B7117D33A817`. For a custom driver, set `InterfaceGuid` in the Studio settings file or pass `--interface-guid` to the CLI.
- The WinUSB OUT endpoint is `0x01` and the IN endpoint is `0x81`. Send one request then read its reply to avoid mismatches. Studio's OUT write timeout is 10000 ms, the IN read timeout is 5000 ms, and the read buffer is 512 bytes. The firmware briefly pauses while compositing large live payloads such as PNGs.
- The 8-byte packet key is built in. Studio, the CLI and the SDK require no extra configuration; the SDK can still accept a custom 8-byte key.
- Check the reply length before parsing a reply. For structured firmware, storage and supply replies, Studio also verifies that byte 0 echoes the request number, so a stale reply from another command is not parsed. A reply does not prove successful execution; some commands add status fields.

## Encrypted channel packet

| Position | Content |
| --- | --- |
| plaintext 0 | command number |
| plaintext 1 | reserved, 0 |
| plaintext 2–3 | fixed marker `1A 6D` |
| plaintext 4–7 | Low 32 bits of the milliseconds from local midnight at the start of the previous day to the current UTC time, captured when the session is created. Little-endian |
| plaintext 8–499 | command arguments, remainder zero-filled |
| USB request 0–503 | DES-CBC encryption of the 500 plaintext bytes with PKCS#7 padding. The IV and key are both the firmware key |
| USB request 504–509 | six zero bytes |
| USB request 510–511 | fixed tail `A1 1A` |
| USB request 512 onward | optional raw payload. Media payloads are not part of the header encryption |

DES-CBC is the compatibility format of existing firmware. Device replies stay plaintext. Commands 101, 102 and 121 store the payload byte count (big-endian) at plaintext 8–11. Single-byte control arguments generally go at plaintext 8.

## Generic send/receive interface

In **Device commands**, header arguments and payloads support decimal and hexadecimal input. Ordinary decimal input is a space-separated sequence of bytes (0–255). For 101/102, the decimal header value is the full payload length; for 121, enter `block-length final-flag`. These lengths are encoded big-endian.

Commands 38/42/98/99/110/113 take an absolute ASCII device path; the length field and reserved bytes are filled in automatically. Changing the input format clears that field to avoid misinterpretation. Unlisted commands follow their own definitions. For command 39, enter `buffer-capacity valid-length final-flag`; for 51, enter `year month day hour minute second mode`.

Commands 101 (JPEG), 102 (PNG) and 40 (small-file write) support browsing for a local file. Studio fills in byte lengths; 40 also requires an absolute ASCII destination path on the device and a file under 100 KiB. Selecting a file prepares the request; use **Send** to transmit it.

For command 121, **Browse H.264** selects a `.h264` or `.264` background video. Studio stays on the Commands page; open **Media** to start playback. Clear the foreground for video-only playback. Studio handles stream blocks and queue pacing.

Command 39 also supports a local file, but create the destination with 38 first. Choose a nonempty file of up to 1 MiB; Studio pads the buffer to 1 MiB, fills in the valid length and marks it as final. 40 requires an existing target directory; check the written file size with 98.

Files already on the device are available under **Advanced controls → On-device media**. Local JPEG and PNG files must contain 1 to 950,000 bytes.

Stop Studio playback on the selected device before sending. For encrypted commands other than `99`, the console checks that the reply echoes the command number and meets the minimum length, then reads past stale replies without resending. Command `99` displays a single text fragment. Refresh the full directory under **Advanced controls → On-device media** and check whether the listing is complete.

In Studio **Device commands**, select a command from the list on the left; the right pane shows the inputs that apply to it. Supported commands with no parameters can be sent directly. Scroll the form when it has more fields than fit; the **Send** button and response area stay below it.

You can also edit the command number or channel on the right. The list selects the matching preset, if available, and the right pane updates its description and form. Editing the number or channel clears the previous command's parameters and attached file. Use decimal or `0x`-prefixed hexadecimal numbers. Encrypted and raw channel numbers are independent; for example, encrypted decimal `60` queries orientation while raw hex `0x60` queries temperature.

| Input or return | Encrypted Win207 | Raw channel |
| --- | --- | --- |
| command number | 0–255, written at plaintext header offset 0 | 0–255, written at request offset 0 |
| arguments | 0–492 bytes from plaintext header offset 8, remainder zero-filled | 0–63 bytes from offset 1, remainder zero-filled |
| payload | optional, appended from USB offset 512. Fill length, flags and other header fields per the command table; Studio does not infer them | no separate payload; request is fixed 64 bytes |
| return | all bytes returned by the IN endpoint, zero bytes preserved, not padded to 512 | same |
| no reply or transfer failure | empty reply or short write reported as an I/O error. Driver errors include the operation name and Windows error code | same |

The reply view shows byte offsets and byte values in hexadecimal, followed by printable ASCII. Other characters appear as dots. Refer to each command for its reply number, field offsets and status values. Check the relevant device status or display output to confirm the operation.

| Example | Channel and number | Arguments | Read-back |
| --- | --- | --- | --- |
| query firmware | encrypted 10 | empty | check reply length ≥ 40, offset 0 = 10, 8–39 = UTF-8 version text |
| query H.264 block size | encrypted 17 | empty | reply ≥ 12 bytes, 8–11 = big-endian block length, 0 means default |
| set brightness to device value 60 | encrypted 14 | `3C` | No common success code is specified. Use SDK `SetBrightness` to apply the model brightness cap |
| query cooling temperature | raw 0x60 (SDK) | empty | reply ≥ 3 bytes, 1 and 2 = pump and fan temperature in °C |

```csharp
byte[] firmware = screen.ExchangeCommand(10);
byte[] temperatures = screen.ExchangeRawCommand(0x60);
Console.WriteLine(Convert.ToHexString(firmware));
Console.WriteLine(Convert.ToHexString(temperatures));
```

Examples generated on the **Code** tab check the model and request ranges before opening the display; the delete example requires typing `DELETE` to send.

## Windows desktop mode and USB display mode

On a supported display, send `150` in **Device commands**. Command `150` sets the mode for the next boot; after the device acknowledges it, Studio offers to restart immediately, and you can also send `11` separately. After the restart into Windows desktop mode, the WinUSB interface disappears and the desktop-mode HID interface (`1A86:AD11`) appears.

To resume sending images and video from Studio, select `SWITCH_TO_MONITOR_MODE` immediately below command `150` in the **Device commands** list. This HID action takes no Win207 command number or parameters. Its detail pane works even when the Studio USB display list is empty. Refresh the desktop-mode devices, choose the target by serial number and click **Return to USB display** in that pane. Studio sends one HID report and waits up to 25 seconds for the WinUSB interface with the same serial number to reappear.

Windows needs the bundled TURZX display driver to use the panel as a monitor in desktop mode. If it is missing, open **Advanced controls** and click **Install desktop-mode driver** in the desktop-mode driver section, then approve the administrator prompt. Installation is required once per machine. To remove the driver, click **Uninstall desktop-mode driver** in the same section.

For the 12.3-inch display (`1CBE:0123`), the command console disables the raw channel and encrypted commands `12`, `51`, `52`, `61`, `62`, `125`, `251` and `252`. To turn off the backlight, send `14` with decimal `0`. In the command console, `14` takes a device value from 0 to 80 and `13` accepts `0`. Use the **Media** page for other orientations so Studio rotates the frames.

The standard round panel (PID `0021`) does not offer Windows desktop mode. Raw LED, temperature and speed commands belong to the standalone `0416:8061` and `0416:8052` controllers; Studio does not send them to displays. Encrypted IDs 61 and 62 are for protocol reference.

## Basic control and queries

| Number | Name | Arguments and reply | Studio entry |
| --- | --- | --- | --- |
| 10 | GET_FIRMWARE_VERSION | Reply is at least 40 bytes. Offset 0 should be 10; 8–39 = UTF-8 firmware version, trailing NUL stripped | Advanced controls · Device commands |
| 11 | RESTART_DEVICE | Zero-filled header arguments and a 512-byte request. Restart interrupts the USB session. No dedicated success code is specified | Device commands |
| 12 | SHUT_DOWN_DEVICE | Zero-filled header arguments and a 512-byte request. Requests device shutdown. Behavior depends on firmware. No dedicated success code is specified | Device commands |
| 13 | SET_DISPLAY_ORIENTATION | Header 8 = u8, 0–3 map to 0°, 90°, 180°, 270°; remaining arguments zero. Studio combines host-side frame rotation with firmware orientation according to the model. Check the result on the physical panel. Command 60 may fail to reflect the setting on some firmware | Media · orientation |
| 14 | SET_DISPLAY_BRIGHTNESS | Header 8 holds a u8 in the range 0–100. Studio converts percentages to floor(percent × 80 ÷ 100) for the 12.3-inch display. No common success code is specified | Media · display controls |
| 15 | SET_VIDEO_FRAME_RATE | Header 8 = u8, frames/second; Studio accepts 1–60. Set before writing the first H.264 block | Media, live input |
| 49 | SET_VIDEO_CLOCK_SYNC | Header 8 = u8; 0 disables video clock sync, 1 enables. Date/time is set with 51 | Device commands |
| 50 | GET_SUPPLY_VOLTAGE | No request arguments. Reply is at least 12 bytes. Offsets 8–11 contain a u32be supply reading in mV on compatible 12.3-inch firmware | Advanced controls · Device commands |
| 51 | SET_DEVICE_CLOCK | Header 8–9 holds the year as u16be. Bytes 10–15 hold month, day, hour, minute, second and mode as u8 values. Mode 0 disables the clock, 1 enables it, and 2 sets the time only. Reply byte 0 echoes 51. The SDK uses local host time | Advanced controls (4.6-inch display) · Device commands |
| 52 | STOP_DEVICE_CLOCK | Header 8 is 0. Stops the device clock layer. No common success code is specified | Advanced controls (4.6-inch display) · Device commands |
| 60 | GET_ORIENTATION_STATE | No request arguments. Reply ≥ 10 bytes, offset 8 = current orientation, offset 9 = sensor orientation, each u8. Raw channel 0x60 queries temperature. Some firmware returns zero for both fields. The reply alone does not confirm a manual orientation setting | Commands |
| 61 | SET_PUMP_SPEED | Encrypted pump speed setting. Arguments and reply layout are unspecified. Use raw 0x61 for compatible pump and fan controls | reference |
| 62 | GET_PUMP_SPEED | Encrypted pump speed query. The reply layout is unspecified. Use raw 0x62 for compatible pump and fan speed queries | reference |
| 125 | SET_DEVICE_CONFIGURATION | Header 8–14 holds brightness, boot mode, 0, orientation, sleep delay, offline mode and offline clock orientation as u8 values. Mode values and delay units depend on firmware. Studio enables manual entry in the command console for the 4.6-inch model. Brightness is 0–100, startup mode 0–2, orientation 0–3, and offline mode 0–1. Set the remaining fields to 0. For the 12.3-inch model, use Advanced controls, which keeps firmware orientation at 0° | Advanced controls (4.6-inch display) · Device commands |
| 150 | SWITCH_TO_DESKTOP_MODE | Header arguments all zero. Sets Windows desktop mode for the next boot. Studio offers to restart immediately; you can also send 11 separately. Model support varies | Device commands |
| 201 | GET_DEVICE_POSITION_INDEX | Zero-filled header arguments. Supported SLC models return a u16be pump reading at reply offsets 10–11 | Commands |

## Images, video and layers

| Number | Name | Arguments and reply | Studio entry |
| --- | --- | --- | --- |
| 17 | GET_H264_BLOCK_SIZE | No request arguments. Reply ≥ 12 bytes, 8–11 = u32be byte count. 0 uses 198 KiB; Studio rejects block sizes above 4 MiB | Commands |
| 101 | DISPLAY_JPEG | Header 8–11 = u32be byte count, complete JPEG from USB offset 512. Reply status code not uniformly defined | Media |
| 102 | DISPLAY_PNG_OVERLAY | Header 8–11 = u32be byte count, complete 8-bit RGBA PNG from USB offset 512. Alpha is composited by firmware. Live PNGs may alternate with 121. Studio validates PNG chunk checksums and compressed pixel data before sending from the command console | Media, live input |
| 110 | START_LOCAL_H264_PLAYBACK | Header 8–11 holds the path length as u32be. The ASCII path starts at offset 16. The SDK first checks offset 8 of the matching command 110 reply for status 200. Some firmware returns the status at offset 1 instead (the reply starts with `6E C8`); when offset 8 is zero in that form, the SDK queries 112 and requires a non-zero playback state | Advanced controls · on-device media |
| 111 | STOP_LOCAL_H264_PLAYBACK | Header arguments all zero; stops H.264 playback from device storage. Host streaming stops with 123 | Commands |
| 112 | GET_LOCAL_H264_PLAYBACK_STATE | No request arguments. Reply ≥ 9 bytes, 8 = u8; 0 = stopped, non-zero = playing | Commands |
| 113 | DISPLAY_LOCAL_JPEG | Uses the path layout of 110. SDK on-device image playback expects a matching command 113 reply with status 200 at offset 1 | Advanced controls · on-device media |
| 114 | STOP_LOCAL_JPEG_LOOP | Header arguments all zero; stops the device JPEG loop. Firmware defines no common layer-clear reply | Commands |
| 121 | WRITE_H264_STREAM_BLOCK | Header 8–11 = block length u32be, 12 = 1 on the final block else 0. Annex B data from USB offset 512. Reply ≥ 9 bytes, 8 = queue depth u8 | Media, live input |
| 122 | GET_H264_QUEUE_DEPTH | Normal query, header arguments all zero. Reply ≥ 9 bytes, 8 = queue depth u8. Sync extension below | Commands |
| 123 | STOP_H264_STREAM | Header arguments all zero. Sent after the host stops sending, so later blocks do not restart video | Media, Commands |

An ordinary video transfer proceeds in this order: stop existing playback and the clock layer (123, 52), set the frame rate (15), read the block size (17), wait for the queue depth to reach 1 or less, then send Annex B data block by block (121). File input waits about 30 ms between blocks. Live input sends smaller blocks and briefly waits to merge frames, reducing per-block reply overhead. When the queue depth exceeds 3, the SDK waits for it to fall to 2 or less before continuing. After the host stops sending, the display keeps playing the blocks already received.

When the display supports layer composition, 102 (PNG foreground) and 121 (H.264 background) can alternate in one session, completing one request/reply pair before the next. The optional 122 synchronization extension stores a big-endian 64-bit sync time in milliseconds at header 8–15 and a big-endian 32-bit total frame count at 16–19. Studio uses the plain query only and does not use this extension.

## Files, lighting and manufacturing checks

| Number | Name | Arguments or known use | Studio entry |
| --- | --- | --- | --- |
| 38 | CREATE_DEVICE_FILE | Uses the path layout of 110. File creation status 200 is at reply offset 8 | Device commands |
| 39 | WRITE_DEVICE_FILE_BLOCK | Header 8–11 holds buffer capacity as u32be, 12–15 holds valid length as u32be, and 16 holds the final-block flag as u8. Studio's file picker uses a 1 MiB buffer and supplies the valid length of the final block | Device commands |
| 40 | WRITE_DEVICE_FILE | Header 8–11 holds the path length as u32be, 12–15 holds the file length as u32be, and the ASCII path starts at 16. File contents start at USB offset 512. Studio accepts files under 100 KiB | Device commands |
| 42 | DELETE_DEVICE_PATH | Deletes a file or directory. Header 8–11 holds the path length as u32be and the ASCII path starts at 16. Studio asks you to confirm the target path and rejects paths ending in a slash, including `/`. Refresh the directory to confirm removal. The SDK checks the command number in the deletion reply | Advanced controls · on-device media |
| 98 | GET_DEVICE_FILE_SIZE | Header 8–11 holds the path length as u32be, and the ASCII path starts at 16. Reply is at least 12 bytes. Offsets 8–11 hold the file size in bytes as u32le | Device commands |
| 99 | LIST_DEVICE_DIRECTORY | Uses the path layout of 98. The SDK reads up to 20 replies and extracts slash-separated names after `file:` from the UTF-8 listing. The SDK checks the directory when the reply echoes it. Some 12.3-inch firmware starts the listing with `result:dir:file:` and omits the path. Reading stops when the listing is complete or the device reports an empty directory. `result:dir:file:` indicates an existing empty directory, `nodir-createdone` indicates a newly created empty directory, and `result:nodir-createfailed` indicates creation failed | Advanced controls · on-device media |
| 100 | GET_STORAGE_CAPACITY | Reply is at least 32 bytes. Offsets 8, 12 and 16 = SD total, used and free; offsets 20, 24 and 28 = Flash equivalents. Fields are little-endian 32-bit, in KiB | Advanced controls · Device commands |
| 246 | TEST_USB_THROUGHPUT | USB write throughput check. Known requests use a zero-filled buffer; other fields are unspecified | protocol reference only; no Studio preset |
| 251 | SYNC_PUMP_AND_FAN_OUTPUTS | A 16-byte argument block starts at plaintext offset 8. Within the block, offsets 0–2 are FF 0F A1, offset 3 is 0, 4–5 hold the pump value as u16be, 6–8 hold three fan values, and 9–15 are zero | Device commands |
| 252 | WRITE_RGB_ANIMATION | Header 8–11 zero, 12–15 = payload length u32be. USB offset 512–513 = total frames u16be, 514 = frame interval u8, animation data from 515. Interval unit and frame encoding are firmware-defined | Device commands |
| 253 | RUN_FACTORY_H264_TEST | Manufacturing H.264 check. The request argument layout is unspecified | protocol reference only; no Studio preset |

## Raw channel

The raw channel uses the same endpoints but the request is a 64-byte buffer without a DES packet. The number goes at request 0.

| Number | Name | Request and reply | Studio entry |
| --- | --- | --- | --- |
| 0x11 | WRITE_LED_PIXEL_BLOCK | Request byte 1 holds the LED start index as u8, followed by RGB data from offset 4. A compatible block layout uses start indices 0, 15 and 30 with up to 45, 45 and 15 data bytes respectively. Unused bytes are zero | reference |
| 0x60 | GET_COOLING_TEMPERATURES | Request 1–63 zero-filled. Reply ≥ 3 bytes, 1 and 2 = pump and fan temperature u8 in °C | reference |
| 0x61 | SET_COOLING_SPEEDS | Request offsets 1–2 and 3–4 hold pump and fan targets as u16be. Offsets 5–63 are zero. The compatible scale uses 3500 as the full-scale raw value | reference |
| 0x62 | GET_COOLING_SPEEDS | Request 1–63 zero-filled. Reply ≥ 5 bytes, 1–2 and 3–4 = pump and fan speed u16be. Conversion is model-dependent | reference |

## Packet and reply examples

Below are pre-encryption headers with the timestamp fixed at 1 ms. Each plaintext header is zero-padded to 500 bytes, then encrypted as described in “Encrypted channel packet”.

| Operation | Plaintext header leading bytes | Explanation |
| --- | --- | --- |
| read version | `0A 00 1A 6D 01 00 00 00` | decimal 10 = 0A, arguments all 0 |
| set brightness 60 | `0E 00 1A 6D 01 00 00 00 3C` | offset 8 = 3C = decimal 60 |
| rotate 180° | `0D 00 1A 6D 01 00 00 00 02` | offset 8 = orientation code 2 |
| send 1024-byte PNG | `66 00 1A 6D 01 00 00 00 00 00 04 00` | length written big-endian; USB request totals 1536 bytes |
| send 512-byte final H.264 block | `79 00 1A 6D 01 00 00 00 00 00 02 00 01` | offset 12 = final-block flag |

Reply decoding examples:

- When 17 reply 8–11 = `00 03 18 00`, the H.264 block length is 202752 bytes = 198 KiB.
- When 100 reply 8–11 = `00 10 00 00`, the SD total field is 4096 KiB. This field is little-endian.
- A raw temperature query sends `60` plus 63 zero bytes. If reply offsets 1, 2 = `23 28`, that is 35 °C and 40 °C.
- A raw speed reply with offsets 1–4 = `06 D6 03 E8` corresponds to raw values 1750 and 1000.

## Directly callable examples

Run these CLI commands from the source repository root. Use `list` first to get device indices. To enumerate devices from the SDK ZIP, run `dotnet run --project examples/SdkDiscover`.

```powershell
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- list
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- info 0
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- brightness 0 60
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- image 0 artwork.png
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- video 0 motion.mp4 24 --loop
dotnet run --project src/TURZX.WinUsbDisplay.Cli -- stop 0
```

The SDK can call device methods within one session. Diagnostics return raw replies; interpret them with the tables above.

```csharp
using TURZX.WinUsbDisplay;

var devices = DisplayDevice.Discover(DisplaySettings.InterfaceGuid);
if (devices.Count == 0)
    throw new InvalidOperationException("No Win207 display found.");
using var screen = devices[0].Open(DisplaySettings.ReadCommandKey());
Console.WriteLine(screen.ReadFirmwareVersion());
screen.SetRotation(2);
byte[] status = screen.ReadDiagnostic(60);
if (status.Length >= 10)
    Console.WriteLine($"orientation {status[8]}, sensor {status[9]}");
byte[] temperatures = screen.ReadDiagnostic(0x60, raw: true);
```

References: [Microsoft WinUSB guide](https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/using-winusb-api-to-communicate-with-a-usb-device), [FFmpeg format documentation](https://ffmpeg.org/ffmpeg-formats.html).

Project configuration and full examples for referencing the device-control DLL directly are in the [DLL developer guide](developer-guide.md).
