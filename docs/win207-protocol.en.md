# TURZX Win207 device command reference

This reference describes the Win207 host protocol implemented by this project. Command support depends on the model and firmware. The tables use upper snake case: `GET_` for reads, `SET_` for configuration, and `WRITE_` for transfers. The device identifies requests by command number and channel.

Offsets are zero-based and ranges are inclusive. `u8` is a 1-byte unsigned integer; `u16be`, `u32be` are 2- and 4-byte big-endian unsigned integers; `u32le` is a 4-byte little-endian unsigned integer. "Header" means the 500-byte plaintext header before encryption; "reply" means the actual bytes read from the IN endpoint. Encrypted channel numbers are decimal; raw channel numbers are hexadecimal.

## Connection and calling

- Windows enumerates by the interface GUID registered by the device driver. The verified default GUID is `DEE824EF-729B-4A0E-9C14-B7117D33A817`; override it with `WINUSB_DISPLAY_INTERFACE_GUID`.
- The WinUSB OUT endpoint is `0x01` and the IN endpoint is `0x81`. Send one request then read its reply each time, so replies are not mismatched across commands. Studio's OUT write timeout is 10000 ms and IN read timeout is 5000 ms (large live payloads such as PNGs pause briefly while the firmware composites the background; too-short timeouts abort transfers, wedge the endpoint or drop slow replies — normal control replies are fast, so the timeouts are only upper bounds). The read buffer is 512 bytes.
- Obtain the firmware's 8-byte packet key from the firmware maintainer. Before launching Studio or the CLI on Windows, set `WINUSB_DISPLAY_COMMAND_KEY` to the matching 16 hexadecimal characters. The SDK can also accept the 8 bytes directly.
- Check the actual length before parsing a reply. For structured firmware, storage and supply queries Studio also checks that reply byte 0 echoes the request number, to avoid parsing a reply left over from another command. Receiving a USB reply and the device successfully executing are two different states; some commands have additional reply fields.

## Encrypted channel packet

| Position | Content |
| --- | --- |
| plaintext 0 | command number |
| plaintext 1 | reserved, 0 |
| plaintext 2–3 | fixed marker `1A 6D` |
| plaintext 4–7 | low 32 bits of the millisecond difference between current UTC and local yesterday-midnight at session creation, little-endian. Follows the original Win207 host time baseline |
| plaintext 8–499 | command arguments, remainder zero-filled |
| USB request 0–503 | the 500 plaintext bytes encrypted with DES-CBC, PKCS#7 padding. IV and key are both the firmware key |
| USB request 504–509 | six zero bytes |
| USB request 510–511 | fixed tail `A1 1A` |
| USB request 512 onward | optional raw payload. Media payloads are not part of the header encryption |

DES-CBC is the compatibility format of existing firmware. Device replies stay plaintext. Commands 101, 102 and 121 store the payload byte count (big-endian) at plaintext 8–11. Single-byte control arguments generally go at plaintext 8.

## Generic send/receive interface

Browse for a local file on commands 101 (JPEG), 102 (PNG), and 40 (small-file write). Studio fills in byte lengths. Command 40 also requires an absolute ASCII destination path on the device and a file under 100 KiB. Selecting a file prepares the request. Use Send to transmit it. For command 121, **Browse H.264** selects a `.h264` or `.264` file as the background on the Media page. The player handles stream blocks and queue pacing. Clear the foreground for video-only playback. For files already on the device, use **Advanced controls → On-device media**.

The console needs a command key to open the device session for either channel. Stop Studio playback on the selected device before sending. For encrypted queries 10, 50, and 100, the console validates the echoed command and minimum reply length (40, 12, and 32 bytes respectively) and retries mismatches. For other requests, interpret the actual reply according to the command-specific format.

In Studio **Commands → send/receive**, choose a channel and enter a command number. Numbers may be decimal or `0x`-prefixed hexadecimal. Encrypted and raw channel numbers are independent; e.g. encrypted decimal `60` queries orientation while raw hex `0x60` queries temperature.

| Input or return | Encrypted Win207 | Raw channel |
| --- | --- | --- |
| command number | 0–255, written at plaintext header offset 0 | 0–255, written at request offset 0 |
| arguments | 0–492 bytes from plaintext header offset 8, remainder zero-filled | 0–63 bytes from offset 1, remainder zero-filled |
| payload | optional, appended from USB offset 512. Fill length, flags and other header fields per the command table; the generic interface does not infer fields | no separate payload; request is fixed 64 bytes |
| return | all bytes actually returned by the IN endpoint, zero bytes preserved, not padded to 512 | same |
| no reply or transfer failure | empty reply or short write reported as an I/O error. Driver errors include the operation name and Windows error code | same |

The preview's left column is hexadecimal byte offsets, the middle is byte values, the right is printable ASCII with other characters shown as dots. Receiving USB data only means the device replied. Reply byte 0 may echo the command number; positions and success codes still follow each command. For example the original implementation of 110 checks reply byte 8 for 200, and 113 checks reply byte 1 for 200. Commands without a defined success code keep the raw bytes for inspection.

| Example | Channel and number | Arguments | Read-back |
| --- | --- | --- | --- |
| query firmware | encrypted 10 | empty | check reply length ≥ 40, offset 0 = 10, 8–39 = UTF-8 version text |
| query H.264 block size | encrypted 17 | empty | reply ≥ 12 bytes, 8–11 = big-endian block length, 0 means default |
| set brightness to device value 60 | encrypted 14 | `3C` | the original host defines no uniform success code; keep the reply for inspection. Use SDK `SetBrightness` to apply the model brightness cap |
| query cooling temperature | raw 0x60 | empty | reply ≥ 3 bytes, 1 and 2 = pump and fan temperature in °C |

```csharp
byte[] firmware = screen.ExchangeCommand(10);
byte[] temperatures = screen.ExchangeRawCommand(0x60);
Console.WriteLine(Convert.ToHexString(firmware));
Console.WriteLine(Convert.ToHexString(temperatures));
```

## Basic control and queries

| Number | Name | Arguments and reply | Studio entry |
| --- | --- | --- | --- |
| 10 | GET_FIRMWARE_VERSION | reply 0 should be 10, 8–39 = UTF-8 firmware version, trailing NUL stripped | Advanced controls · Device commands |
| 11 | RESTART_DEVICE | header arguments all zero, 512-byte request. Restart interrupts the session; the original host checks no dedicated success code | Advanced controls |
| 12 | SHUT_DOWN_DEVICE | header arguments all zero, 512-byte request. Powers off the device; no dedicated success code checked | reference |
| 13 | SET_DISPLAY_ORIENTATION | header 8 = u8, 0–3 map to 0°, 90°, 180°, 270°; remaining arguments zero. Studio combines host-side frame rotation with firmware orientation according to the model. Verify the result on the physical panel. Command 60 may fail to reflect the setting on some firmware | Media · orientation |
| 14 | SET_DISPLAY_BRIGHTNESS | header 8 = u8, range 0–100. The 12.3 inch sends floor(percent × 80 ÷ 100). No uniform success code | home · quick controls |
| 15 | SET_VIDEO_FRAME_RATE | header 8 = u8, frames/second; Studio accepts 1–60. Set before writing the first H.264 block | Media, live input |
| 49 | SET_VIDEO_CLOCK_SYNC | header 8 = u8; 0 disables video clock sync, 1 enables. Date/time is set with 51 | reference |
| 50 | GET_SUPPLY_VOLTAGE | no request arguments. Reply ≥ 12 bytes, 8–11 = u32be supply reading. The original device page shows mV on the specified 12.3 inch model | Advanced controls · Device commands |
| 51 | SET_DEVICE_CLOCK | plaintext 8–15 = year high, year low, month, day, hour, minute, second and mode. Original host mode 0/1 = disable/enable, 2 = set time only. Original host checks reply 0 = 51. The SDK uses mode 1 to show the clock and mode 2 to synchronize host local time only. | Advanced controls · Device commands |
| 52 | STOP_DEVICE_CLOCK | header 8 = 0, stops the device clock layer. No uniform success code | Advanced controls · Device commands |
| 60 | GET_ORIENTATION_STATE | no request arguments. Reply ≥ 10 bytes, 8 = current orientation, 9 = sensor orientation, each u8. Raw channel 0x60 queries temperature. Note: measured on turzx_0001_0020 firmware, reply bytes 8 and 9 stay 0 regardless of the value sent with 13, so this reply cannot confirm whether a manual orientation took effect | Commands |
| 61 | SET_PUMP_SPEED | encrypted command defined in the original enum; the host's pump/fan settings use the raw channel | reference |
| 62 | GET_PUMP_SPEED | encrypted command defined in the original enum; the host's speed query uses the raw channel | reference |
| 125 | SET_DEVICE_CONFIGURATION | original host writes u8 brightness, boot mode, 0, orientation, sleep delay, offline mode, offline clock orientation at header 8–14. Mode and delay units are firmware-defined | Advanced controls |
| 150 | SWITCH_TO_DESKTOP_MODE | header arguments all zero, switches to device desktop mode. Model-dependent; no dedicated success code checked | Commands |
| 201 | GET_DEVICE_POSITION_INDEX | header arguments all zero. The original SLC host treats reply 10–11 as a u16be pump reading | Commands |

## Images, video and layers

| Number | Name | Arguments and reply | Studio entry |
| --- | --- | --- | --- |
| 17 | GET_H264_BLOCK_SIZE | no request arguments. Reply ≥ 12 bytes, 8–11 = u32be byte count. 0 uses 198 KiB; Studio rejects block sizes above 4 MiB | Commands |
| 101 | DISPLAY_JPEG | header 8–11 = u32be byte count, complete JPEG from USB offset 512. Reply status code not uniformly defined | Media |
| 102 | DISPLAY_PNG_OVERLAY | header 8–11 = u32be byte count, complete PNG from USB offset 512. Alpha is composited by firmware; live PNGs may alternate with 121 | Media, live input |
| 110 | START_LOCAL_H264_PLAYBACK | plaintext 8–11 = big-endian path length, ASCII path from 16. Original host checks reply 8 = 200 | Advanced controls · on-device media |
| 111 | STOP_LOCAL_H264_PLAYBACK | header arguments all zero, stops H.264 playback from device storage. Host streaming stops with 123 | Commands |
| 112 | GET_LOCAL_H264_PLAYBACK_STATE | no request arguments. Reply ≥ 9 bytes, 8 = u8; 0 = stopped, non-zero = playing | Commands |
| 113 | DISPLAY_LOCAL_JPEG | path arguments as 110. Original host checks reply 1 = 200 | Advanced controls · on-device media |
| 114 | STOP_LOCAL_JPEG_LOOP | header arguments all zero, stops the device JPEG loop. Firmware provides no portable layer-clear reply | Commands |
| 121 | WRITE_H264_STREAM_BLOCK | header 8–11 = block length u32be, 12 = 1 on the final block else 0. Annex B data from USB offset 512. Reply ≥ 9 bytes, 8 = queue depth u8 | Media, live input |
| 122 | GET_H264_QUEUE_DEPTH | normal query, header arguments all zero. Reply ≥ 9 bytes, 8 = queue depth u8. Sync extension below | Commands |
| 123 | STOP_H264_STREAM | header arguments all zero. Sent after the host stops sending, so later blocks do not restart video | Media, Commands |

For ordinary video transfers, the SDK sets the frame rate, reads the block size, waits for the queue depth to reach 1 or less, and sends Annex B blocks. File input waits about 30 ms between blocks. Live input uses separate pacing. During transfer, a queue depth above 3 usually triggers a wait until it falls to 2 or less. The device consumes the remaining queue after a single transfer ends. In firmware-layer mode, foreground 102 requests can alternate with background 121 requests between complete request/reply pairs. The original host's 122 sync extension stores big-endian 64-bit sync time (ms) at header 8–15 and big-endian 32-bit frame count at 16–19. Studio uses the plain query.

## Files, factory tests and lighting

| Number | Name | Arguments or known use | Studio entry |
| --- | --- | --- | --- |
| 38 | CREATE_DEVICE_FILE | path layout as 110; original host checks reply 8 = 200 | reference |
| 39 | WRITE_DEVICE_FILE_BLOCK | header 8–11 = buffer capacity u32be, 12–15 = valid length of this block u32be, 16 = final-block flag u8. The original host sends a full 1 MiB buffer each time; the final block is interpreted by valid length | reference |
| 40 | WRITE_DEVICE_FILE | header 8–11 = path length u32be, 12–15 = file length u32be, ASCII path from 16. File from offset 512. Original host uses it for files under 100 KiB | reference |
| 42 | DELETE_DEVICE_PATH | deletes a file or directory. Header 8–11 = path byte count u32be, ASCII path from 16. No success code parsed | Advanced controls · on-device media |
| 98 | GET_DEVICE_FILE_SIZE | header 8–11 = path byte count u32be, ASCII path from 16. Reply ≥ 12 bytes, 8–11 = u32le. Original host uses the value as bytes | reference |
| 99 | LIST_DEVICE_DIRECTORY | path layout as 98. The SDK issues up to 20 requests, joins the UTF-8 replies and parses slash-separated names after `file:`. `nodir-createdone` indicates an empty directory was created. `result:nodir-createfailed` indicates directory creation failed | Advanced controls · on-device media |
| 100 | GET_STORAGE_CAPACITY | reply 8, 12, 16 = SD total, used, free; 20, 24, 28 = Flash equivalents. Fields are little-endian 32-bit, in KiB | Advanced controls · Device commands |
| 246 | TEST_USB_THROUGHPUT | the original speed-test function sends a zero-filled buffer | reference |
| 251 | SYNC_PUMP_AND_FAN_OUTPUTS | original host writes 16 argument bytes from plaintext 8; within them 0–2 = FF 0F A1, 4–5 = big-endian pump value, 6–8 = three fan values | reference |
| 252 | WRITE_RGB_ANIMATION | header 12–15 = payload length u32be. USB offset 512–513 = total frames u16be, 514 = frame interval u8, animation data from 515. Interval unit and frame encoding are firmware-defined | reference |
| 253 | RUN_FACTORY_H264_TEST | factory-test command in the original enum | reference |

## Raw channel

The raw channel uses the same endpoints but the request is a 64-byte buffer without a DES packet. The number goes at request 0.

| Number | Name | Request and reply | Studio entry |
| --- | --- | --- | --- |
| 0x11 | WRITE_LED_PIXEL_BLOCK | request 1 = LED start index u8, RGB data from 4. The original host sends indices 0, 15, 30 with up to 45, 45, 15 data bytes, remainder zero-filled | reference |
| 0x60 | GET_COOLING_TEMPERATURES | request 1–63 zero-filled. Reply ≥ 3 bytes, 1 and 2 = pump and fan temperature u8 in °C | Commands |
| 0x61 | SET_COOLING_SPEEDS | request 1–2 and 3–4 = pump and fan targets u16be, 5–63 zero-filled. Original host default raw full scale is 3500 | reference |
| 0x62 | GET_COOLING_SPEEDS | request 1–63 zero-filled. Reply ≥ 5 bytes, 1–2 and 3–4 = pump and fan speed u16be. Conversion is model-dependent | Commands |

## Packet and reply examples

Below are pre-encryption headers with the timestamp fixed at 1 ms. Each encrypted header is zero-padded to 500 bytes then processed as above.

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

Project configuration and full examples for referencing the device-control DLL directly are in the [DLL developer guide](developer-guide.en.md).
