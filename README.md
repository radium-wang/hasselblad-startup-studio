# Hasselblad Startup Studio

当前本地 Windows 开发版为 `0.3.0-rgb-preview.4`（本地测试包“哈苏win启动RGB 0.1.1”），支持高清 PNG／JPEG、16-bit PNG 自动生成 1024×768 的 8-bit 成品，以及大区域鼠标裁切、RGB／灰度、右下角统一操作按钮与历史灰度安装恢复。左侧成品预览放大并固定在顶部，下方设置独立滚动，滚动条与菜单留出间隔。裁切框四边可独立拖动，右上角“绘制画框”可重新选取范围。下方公开下载仍为作者旧版；RGB 版尚未进行真实相机安装和冷启动验收，说明见 [RGB 迭代与恢复](research/WINDOWS_RGB.md)。

## 欢迎交流 / Discussion and contributions

欢迎交流、分享研究发现和使用反馈。欢迎通过 [Issues](https://github.com/radium-wang/hasselblad-startup-studio/issues) 讨论问题与建议，也欢迎提交 [Pull Requests](https://github.com/radium-wang/hasselblad-startup-studio/pulls) 改进代码、文档和测试。

Discussion, research findings and feedback are welcome. Please use [Issues](https://github.com/radium-wang/hasselblad-startup-studio/issues) for questions and suggestions, and submit [Pull Requests](https://github.com/radium-wang/hasselblad-startup-studio/pulls) to improve code, documentation and tests.

## ⬇️ 下载 macOS 安装包

### [点击下载：macOS Universal 安装包（Intel / Apple Silicon）](https://github.com/radium-wang/hasselblad-startup-studio/releases/download/v0.1.4-cc.1/hasselblad-startup-studio-0.1.4-cc.1-macOS-Universal.zip)

**macOS 13 及以上 · 功能版本 0.1.4 · 许可更新包 0.1.4-cc.1 · 测试版**

解压 ZIP，将 **X2D Startup Studio.app** 拖入“应用程序”，然后打开即可，无需自行编译或安装 Homebrew。
[发布说明与 SHA-256](https://github.com/radium-wang/hasselblad-startup-studio/releases/tag/v0.1.4-cc.1)。
本包未经过 Apple 公证；首次打开如提示开发者无法验证，可按[Apple 官方说明](https://support.apple.com/zh-cn/102445)
在“系统设置 → 隐私与安全性”中核对 App 名称后允许打开。

仅面向 **第一代 X2D 100C / 907X 与 CFV 100C 原厂 4.2.0**；各机型验证情况见下方“支持与验证”。

## ⬇️ 下载 Windows 测试版

### [点击下载：Windows x64 测试包](https://github.com/radium-wang/hasselblad-startup-studio/releases/download/v0.2.0-preview.3/Hasselblad-Startup-Studio-0.2.0-preview.3-Windows-x64.zip)

**0.2.0-preview.3 · Intel / AMD 64 位 · 预发布测试版**

此前 preview.2 下载包没有包含 10 月 13 日合入的工厂 WinUSB / ADB 共存修复。preview.3 已将修复打入 Windows 包，并在窗口标题与启动日志显示版本；只读实机证据及完整安装/恢复的未验收范围见 [WinUSB 验证摘要](research/WINDOWS_WINUSB.md)。

本版修复了上一版内置 ADB 监听地址导致的启动失败，并补充真实内置 ADB 的生产启动回归。

完整解压后运行 **HasselbladStartupStudio.exe**，保持整个文件夹；无需另装 .NET、Python 或开发工具。
包含灰度图片编辑、原尺寸居中预览、USB / ADB、首次备份与恢复。

完整安装／恢复尚缺 Windows 实机验收；已有验证：

- preview.3 源码：[Windows 云端 150 项无相机检查](https://github.com/radium-wang/hasselblad-startup-studio/actions/runs/38365823264)、包内 ADB 探测、自包含 EXE 打包及原生窗口启动通过；最终 ZIP 另经本地依赖、许可、哈希与隐私检查。
- 本地 171 项固件离线检查；原安装／恢复脚本的 50 个中断恢复点证据继续适用。
- 最终安装包依赖、许可与隐私检查。

**preview.3 尚未经过真实相机完整安装／恢复验收；仅在一台 X2D 上完成只读 USB／ADB 验证，仍保留为测试版。** 普通版和大地版的首次备份保护均已保留。

Windows 云端执行环境为 Windows Server 2025 x64；上述检查不能代替 Windows 11 / 10 用户电脑的驱动和相机验收。
[发布说明与 SHA-256](https://github.com/radium-wang/hasselblad-startup-studio/releases/tag/v0.2.0-preview.3) · [Windows 使用与驱动说明](research/WINDOWS.md) · [云端测试记录](https://github.com/radium-wang/hasselblad-startup-studio/actions/runs/38365823264)。

---

为第一代 **X2D 100C / 907X 与 CFV 100C 原厂 4.2.0** 更换灰度开机画面的桌面 App。
macOS 原生版支持 macOS 13 及以上，Intel 与 Apple Silicon Universal，当前功能版本 **0.1.4**；Windows x64 测试版为 **0.2.0-preview.3**。当前源码使用 [MIT + Commons Clause 1.0](LICENSE)。

导入自己的 PNG／JPEG、选择保留黑边或裁剪、在画布内按原尺寸居中预览，然后通过 USB 安装。
安装采用 App 独立模式 3，保留原厂普通版／大地版身份和原厂显示键；恢复使用这台相机的首次原厂备份。
本项目是独立研究工具，与相机厂商没有隶属或背书关系。

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
上方的 `0.1.4-cc.1` 安装包附带完整新许可；旧 `v0.1.4` 下载包保持不变。各下载包以所附许可和对应源码修订为准。

## 功能

- 图片按方向纠正后要求宽不超过 1024、高不超过 768；转换为 8 位灰度，透明区域填黑，不自动缩放。
- 左侧导入、保留黑边／自由裁剪，支持新建裁剪框、移动和拖动四角；中间按 1024×768 画布预览。
- 顶部连接相机、底部安装／恢复，右侧只显示操作阶段和明确错误，不设复制或删除日志按钮。
- 首次原厂备份不可被后续换图覆盖；可以直接再次安装另一张图，恢复仍回到原厂画面。
- 普通 X2D 保留变体 0 与原厂 H 图，大地 X2D 保留变体 2 与原厂大地图；CFV 100C 当前只接受标准变体 0。
- 不提供切换机型、写序列号、写产品变体或写原厂环境选择的入口。

## 支持与验证

| 对象 | 当前状态 |
| --- | --- |
| 第一代普通 X2D 100C / 原厂 4.2.0 | 0.1.2 普通机实际 App 安装、恢复和再次检测通过；独立只读核对原库、文件属性、原厂配置／键及自有文件清理通过 |
| 第一代大地 X2D 100C / 原厂 4.2.0 | 身份保留、原键与恢复约束已离线验证；原厂大地机完整实机流程待验收 |
| 907X / CFV 100C / 原厂 4.2.0 | 0.1.4 新增识别与恢复约束，固件共用依据和离线检查已完成；实际显示、安装与恢复待验收 |
| CFV II 50C / X2D II / 其他固件 | 不开放安装 |
| Apple Silicon | 原生窗口和离线检查通过 |
| Intel / macOS 13 最低系统 | Universal 包检查及 x86_64 Rosetta 执行通过；真实 Intel 与最低系统验收待完成 |

0.1.4 两架构各 69 项原生／文件系统检查，包含两机型共 5 种原厂键状态与 50 处中断恢复。
另有 29 项实际 ARM64 绘制模拟、20 项 C USB 替身检查、11 项 Swift/C 集成及 8 项 Universal 包检查。
CI 只进行源码检查；默认测试不访问设备。离线服务和文件系统替身不能替代真实 USB 断开、掉电或 SELinux 验收。
普通 X2D 的一次成功不代表全部设备或故障下都已保证无残留。

## 使用

1. 关闭其他使用相机 USB 的软件，连接开机的受支持相机，点击“连接相机”。
2. 导入符合尺寸要求的图片，选择保留黑边或裁剪，确认预览。
3. 点击“安装自定义画面”，等待安装、读回核验与 USB 恢复完成；正常关机再开机检查画面。
4. 后续换图可以直接再安装，无须先恢复；每次等待上一项操作完成。
5. 点击“恢复原厂”，等待核验与清理完成，正常关机再开机并重新检测。

恢复只撤回本 App 的开机图修改，不撤回其他工具的功能；不要同时用两个工具安装或恢复。
原厂身份、原键、未知绘图库、恢复记录、设备归属或异常符号链接校验失败时停止操作。
安装中断应保留首次备份及事务记录，在同一台 Mac 上重新连接、检查并恢复。
本地备份位于 App 的 Application Support 私有目录；详细诊断也留在本机，不上传到公开 Issue。
源码构建使用临时签名，未作开发者公证。

## 构建与开发

见 [BUILD.md](BUILD.md)。普通构建、加载器模拟、USB 替身、源码检查均为离线入口。
原厂库、适配层／补丁二进制、依赖发行包、用户图片、设备备份和原始日志 **不包含在源码仓库中**。
构建者自行准备匹配的原厂库与 libusb／ADB，缺少输入时不能完成可安装 App 的构建。

```sh
python3 tools/check_source.py
python3 CodeTests/check_source_tools.py
```

上面两项只需 Python 3.11+ 标准库，不需要固件、USB 或另一 App。

## 文件地图

| 路径 | 职责 |
| --- | --- |
| `native/App.swift` | 原生界面、导入、灰度转换、裁剪、预览 |
| `native/Core.swift` | 图片协议、原厂只读校验、首次备份、安装／恢复事务 |
| `native/Transport.swift` | 工厂 USB 控制、ADB 大文件传输、通道恢复、私有诊断 |
| `native/USBBridge.c` / `.h` | 两机型 USB 枚举、CRC／cookie、最终回复与错误处理 |
| `native/loader.S` | 独立模式 3 的机内资源读取；无效资源回原厂路径 |
| `tools/firmware_layout.py` | 本模块固件布局与门禁，不依赖旧实验 |
| `tools/build_adapter.py` / `build_mac.py` | 离线适配层与 Universal App 构建 |
| `tools/mac_compatibility.py` | 包架构、最低系统与动态依赖审查 |
| `tools/check_source.py` / `export_source.py` | 源码隐私／链接审查与白名单导出 |
| `CodeTests/` | 离线验证与合成样本；`Live*` 为单独显式实机入口 |
| `research/` | [架构](research/ARCHITECTURE.md)、[USB](research/USB-CONNECTION.md)、[Mac 兼容](research/MAC-COMPATIBILITY.md)、[CFV 适配](research/CFV100-ADAPTATION.md) |
| `outputs/` | 本地生成物，Git 忽略，不随源码导出 |

参与贡献见 [CONTRIBUTING.md](CONTRIBUTING.md)，依赖与通知见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)，
安全与恢复问题见 [SECURITY.md](SECURITY.md)，版本变化见 [CHANGELOG.md](CHANGELOG.md)。
