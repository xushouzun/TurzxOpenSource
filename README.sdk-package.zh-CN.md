# TURZX WinUSB Display SDK

[TURZX 官方网站](https://www.turzx.com/) · 了解 TURZX 副屏及更多产品。

将 ZIP 完整解压。在 Windows 安装 .NET 10 SDK，然后从解压目录运行以下命令，枚举已连接且驱动匹配的显示设备。

```powershell
dotnet run --project examples/SdkDiscover
```

设备控制程序集是 `lib/TURZX.WinUsbDisplay.dll`。媒体处理还需要 `lib/TURZX.WinUsbDisplay.Media.dll`。同目录包含 IntelliSense 文档。打开设备会话需要从固件维护者获取 8 字节命令密钥。

- [SDK 指南](docs/developer-guide.md)
- [Win207 协议](docs/win207-protocol.md)
- [许可证和来源](NOTICE.zh-CN.md) · [安全问题报告](SECURITY.zh-CN.md)
