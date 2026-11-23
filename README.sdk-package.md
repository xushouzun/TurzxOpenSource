# TURZX WinUSB Display SDK

[Official website](https://www.turzx.com/) · Explore TURZX displays and more.

Extract the ZIP and install the .NET 10 SDK on Windows. Run the following command from the extracted directory to enumerate connected displays with matching drivers.

```powershell
dotnet run --project examples/SdkDiscover
```

Reference `lib/TURZX.WinUsbDisplay.dll` for device control. Add `lib/TURZX.WinUsbDisplay.Media.dll` for media processing. IntelliSense documentation is alongside the assemblies. Opening a device session requires the 8-byte command key supplied by the firmware maintainer.

- [SDK guide](docs/developer-guide.en.md)
- [Win207 protocol reference](docs/win207-protocol.en.md)
- [License and notices](NOTICE.md) · [Reporting security issues](SECURITY.md)
