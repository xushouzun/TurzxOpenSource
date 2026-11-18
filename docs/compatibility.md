# 兼容性记录 · Compatibility

下表按设备记录固件查询、传输应答与已观察到的实体画面。 / The table records firmware queries, transfer acknowledgements, and observed panel output for each tested display.

| USB VID:PID | 原生分辨率 / Native size | 当前验证 / Current evidence |
| --- | --- | --- |
| `1CBE:0046` | 320 × 960 | 固件 `turzx_0001_0020` 已成功读取。切屏后能恢复本机媒体设置。 / Firmware query and Studio settings restore verified. |
| `1CBE:0055` | 720 × 1280 | 固件 `turzx_0001_0029` 已成功读取。设备应答了 CLI 单图送屏与停止命令。机身存储可写入、列出及删除临时测试图片。 / Firmware query returned `turzx_0001_0029`. The device acknowledged CLI still-image transfers and stop commands. The internal storage supported writing, listing and deleting a temporary test image. |
| `1CBE:0070` | 800 × 1280 | 固件 `turzx_0001_0017` 已成功读取。Studio 单图送屏、播放中切屏、停止后继续编辑均已验证。Studio 已在 90° 下将霓虹赛车 HTML 以 800 × 1280 透明 PNG 帧叠加到 H.264 背景送屏并停止，现场确认方向、裁切和叠加效果均正常。 / Firmware query, still image output, switching during playback, and editing after stop verified. Studio streamed 800 × 1280 transparent PNG frames from the Neon Racing HTML preset over an H.264 background at 90° and stopped successfully. Physical orientation, cropping, and compositing were visually confirmed on the device. |
| `1CBE:0021` | 480 × 480 | 2026-11-17 从暂存 SDK 发布目录运行 `SdkDiscover`，成功枚举设备。固件查询返回 `turzx_0001_0023`。设备确认收到 30 FPS H.264 视频块，取消播放并停止后仍能响应固件查询。 / The staged SDK's `SdkDiscover` example enumerated the device on 2026-11-17. Firmware query returned `turzx_0001_0023`. The device acknowledged 30 FPS H.264 video blocks and responded to a firmware query after playback was cancelled and stopped. |
| `1CBE:0123` | 720 × 1920 | 2026-11-18 实体屏确认：HTML 实时前景使用原生 720 × 1920 JPEG，硬件方向固定为 0，由主机旋转像素，0° 与 90° 均显示正常，90° 方向正确。取消自动实时 H.264 路径：该路径虽有数据块应答，实体屏上半部出现马赛克。原生 JPEG 配合硬件 90° 时黑屏。PNG 清层采用只写入、不等应答，写入后等待 100 ms；实体屏确认旧画面消失、时间与动画持续更新。此前跳过清层会留下旧覆盖层，不能把 PNG 应答超时解释为清层无效。其他角度与视频背景叠加尚未实屏验证。 / Physical output confirmed on 2026-11-18: live HTML foreground displays correctly at 0° and 90° using native 720 × 1920 JPEG frames, host-side pixel rotation and firmware rotation fixed at zero. The 90° orientation is correct. Automatic live H.264 is disabled because acknowledged blocks still produced corruption in the upper screen area. Native JPEG with firmware rotation set to 90° produced a black screen. Playback writes a transparent PNG without reading a reply and waits 100 ms before sending subsequent frames. The user confirmed that this removes the old image and restores continuous clock and animation updates. Skipping PNG clearing left stale overlays; a missing PNG acknowledgement does not mean clearing is ineffective. Other angles and video background compositing remain visually unverified. |

7 寸屏固件 `turzx_0001_0017` 上，两个 SD 卡目录均未收到匹配回包，两个机身存储目录均未返回文件。 / On 7-inch firmware `turzx_0001_0017`, neither SD directory returned a matching reply and both internal directories returned no files.

5 寸屏固件 `turzx_0001_0029` 上，Studio 删除临时图片后，在隔离构建中模拟自动刷新失败。界面清空旧列表，手动刷新后恢复，复查确认图片已移除。 / On 5-inch firmware `turzx_0001_0029`, Studio deleted a temporary image, then an isolated test build simulated a refresh failure. The UI cleared the stale list, recovered on manual refresh, and confirmed the image was absent.

7 寸屏 90° 模式下，非方形像素素材已通过 Studio 转码、送屏及停止测试。H.264 输出为 800 × 1280 方形像素，单像素偏移已通过解码核对。 / On the 7-inch display at 90°, Studio transcoded, streamed, and stopped video from a non-square-pixel source. The H.264 output used square pixels at 800 × 1280, and decoded frames retained one-pixel offsets.

提交兼容性结果时，请附上 Studio 版本或提交、VID:PID、固件版本、方向、前景类型、背景类型和实际表现。区分设备回包、Studio 预览与实体画面，逐项记录单图、轮播、网页或窗口，以及叠加 H.264 视频。

When sharing results, include the Studio version or commit, VID:PID, firmware version, rotation, foreground source, background type, and observed behavior. Distinguish device replies, Studio preview, and physical output. Test still images, slideshow, live capture, and live capture over H.264 separately.

欢迎通过 Issues 的「设备兼容性」表单提交结果。 / Use the Device compatibility issue form to share results.
