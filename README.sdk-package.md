# TURZX WinUSB Display SDK

[TURZX 官方网站 / Official website](https://www.turzx.com/) · 了解 TURZX 副屏及更多产品 / Explore TURZX displays and more.

将 ZIP 完整解压。在 Windows 安装 .NET 10 SDK，然后从解压目录运行以下命令，枚举已连接且驱动匹配的显示设备。

Extract the ZIP and install the .NET 10 SDK on Windows. Run the following command from the extracted directory to enumerate connected displays with matching drivers.

```powershell
dotnet run --project examples/SdkDiscover
```

设备控制程序集是 `lib/TURZX.WinUsbDisplay.dll`。媒体处理还需要 `lib/TURZX.WinUsbDisplay.Media.dll`。同目录包含 IntelliSense 文档。打开设备会话需要从固件维护者获取 8 字节命令密钥。

Reference `lib/TURZX.WinUsbDisplay.dll` for device control. Add `lib/TURZX.WinUsbDisplay.Media.dll` for media processing. IntelliSense documentation is alongside the assemblies. Opening a device session requires the 8-byte command key supplied by the firmware maintainer.

- SDK 指南 / SDK guide · [简体中文](docs/developer-guide.md) · [English](docs/developer-guide.en.md) · [日本語](docs/developer-guide.ja.md) · [한국어](docs/developer-guide.ko.md)
- Win207 协议 / Protocol reference · [简体中文](docs/win207-protocol.md) · [English](docs/win207-protocol.en.md) · [日本語](docs/win207-protocol.ja.md) · [한국어](docs/win207-protocol.ko.md)
- [License and notices / 许可证和来源](NOTICE.md) · [Reporting security issues / 安全问题报告](SECURITY.md)
