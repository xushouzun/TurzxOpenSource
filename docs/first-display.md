# First device output

Use the 7-inch `USB\VID_1CBE&PID_0070` as the starting point. See the [compatibility record](compatibility.md) for other models.

1. Download and extract `TURZX-Studio-win-x64.zip` from Releases, then run `TURZX.WinUsbDisplay.Studio.exe` directly. Preview, layout and offline configuration do not require a connected display.
2. In Device Manager, check **Properties → Details → Hardware Ids** for the VID and PID, and **Driver → Driver Details** to confirm the device is bound to `winusb.sys`. If a driver is needed, obtain a signed WinUSB driver for that hardware ID from [TURZX](https://www.turzx.com/en/) or your device supplier. If a working driver is already installed, continue to the next step. Models with the same physical size may use different protocols.
3. Close vendor software that is using the display. In **Media → Foreground source**, switch to **Choose image**, then use **Browse** to select a JPEG or PNG. Clear the background and click **Start playback**. Check the image and orientation on the physical display, then click **Stop playback**.

If the display is showing the Windows desktop, select `SWITCH_TO_MONITOR_MODE` in **Device commands**, refresh the desktop-mode devices, select the display by serial number and click **Return to USB display**. Desktop mode requires the bundled TURZX display driver; if Windows does not show the display, install it from the desktop-mode driver section in **Advanced controls**. See [mode-switching instructions](win207-protocol.md).

If Device Manager sees a display already in USB display mode while Studio's list stays empty, ask the driver supplier which WinUSB interface GUID it registers. For a custom GUID, set the `InterfaceGuid` field in `%LOCALAPPDATA%\WinUsbDisplay\studio-settings.json`; the CLI accepts `--interface-guid <GUID>`.
