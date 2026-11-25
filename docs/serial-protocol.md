# TURZX 3.5-inch serial display protocol

This document describes the serial protocol used by the 3.5-inch serial display (`1A86:5722`). It is not a WinUSB/Win207 device: Studio renders the JSON theme on the host and sends one complete RGB565 frame over a CH340 serial port.

The protocol is one-way. Studio does not read replies, so a successful serial write only proves that Windows accepted the bytes.

## Device and serial port

- The USB VID:PID is `1A86:5722`; the USB instance name contains `USB35INCH`. Studio finds the active `COM` port for that instance.
- Serial settings: 115200 baud, 8 data bits, no parity, 1 stop bit, no flow control; DTR and RTS are enabled.
- The device canvas is 480 × 320. A theme may use a 480 × 320 or 320 × 480 canvas.
- Only one frame is sent when playback starts and after a theme or orientation change; there is no continuous stream.

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

When a transfer carries a payload longer than six bytes, the first six bytes of the payload buffer are replaced by the header; meaningful payload fields therefore start at byte 6. Payloads of six bytes or fewer are ignored and only the header is sent.

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

Pixels are RGB565, row-major from top to bottom, with no row padding. Each pixel is two bytes, low byte first (little-endian). A 480 × 320 frame contains 307,200 pixel bytes. At 115200 8N1 a full frame takes about 27 seconds, so this interface is intended for static theme frames, not live video.

## Theme file

- Studio reads JSON `.data` files only; legacy binary `.data` files are rejected.
- `backImg` is required and contains a base64 PNG; the factory themes store it under `backImg.png`. Optional fields include `osdImg`, `osdWithFontBack`, `groupList`, `barList`, `archList` and `tipList`.
- The `isLanscape` flag preserves the original file spelling. The canvas must be 480 × 320 or 320 × 480.
- Studio renders text, bars, arcs and images on the host. Values come from the theme file; empty item text uses placeholder samples, and live sensor data is not read.
- Three factory landscape themes ship with Studio, all 480 × 320.

## Limits

- Studio uses no read channel, acknowledgement or retry; serial write errors are the only transport feedback.
- No encryption or command key.
- This interface provides no device-side storage, H.264, layers, clock or desktop mode.
- One complete frame is transferred at a time; do not interleave frames.

See the [compatibility record](compatibility.md) for device detection, brightness and orientation in Studio.
