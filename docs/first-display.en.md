# First device output

Use the 7-inch `USB\VID_1CBE&PID_0070` as the starting point. Firmware `turzx_0001_0017` has recorded device results. See the [compatibility record](compatibility.en.md) for other models.

1. Extract `TURZX-Studio-win-x64.zip` from the release and launch `TURZX.WinUsbDisplay.Studio.exe`. Preview the built-in Digital Clock with Chrome or Edge before connecting a display.
2. In Device Manager, check **Properties → Details → Hardware Ids** for the VID and PID, and **Driver → Driver Details** for `winusb.sys`. If a driver is needed, ask the [TURZX download or support channel](https://www.turzx.com/en/) or your device supplier for a signed WinUSB driver matching the hardware ID. Continue when a working driver is already installed. Products with the same physical size may still use different protocols.
3. Ask the supplier to put you in touch with the firmware maintainer. Provide the hardware ID and the firmware version shown by the vendor tool or device documentation, and request the matching 8-byte command packet key. The release requires a separately supplied key for physical output. Offline preview is available while you obtain it. Keep the key out of public reports.
4. Close vendor software that is using the display. Set `WINUSB_DISPLAY_COMMAND_KEY` to the supplied 16 hexadecimal characters in PowerShell, then launch Studio from that session. Select the display and read the firmware version in **Advanced controls** to check it against the version supplied with the key.
5. In **Media → Foreground source**, switch to **Choose image**, then use **Browse** to select a JPEG or PNG. Clear the background and click **Start playback**. Check the image and orientation on the physical display, then click **Stop playback**. Record the Studio version, firmware version, orientation and result.

Launch Studio from PowerShell in the extracted folder:

```powershell
$env:WINUSB_DISPLAY_COMMAND_KEY = '<16-hex-character key supplied by the firmware maintainer>'
.\TURZX.WinUsbDisplay.Studio.exe
```

If the display is showing the Windows desktop, select `SWITCH_TO_MONITOR_MODE` in Studio's **Device commands** list. In the right pane, refresh the desktop-mode devices, select the display by serial number and click **Return to USB display**. The display will reappear in Studio's USB device list when it returns. [Mode-switching instructions](win207-protocol.en.md)

If Device Manager sees a display already in USB display mode while Studio's list stays empty, ask the driver supplier which WinUSB interface GUID it registers. For a custom GUID, close Studio, set the environment variable in PowerShell and launch Studio from that window.

```powershell
$env:WINUSB_DISPLAY_INTERFACE_GUID = '<GUID supplied by the driver provider>'
.\TURZX.WinUsbDisplay.Studio.exe
```
