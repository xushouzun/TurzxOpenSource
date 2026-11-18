# 来源与授权 / Sources and notices

本项目采用 [MIT 许可证](LICENSE)。

The project is licensed under the [MIT License](LICENSE).

| 内容 / Material | 位置 / Location | 现有记录 / Evidence |
| --- | --- | --- |
| Win207 实现 / Win207 implementation | `src/TURZX.WinUsbDisplay` | 项目方自有代码，从父工作区导入。 / Project-owned code imported from a parent workspace. |
| Studio 标识 / Studio mark | `src/TURZX.WinUsbDisplay.Studio/MainWindow.xaml` | 项目内用 XAML 几何图形绘制。 / Drawn in the project using XAML geometry. |
| 示例场景 / Sample scenes | `src/TURZX.WinUsbDisplay.Studio/Scenes/` | 场景的图形由随项目提供的 HTML、Canvas 与 SVG 源码绘制。 / Scene artwork is drawn by the included HTML, Canvas and SVG source. |
| 圆屏预览 / Round display previews | `docs/images/` | 由内置数字时钟、霓虹星轨和像素烟花场景生成。 / Rendered from the built-in Digital Clock, Neon Star Trails and Pixel Fireworks scenes. |

旧图标和示例图片由团队使用 AI 工具生成。它们和展示这些素材的旧预览截图仅存于本地开发历史。

The team generated the former icon and sample images with AI tools. These assets and the preview screenshot that showed them remain in the local development history.

Win207 协议记录 / Win207 reference · [简体中文](docs/win207-protocol.md) · [English](docs/win207-protocol.en.md) · [日本語](docs/win207-protocol.ja.md) · [한국어](docs/win207-protocol.ko.md)

Windows x64 自包含 Studio 发布包含 .NET 与 Windows Desktop 运行时，并在 `licenses/` 中保留所用运行时包提供的许可证和第三方声明。FFmpeg、Chrome、Edge、设备驱动和固件由用户从各自提供方单独获取。

The self-contained Windows x64 Studio archive includes the .NET and Windows Desktop runtimes. The licenses and third-party notices supplied by those runtime packs are under `licenses/`. Users obtain FFmpeg, Chrome, Edge, device drivers and firmware separately.
