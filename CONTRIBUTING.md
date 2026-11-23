# Contributing

[简体中文](CONTRIBUTING.zh-CN.md)

Install the .NET 10 SDK on Windows and run the following commands from the repository root. Offline tests run without a display. For device-behavior changes, include the hardware ID, firmware, steps and observed results, together with the test information listed in the [compatibility record](docs/compatibility.en.md).

```powershell
dotnet build TURZX.WinUsbDisplay.slnx -c Release
dotnet test tests/TURZX.WinUsbDisplay.Tests -c Release --no-build
dotnet test tests/TURZX.WinUsbDisplay.Windows.Tests -c Release --no-build
```

Run `pwsh -NoProfile -File tools/verify-project.ps1` for repeatable offline verification, including both test suites, separate TRX/Cobertura reports, CLI checks and real local Chromium capture. Chrome or Edge must be installed; `WINUSB_DISPLAY_BROWSER` can select the executable. Evidence, including failures, is saved under `artifacts/verification/`. Coverage is reported per suite and does not establish complete GUI or physical-device coverage.

Add `-Media` when FFmpeg is configured to verify real conversion, preview muxing and frame-rate detection. Add `-Package` to build and inspect both archives and run the extracted SDK discovery example. `-Stage Static|Unit|E2E|Package` runs a self-contained stage. These checks perform no USB writes; retain the hardware and clean-machine checks below.

Open an issue to share bugs and compatibility results, and use the compatibility template when it is available. Remove packet keys and device serial numbers from logs before posting.

In a pull request, explain the change and list the verification commands and results. When SDK or protocol behavior changes, check the corresponding Chinese, English, Japanese and Korean documentation. Report security issues privately as described in [SECURITY](SECURITY.md).

## Git source and preview release verification

Commit and publish source through Git. Run the ZIP checks below when preparing Studio or SDK downloads for Releases.

1. Confirm Windows CI passes on the commit to be released. Run `pwsh -NoProfile -File tools/package-release.ps1` from the repository root with PowerShell 7 or later to build the archives locally. The GitHub Windows preview workflow can also generate verification ZIPs manually and creates a Release draft after a `v*` tag is pushed.
2. Extract the Studio ZIP on a Windows x64 machine without the .NET SDK or runtime, verify startup and offline preview, and follow the [first-output guide](docs/first-display.en.md) with `0070` to verify enumeration, firmware query, still-image output and stop. Record the observed results.
3. Extract the SDK ZIP on a Windows machine with the .NET 10 SDK and run `dotnet run --project examples/SdkDiscover` from the archive root. Check the enumerated devices, license, DLLs, XML documentation and developer guide.
4. Confirm both ZIPs contain the project `LICENSE`, the English and Chinese `NOTICE` and `SECURITY` files and the matching README files. Check that the Studio ZIP's `licenses/` folder contains the licenses and third-party notices for the bundled runtime. Confirm the compatibility record matches the packaged commit.
5. Check bundled assets and attribution against [NOTICE](NOTICE.md).

Before the first public Git push, review the history and assets being published, enable private vulnerability reporting, and verify the entry point as described in [SECURITY](SECURITY.md).

### Functional verification

Record results against the candidate commit and firmware version. Record device acknowledgements and observed display output separately. List completed checks in the compatibility table.

| Area | Checks |
| --- | --- |
| Studio offline preview | Start Studio, switch through the four languages, open the technical documentation, and preview built-in HTML, a slideshow, a window and video with FFmpeg. |
| Playback | On supported firmware, verify a still image, H.264 looping, live overlay, cancellation and stop. Disconnect and reconnect during playback to verify resume. Exercise the applicable Studio and CLI entry points. |
| Device controls | Read firmware and available status, then check brightness, rotation and startup settings. Use a dedicated test file to verify listing, playback and deletion on device storage. |

To verify 20 FPS live output, use a continuously changing page or window and record the distinct-frame rate and P95 frame interval for at least 30 seconds. For H.264, record encoder input, block acknowledgements and observed panel output. Set slideshow timing to 0.05 seconds to measure the rate of distinct image changes.
