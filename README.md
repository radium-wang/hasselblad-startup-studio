# Hasselblad Startup Studio

为 **X2D 100C / 907X 与 CFV 100C（原厂固件 4.2.0）** 及 **X2D II 100C（1.3.18.1）** 更换主屏开机画面。
支持导入 PNG／JPEG、基础裁剪、原尺寸居中预览，以及首次原厂备份与恢复。

## 下载最新版

**`0.4.0-gen2-preview.1` · macOS / Windows 均已完成二代适配 · RGB 彩色／灰度 · 预发布测试版**

| 平台 | 下载 | 系统要求 |
| --- | --- | --- |
| macOS | [下载 Universal ZIP](https://github.com/radium-wang/hasselblad-startup-studio/releases/download/v0.4.0-gen2-preview.1/Hasselblad-Startup-Studio-0.4.0-gen2-preview.1-macOS-Universal.zip) | macOS 13 及以上；Intel / Apple Silicon |
| Windows | [下载 x64 测试版](https://github.com/radium-wang/hasselblad-startup-studio/releases/download/v0.4.0-gen2-preview.1/Hasselblad-Startup-Studio-0.4.0-gen2-preview.1-Windows-x64.zip) | Intel / AMD 64 位；作者没有 Windows 电脑，尚未实机测试 |

[发布说明与 SHA-256](https://github.com/radium-wang/hasselblad-startup-studio/releases/tag/v0.4.0-gen2-preview.1) · [对应源码](https://github.com/radium-wang/hasselblad-startup-studio/tree/v0.4.0-gen2-preview.1) · [二代适配与验证说明](research/GEN2.md)

**Mac 版已收到用户实机测试正常的反馈，现已开放下载。Windows 版已完成适配并开放测试包；作者没有 Windows 电脑，因此尚未实机测试。** 普通版和大地版的首次备份保护均已保留。

### 安装与升级

- **macOS：**解压后将 **X2D Startup Studio.app** 拖入“应用程序”。无需 Homebrew；安装包未经过 Apple 公证，首次打开可按 [Apple 官方说明](https://support.apple.com/zh-cn/102445)核对 App 后允许打开。
- **Windows：**本版本尚未实机测试。完整解压后运行 **HasselbladStartupStudio.exe**，保留整个文件夹；无需另装 .NET、Python 或开发工具。
- **旧灰度版升级：**保留首次备份，在同一台电脑上先恢复原厂，再安装 RGB 版。新版能识别已登记的历史灰度安装并提供恢复；不要直接覆盖旧适配层。

<details>
<summary>下载旧版（仅支持第一代相机）</summary>

| 平台 | 版本 | 下载与说明 |
| --- | --- | --- |
| Windows x64 | 0.3.0-rgb-preview.5（RGB／灰度） | [ZIP](https://github.com/radium-wang/hasselblad-startup-studio/releases/download/v0.3.0-rgb-preview.5/Hasselblad-Startup-Studio-0.3.0-rgb-preview.5-Windows-x64.zip) · [发布说明](https://github.com/radium-wang/hasselblad-startup-studio/releases/tag/v0.3.0-rgb-preview.5) |
| macOS Universal | 0.1.4-cc.1（功能版本 0.1.4） | [ZIP](https://github.com/radium-wang/hasselblad-startup-studio/releases/download/v0.1.4-cc.1/hasselblad-startup-studio-0.1.4-cc.1-macOS-Universal.zip) · [发布说明](https://github.com/radium-wang/hasselblad-startup-studio/releases/tag/v0.1.4-cc.1) |
| Windows x64 | 0.2.0-preview.3 | [ZIP](https://github.com/radium-wang/hasselblad-startup-studio/releases/download/v0.2.0-preview.3/Hasselblad-Startup-Studio-0.2.0-preview.3-Windows-x64.zip) · [发布说明](https://github.com/radium-wang/hasselblad-startup-studio/releases/tag/v0.2.0-preview.3) |

0.1.4 和 0.2.0 旧包仅支持灰度。Windows preview.3 已包含工厂 WinUSB / ADB 共存修复；更早 preview.2 的发布附件不包含该修复。旧包同样属于测试版，验证范围见各自发布说明。

</details>

## 功能与图片要求

- **灰度或彩色：**可选择 8 位灰度或 RGB 每通道 8 位；透明区域填黑。
- **原尺寸居中：**PNG／JPEG 按方向纠正后，宽不超过 **1024**、高不超过 **768** 像素；不自动缩放，按原厂方式在 1024×768 画布中居中显示。
- **基础裁剪：**保留原版选框、新建裁剪框、移动及四角调整；macOS 和 Windows 以 Mac 三栏布局为准。
- **保留原厂身份：**第一代自定义画面使用独立模式 3，保留普通版／大地版身份和显示键；二代备份并替换独立 Logo 文件，不创建产品变体。
- **连续换图：**同一新版安装后可直接换另一张图，后续换图不覆盖首次原厂备份。
- **恢复原厂：**使用这台相机自己的首次备份。普通版恢复原厂 H 图，大地版恢复原厂大地图；不改序列号、产品变体或原厂显示键。

当前只支持静态画面。已收到关机时短暂闪出自定义图的反馈；固件离线分析发现空界面会重新触发 Logo 绘制，preview.5 尚未修复，具体实机关机时序仍待核验。

## 使用与恢复

1. 关闭其他占用相机 USB 的软件，连接开机的受支持相机，点击“连接相机”。
2. 导入符合尺寸要求的图片，按需裁剪、选择灰度／彩色，并核对预览。
3. 点击“安装自定义画面”，等待安装、读回核验与 USB 恢复完成；完整关机再开机检查。
4. 后续可直接再安装另一张图，每次等待上一项操作完成。
5. 需要撤回时点击“恢复原厂”，等待核验与清理完成，再关机开机并重新检测。

恢复只撤回本 App 的开机图修改，不撤回其他工具的功能；不要同时使用两个工具安装或恢复。检测到扩展菜单保护提示时，按提示完整关机再开机，让菜单重新加载。

保留首次备份与事务记录；遇到中断或校验失败，在原电脑重新连接、检测并恢复。本地备份与诊断留在 App 的私有目录，不上传到公开 Issue。原厂身份、原键、未知绘图库、恢复记录、设备归属或异常符号链接校验失败时，App 会停止操作。

## 支持与验证

| 对象 | 验证范围 |
| --- | --- |
| 第一代普通 X2D 100C / 4.2.0 | 历史灰度版完成一次 App 安装、恢复及独立原厂状态核验；新版 RGB 完整实机流程待验收 |
| 第一代大地 X2D 100C / 4.2.0 | 原厂身份、显示键、首次备份与恢复约束已离线验证；完整实机流程待验收 |
| 907X / CFV 100C / 4.2.0 | 支持识别与恢复包机型约束，当前仅接受标准变体 0；显示、安装及恢复待实机验收 |
| X2D II 100C / 1.3.18.1 | macOS 已完成适配并收到用户实机测试正常反馈；Windows 已适配并提供测试包，作者没有 Windows 电脑，尚未实机测试 |
| CFV II 50C / 其他固件 | 不开放安装 |
| macOS Intel / Apple Silicon | Universal 包、原生窗口及离线检查通过；真实 Intel 与 macOS 13 最低系统验收待完成 |
| Windows | 有云端原生窗口及离线验证记录、历史只读 USB / ADB 实机证据；不同电脑的驱动及新版完整安装／恢复待验收 |

preview.5 已记录 **41 项 ARM64 绘制模拟、Mac 两架构各 75 项、Windows 231 项离线检查**。范围与历史迭代见 [RGB 验证记录](https://github.com/radium-wang/hasselblad-startup-studio/blob/v0.3.0-rgb-preview.5/research/WINDOWS_RGB.md)。这些结果不等于真实相机冷启动、断电、SELinux 或 USB 故障恢复验收。

<details>
<summary>历史灰度版验证记录</summary>

- 0.1.4：Mac 两架构各 69 项原生／文件系统检查，包含机型／原键基线和 50 处中断恢复；另有 29 项 ARM64 绘制模拟、20 项 C USB 替身、11 项 Swift/C 集成及 8 项 Universal 包检查。
- Windows preview.3：[云端 150 项无相机检查及原生窗口启动](https://github.com/radium-wang/hasselblad-startup-studio/actions/runs/38365823264)、本地 171 项带固件离线检查、安装包依赖／许可／隐私检查通过。
- 一台普通 X2D 的只读工厂接口／ADB 共存检查通过，见 [WinUSB 摘要](research/WINDOWS_WINUSB.md)。这不能代表其他机型、用户电脑或新版 RGB 完整安装／恢复已验收。

</details>

## 交流与贡献 / Discussion and contributions

欢迎通过 [Issues](https://github.com/radium-wang/hasselblad-startup-studio/issues)反馈问题、分享研究发现，通过 [Pull Requests](https://github.com/radium-wang/hasselblad-startup-studio/pulls)改进代码、文档与测试。提交前请阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。

Discussion, research findings and feedback are welcome. Please use [Issues](https://github.com/radium-wang/hasselblad-startup-studio/issues) for questions and suggestions, and [Pull Requests](https://github.com/radium-wang/hasselblad-startup-studio/pulls) for contributions.

## 构建与源码

**macOS / Windows 二代适配源码已合入 `main`，版本快照见 [v0.4.0-gen2-preview.1](https://github.com/radium-wang/hasselblad-startup-studio/tree/v0.4.0-gen2-preview.1)。** 两端均提供预发布测试包；Windows 因作者没有对应电脑，尚未实机测试。

构建请使用目标版本自己的 [RGB 构建说明](https://github.com/radium-wang/hasselblad-startup-studio/blob/v0.3.0-rgb-preview.5/BUILD.md)或本分支 [BUILD.md](BUILD.md)。原厂库、生成的补丁二进制、依赖发行包、用户图片、设备备份及原始日志不包含在源码仓库中；构建者自行准备匹配输入。

默认构建和测试不访问相机；`Live*` 是单独的显式实机入口。源码隐私与链接检查：

```sh
python3 tools/check_source.py
python3 CodeTests/check_source_tools.py
```

| 目录 | 职责 |
| --- | --- |
| `native/` | macOS 界面、图片处理、USB 通信、备份与恢复、相机端加载器 |
| `windows/` | Windows 界面、图片处理、USB 通信、备份与恢复 |
| `tools/` | 离线构建、源码隐私检查与导出 |
| `CodeTests/` | 离线验证；`Live*` 为显式实机入口 |
| `research/` | [架构](research/ARCHITECTURE.md)、[USB](research/USB-CONNECTION.md)、[Mac 兼容](research/MAC-COMPATIBILITY.md)、[CFV 适配](research/CFV100-ADAPTATION.md) |

版本变化见 [CHANGELOG.md](CHANGELOG.md)，依赖通知见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)，安全与恢复问题见 [SECURITY.md](SECURITY.md)。本项目为独立研究工具，与相机厂商没有隶属或背书关系。

## 许可与收费边界

本次许可变更后的项目原创源码及文档采用 **MIT + Commons Clause 1.0**，两部分共同构成许可，不能仅按 MIT 使用。
这是源码可用项目，不属于 OSI 定义的开源软件。完整英文条款见 [LICENSE](LICENSE)，以下是使用边界说明：

- 允许个人、公司及职业摄影师自用，包括在商业工作环境中使用；允许按完整许可修改和免费分享。
- 不授予出售本软件的权利，也不授予向第三方收取费用或其他对价、提供价值全部或主要来自本软件功能的产品或服务的权利。
- 对本软件收费分发、改名打包出售，以及主要利用本软件进行收费代装、开机图更换或相关支持服务，属于上述限制范围。
- 限制取决于产品或服务的价值来源，不是禁止一切相关商业活动；增加实质新价值的产品或服务可能仍可收费，具体按完整条款判断。
- 再分发须同时保留版权、MIT 全文及 Commons Clause 通知。第三方组件、厂商固件、商标与用户图片保留各自的权利及许可。

**旧版授权不追溯变更。** 初始提交 `2809b767992db0f3a6ca413a62035bfd79c1219f`、标签 `v0.1.4`
及已经发布的 0.1.4 源码包仍按原 MIT 许可提供；本次变更不撤销旧副本或其后续合法再分发的 MIT 权利。
旧版 `0.1.4-cc.1` 安装包附带完整新许可；旧 `v0.1.4` 下载包保持不变。各下载包以所附许可和对应源码修订为准。
