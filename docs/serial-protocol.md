# TURZX 3.5-inch serial display protocol

This document describes the serial protocol used by the 3.5-inch serial display (`1A86:5722`). It is not a WinUSB/Win207 device: Studio renders the theme on the host, sends one complete RGB565 frame when playback starts and then sends only changed regions over a CH340 serial port.

The protocol is one-way. Studio does not read replies, so a successful serial write only proves that Windows accepted the bytes.

## Device and serial port

- The USB VID:PID is `1A86:5722`; the USB instance name contains `USB35INCH`. Studio finds the active `COM` port for that instance.
- Serial settings: 115200 baud, 8 data bits, no parity, 1 stop bit, no flow control; DTR and RTS are enabled.
- The device canvas is 480 × 320. A theme may use a 480 × 320 or 320 × 480 canvas.
- A full frame is sent when playback starts or the orientation changes. While playing, Studio refreshes changed regions about once per second.

## Register packet

Each transfer starts with a 6-byte header. X, Y, EndX and EndY are 10-bit values (0–1023); the register is one byte.

| Byte | Content |
| --- | --- |
| 0 | X bits 9–2 |
| 1 | X bits 1–0 in bits 7–6; Y bits 9–4 in bits 5–0 |
| 2 | Y bits 3–0 in bits 7–4; EndX bits 9–6 in bits 3–0 |
| 3 | EndX bits 5–0 in bits 7–2; EndY bits 9–8 in bits 1–0 |
| 4 | EndY bits 7–0 |
| 5 | register |

For payloads longer than six bytes, the header overwrites the first six bytes of the payload buffer, so payload data starts at byte 6. Payloads of six bytes or fewer carry no data and are sent as the header alone.

## Registers

| Register | Purpose | Fields |
| --- | --- | --- |
| 255 | connection probe | all coordinates 0; no payload |
| 110 | wake / backlight | X = 255 − brightness level. Level 0 is darkest and 255 is brightest. Studio writes X = 0 during wake-up |
| 109 | wake | all coordinates 0; no payload |
| 122 | mirror mode | 16-byte payload; byte 6 = mode (0 = normal) |
| 121 | orientation | 16-byte payload; byte 6 = 100 + type (type 0–3), bytes 7–8 = width and 9–10 = height, both big-endian |
| 197 | frame start | header X = 0, Y = 0, EndX = width − 1, EndY = height − 1; followed by RGB565 pixels |

| Transfer | Bytes |
| --- | --- |
| Probe | `00 00 00 00 00 FF` |
| Brightness level 0 (darkest) | `3F C0 00 00 00 6E` |
| Brightness level 255 (brightest) | `00 00 00 00 00 6E` |
| Mirror normal (16 bytes) | `00 00 00 00 00 7A 00 00 00 00 00 00 00 00 00 00` |
| Orientation landscape, type 2, 480 × 320 (16 bytes) | `00 00 00 00 00 79 66 01 E0 01 40 00 00 00 00 00` |
| Frame header for 480 × 320 | `00 00 07 7D 3F C5`, then 307,200 pixel bytes |

## Start-up sequence

1. Open the serial port and wait about 100 ms.
2. Send register 255.
3. Wait about 100 ms, then write 0 to register 110 and to register 109.
4. Wait about 100 ms, set mirror mode 0, then set orientation.
5. Wait about 500 ms and set the target brightness.
6. Send the register 197 header, wait about 10 ms, send the RGB565 frame, wait about 10 ms.

The delays mirror Studio's current implementation; firmware may tolerate other timing.

## Orientation

Types 0–3 correspond to the four firmware orientations. Landscape themes use type 2 by default and type 3 when inverted. Portrait themes use type 0 and type 1. Studio exposes 0° and 180° for this display.

## Frame data

Pixels are RGB565, row-major from top to bottom, with no row padding. Each pixel is two bytes, low byte first (little-endian). A 480 × 320 frame contains 307,200 pixel bytes. At 115200 8N1 a full frame takes about 27 seconds, so live updates send only changed regions; this interface is not intended for live video.

## Theme file

- A theme is a `.data` file produced by the original 3.5-inch display tool: a .NET BinaryFormatter (NRBF) stream whose root object is `usb_hid.ThemeConfig`.
- The file stores the background PNG, optional OSD images, text groups, bars, arcs and tips; the `isLanscape` flag keeps the original spelling.
- The background canvas must be 480 × 320 or 320 × 480; other sizes are rejected.
- Studio only reads this format and never rewrites it. Create or edit themes with the original tool.
- Studio renders text, bars, arcs and images on the host. Named metrics use simulated mock values; unnamed or custom text keeps theme values and placeholder samples. No live sensor data is read.
- Eight factory themes ship with Studio: six at 480 × 320 and two at 320 × 480.

## Limits

- Studio uses no read channel, acknowledgement or retry; serial write errors are the only transport feedback.
- No encryption or command key.
- This interface provides no device-side storage, H.264, layers, clock or desktop mode.
- One region or full frame is transferred at a time; do not interleave transfers.

See the [compatibility record](compatibility.md) for device detection, brightness and orientation in Studio.
