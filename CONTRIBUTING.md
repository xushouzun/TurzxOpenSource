# 贡献指南 / Contributing

在 Windows 安装 .NET 10 SDK 后，从仓库根目录运行以下命令。离线单测无需连接副屏。涉及设备行为的变更，请附上 [兼容性记录](docs/compatibility.md) 所列的测试信息及实际结果。

```powershell
dotnet build TURZX.WinUsbDisplay.slnx -c Release
dotnet test tests/TURZX.WinUsbDisplay.Tests -c Release --no-build
dotnet test tests/TURZX.WinUsbDisplay.Windows.Tests -c Release --no-build
```

完整离线验收使用 `pwsh -NoProfile -File tools/verify-project.ps1`。该入口将构建警告视为错误，运行两套测试并分别保存 TRX、Cobertura 覆盖率，验证 CLI 错误路径及真实 Chromium 本地 HTML 截图、推帧、取消和释放。需要本机 Chrome 或 Edge；可用 `WINUSB_DISPLAY_BROWSER` 指定已有浏览器。证据保存在 `artifacts/verification/`，失败也会生成 `summary.json`。两套覆盖率分别报告，不相加；它们不能证明完整 GUI 或实体 USB 行为。

Run `pwsh -NoProfile -File tools/verify-project.ps1` for repeatable offline verification, including both test suites, separate TRX/Cobertura reports, CLI checks and real local Chromium capture. Chrome or Edge must be installed; `WINUSB_DISPLAY_BROWSER` can select the executable. Evidence, including failures, is saved under `artifacts/verification/`. Coverage is reported per suite and does not establish complete GUI or physical-device coverage.

已有 FFmpeg 时加 `-Media` 验证 PNG 转 H.264、MP4 预览与帧率探测。准备发布包时加 `-Package`，同时核对 ZIP 许可证、DLL/XML 和解压后 SDK 枚举示例。也可用 `-Stage Static|Unit|E2E|Package` 独立运行某阶段，每阶段均可从干净源码执行。此入口不执行 USB 写操作，实体画面、插拔重连及无 .NET 环境启动仍按下方清单实测。

Add `-Media` when FFmpeg is configured to verify real conversion, preview muxing and frame-rate detection. Add `-Package` to build and inspect both archives and run the extracted SDK discovery example. `-Stage Static|Unit|E2E|Package` runs a self-contained stage. These checks perform no USB writes; retain the hardware and clean-machine checks below.

Use the commands above from the repository root on Windows with the .NET 10 SDK. Offline tests run without a display. For device changes, include the hardware ID, firmware, steps and observed results.

请通过 Issues 提交问题和实测结果，渠道可用时选择兼容性模板。提交前请移除日志中的封包参数和设备序列号。/ Use Issues to share bugs and compatibility results. Remove packet keys and device serial numbers from logs before posting.

提交 PR 时请说明变更原因、验证命令和结果。更新 SDK 或协议行为时，请同步核对对应的中文、英文、日文和韩文技术文档。安全问题请按[安全政策](SECURITY.md)私下报告。

In a pull request, explain the change and list the verification commands and results. When SDK or protocol behavior changes, check the corresponding Chinese, English, Japanese and Korean documentation. Report security issues privately as described in [SECURITY](SECURITY.md).

## Git 源码与预览版验收 / Git source and preview release verification

通过 Git 提交与发布源码。只有要在 Releases 提供 Studio 或 SDK 下载时，才需要运行以下 ZIP 验收步骤。

Commit and publish source through Git. Run the ZIP checks below when preparing Studio or SDK downloads for Releases.

1. 确认 Windows CI 在待发布提交上通过。在仓库根目录使用 PowerShell 7 或更新版本运行 `pwsh -NoProfile -File tools/package-release.ps1` 可本地打包。GitHub 的 Windows preview 工作流支持手动生成验收用的 ZIP，也会在推送 `v*` 标签后生成 Release 草稿。
2. 在未安装 .NET SDK 或运行时的 Windows x64 环境解压 Studio ZIP，验证启动和离线预览。按[首次送屏指南](docs/first-display.md)用 `0070` 验证枚举、固件查询、单图送屏和停止，记录实测结果。
3. 在装有 .NET 10 SDK 的 Windows 环境解压 SDK ZIP，从包根目录运行 `dotnet run --project examples/SdkDiscover`，核对设备枚举结果、许可证、DLL、XML 和开发指南。
4. 确认两个 ZIP 包含项目 `LICENSE` 和 `NOTICE.md`，并检查 Studio ZIP 的 `licenses/` 是否包含随包运行时的许可证和第三方声明。确认兼容性记录与打包提交一致。
5. 按 [NOTICE](NOTICE.md) 核对随包素材和署名。

首次公开 Git 仓库前，核对准备公开的历史及素材。启用私密漏洞报告后，按 [SECURITY](SECURITY.md) 验证入口可用。

Before uploading binary releases, verify the archives from the tagged workflow. Preview Studio offline on Windows x64 without .NET installed, verify first output on hardware, and run the discovery example from the extracted SDK ZIP with the .NET 10 SDK. Check the bundled files against NOTICE and record the results. Before the first public Git push, review the history being published and verify that private vulnerability reporting works on the hosted repository.

### 功能实测记录 / Functional verification

按候选提交和固件版本记录结果。设备应答与实体画面分别记录。兼容性表只列实际完成的验证。/ Record results against the candidate commit and firmware version. Record device acknowledgements and observed display output separately. List completed checks in the compatibility table.

| 范围 / Area | 检查 / Checks |
| --- | --- |
| Studio 离线预览 / Studio offline preview | 启动、切换四种语言、打开技术文档，分别预览内置 HTML、图片轮播、窗口和使用 FFmpeg 的视频。 / Start Studio, switch through the four languages, open the technical documentation, and preview built-in HTML, a slideshow, a window and video with FFmpeg. |
| 播放 / Playback | 在支持的固件上验证单图、H.264 循环、实时前景叠加、取消和停止。播放中断开再连接，确认续播；分别检查 Studio 和 CLI 的对应入口。 / On supported firmware, verify a still image, H.264 looping, live overlay, cancellation and stop. Disconnect and reconnect during playback to verify resume. Exercise the applicable Studio and CLI entry points. |
| 设备控制 / Device controls | 读取固件和可用状态，检查亮度、方向及启动设置。验证机身存储时使用专用测试文件，列出、播放后删除该文件。 / Read firmware and available status, then check brightness, rotation and startup settings. Use a dedicated test file to verify listing, playback and deletion on device storage. |

验证 20 FPS 实时输出时，使用持续变化的网页或窗口，记录至少 30 秒的不同画面帧率和帧间隔 P95。H.264 路径记录编码器输入、设备块应答和实体画面。轮播可设置为 0.05 秒，记录不同图片的换图率。/ To verify 20 FPS live output, use a continuously changing page or window and record the distinct-frame rate and P95 frame interval for at least 30 seconds. For H.264, record encoder input, block acknowledgements and observed panel output. Set slideshow timing to 0.05 seconds to measure the rate of distinct image changes.
