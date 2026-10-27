# Hasselblad Startup Studio

[中文](#中文) · [English](#english)

## 中文

哈苏相机开机画面更换工具，支持 macOS / Windows、彩色与灰度、基础裁剪及原厂恢复。

> **切换版本或换图工具前，请先在原电脑使用原工具恢复原厂开机画面，再使用本版本。**
>
> 请保留原工具的首次备份与恢复记录，确认还原完成后再切换。

### 下载

`0.4.0-gen2-preview.1` · 预发布测试版

| 平台 | 下载 | 系统要求 |
| --- | --- | --- |
| macOS | [Universal ZIP](https://github.com/radium-wang/hasselblad-startup-studio/releases/download/v0.4.0-gen2-preview.1/Hasselblad-Startup-Studio-0.4.0-gen2-preview.1-macOS-Universal.zip) | macOS 13+；Intel / Apple Silicon |
| Windows | [x64 测试版](https://github.com/radium-wang/hasselblad-startup-studio/releases/download/v0.4.0-gen2-preview.1/Hasselblad-Startup-Studio-0.4.0-gen2-preview.1-Windows-x64.zip) | Intel / AMD 64 位 |

Mac 版已收到用户实测正常反馈。**作者没有 Windows 电脑，Windows 版尚未实机测试。**

[发布说明与 SHA-256](https://github.com/radium-wang/hasselblad-startup-studio/releases/tag/v0.4.0-gen2-preview.1) · [更新日志](CHANGELOG.md) · [历史版本](https://github.com/radium-wang/hasselblad-startup-studio/releases)

### 机型与图片

| 相机 | 支持固件 |
| --- | --- |
| X2D 100C 普通版 / 大地版 | 原厂 4.2.0 |
| 907X / CFV 100C 标准版 | 原厂 4.2.0 |
| X2D II 100C | 原厂 1.3.18.1 |

CFV II 50C 和其他固件暂不支持。

- PNG / JPEG，校正方向后宽 ≤ 1024、高 ≤ 768 像素。
- RGB 每通道 8 位或 8 位灰度；透明区域填黑，按原尺寸居中，不自动缩放。
- 支持新建裁剪框、移动与四角调整，可导出处理后的 PNG。

### 使用与恢复

1. 完成旧工具还原并关闭其他相机软件，连接开机的相机，点击“连接相机”。
2. 导入图片，选择彩色或灰度，按需裁剪并核对预览。
3. 点击“安装自定义画面”，等待核验与 USB 恢复完成，再完整关机开机查看结果。
4. 需要撤回时点击“恢复原厂”，等待核验与清理完成，再关机开机。

本版本可直接连续换图，不覆盖首次原厂备份。普通版恢复原厂 H 图，大地版恢复原厂大地图。
二代安装期间在电脑和机内各保留一份原图；恢复后清理本工具的机内文件，电脑备份继续保留。
如操作中断，请保留备份与事务记录，在原电脑重新检测并恢复。

仅支持静态图片。早期 RGB 版本曾收到关机时短暂闪图的反馈，尚未确认修复。

### 文档与许可

[构建](BUILD.md) · [二代适配](research/GEN2.md) · [一代 RGB 验证](research/WINDOWS_RGB.md) · [Windows 驱动与验证](research/WINDOWS.md)

[反馈问题](https://github.com/radium-wang/hasselblad-startup-studio/issues) · [参与贡献](CONTRIBUTING.md) · [恢复与安全](SECURITY.md)

原创源码和文档采用 [MIT + Commons Clause 1.0](LICENSE)，属于源码可用项目。允许商业场景自用与按许可免费分享；出售及价值主要来自本软件的收费服务受许可限制。既有 MIT 副本继续按原授权使用，第三方权利见 [依赖通知](THIRD_PARTY_NOTICES.md)。

两端源码已合入 `main`。原厂固件与依赖发行包需自行准备，具体见构建文档。本项目独立开发，与相机厂商无隶属或背书关系。

---

## English

A Hasselblad startup-image tool for macOS and Windows, with color and grayscale support, basic cropping, and factory-image restoration.

> **Before switching versions or startup-image tools, restore the factory startup image using the original tool on the original computer. Then use this version.**
>
> Keep the original tool’s first backup and recovery records until you have confirmed that restoration is complete.

### Downloads

`0.4.0-gen2-preview.1` · Prerelease

| Platform | Download | System requirements |
| --- | --- | --- |
| macOS | [Universal ZIP](https://github.com/radium-wang/hasselblad-startup-studio/releases/download/v0.4.0-gen2-preview.1/Hasselblad-Startup-Studio-0.4.0-gen2-preview.1-macOS-Universal.zip) | macOS 13+; Intel / Apple Silicon |
| Windows | [x64 test build](https://github.com/radium-wang/hasselblad-startup-studio/releases/download/v0.4.0-gen2-preview.1/Hasselblad-Startup-Studio-0.4.0-gen2-preview.1-Windows-x64.zip) | Intel / AMD 64-bit |

A user has reported successful real-camera testing of the macOS build. **The author does not have a Windows PC, so the Windows build has not been tested with a real camera.**

[Release notes and SHA-256](https://github.com/radium-wang/hasselblad-startup-studio/releases/tag/v0.4.0-gen2-preview.1) · [Changelog](CHANGELOG.md) · [Previous releases](https://github.com/radium-wang/hasselblad-startup-studio/releases)

### Cameras and images

| Camera | Supported firmware |
| --- | --- |
| X2D 100C, standard / Earth Explorer edition | Stock 4.2.0 |
| 907X / CFV 100C, standard edition | Stock 4.2.0 |
| X2D II 100C | Stock 1.3.18.1 |

CFV II 50C and other firmware versions are not supported.

- PNG / JPEG, up to 1024 pixels wide and 768 pixels high after orientation correction.
- RGB with 8 bits per channel or 8-bit grayscale. Transparency is filled with black; images are centered at their original size without automatic resizing.
- Create or move a crop selection, adjust its corners, and export the processed image as PNG.

### Use and restoration

1. Restore with the previous tool and close other camera software. Connect the powered-on camera and click “连接相机” (Connect camera).
2. Import an image, choose color or grayscale, crop if needed, and check the preview.
3. Click “安装自定义画面” (Install custom image). Wait for verification and USB restoration, then fully power the camera off and on to check the result.
4. To undo the change, click “恢复原厂” (Restore factory image). Wait for verification and cleanup, then fully power the camera off and on.

You can replace images repeatedly within this version without overwriting the first factory backup. Standard cameras restore their factory H image; Earth Explorer cameras restore their factory Earth Explorer image.
During a Gen2 installation, the original is kept on both the computer and the camera. Restoration removes this tool’s on-camera files while retaining the computer backup.
If interrupted, keep the backups and transaction records, then inspect and restore on the original computer.

Only static images are supported. Earlier RGB builds received reports of a brief image flash during shutdown; a fix has not been confirmed.

### Documentation and license

[Building](BUILD.md) · [Gen2 support](research/GEN2.md) · [First-generation RGB verification](research/WINDOWS_RGB.md) · [Windows drivers and verification](research/WINDOWS.md)

[Report an issue](https://github.com/radium-wang/hasselblad-startup-studio/issues) · [Contributing](CONTRIBUTING.md) · [Recovery and security](SECURITY.md)

Original source and documentation use [MIT + Commons Clause 1.0](LICENSE), a source-available license. Use in commercial work and free sharing under the license are permitted; selling and paid services whose value derives substantially from the software are restricted by the license. Existing MIT copies retain their original terms. See [third-party notices](THIRD_PARTY_NOTICES.md) for third-party rights.

Both implementations are merged into `main`. Stock firmware and dependency distributions are not included; see the build documentation. This project is independent and is not affiliated with or endorsed by the camera manufacturer.
