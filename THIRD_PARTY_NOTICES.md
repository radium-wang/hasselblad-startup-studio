# 依赖、许可与来源

本分支项目原创 Swift、C、C#、汇编、Python 源码及文档使用 [MIT + Commons Clause 1.0](LICENSE)。
两部分共同构成许可；商业场景自用与收费限制、旧版 MIT 授权边界见 [README](README.md#许可与收费边界)。
本许可不授予相机厂商固件、商标、用户图片或另行提供的第三方组件的权利。
Commons Clause 不适用于这些第三方组件；它们继续遵循各自的许可。

| 组件 | 用途与提供方式 |
| --- | --- |
| macOS SDK / Swift / clang | 系统框架与编译工具；由开发者自行安装，不随源码包分发 |
| libusb | App 运行时动态库；由构建者提供，两种 Mac 架构均需包含；保留输入发行版的 COPYING，构建时复制为 `libusb-COPYING.txt`；0.1.4-cc.1 安装包另附 libusb 1.0.30 原始源码与重建说明 |
| Android platform-tools / adb | 大文件 USB 传输；构建者自行准备可用的 Universal 可执行文件与对应许可文件，许可复制为 `Android-platform-tools.txt`；不随本源码仓库分发 |
| pyelftools | 本地读取 ELF，依赖版本见 `requirements-dev.txt`；不进入 App |
| Unicorn | 本地执行实际 ARM64 指令检查；不进入 App |
| .NET 10 | Windows 自包含运行时；MIT 与对应第三方通知，通知随 Windows 包提供 |
| Avalonia 12.1.4 | Windows 界面与离线控件测试；MIT，通知随 Windows 包提供 |
| SkiaSharp 3.119.4 / Skia | Windows 图片解码、灰度 PNG 与绘制；相应 MIT / BSD 等通知随包提供 |
| HarfBuzzSharp 8.3.1.3 / HarfBuzz | 文字排版；相应通知随 Windows 包提供 |
| ANGLE | Windows 图形后端；对应 BSD 及第三方通知随包提供 |
| CPython 3.13.15 | 二代共享引擎的内置运行库；PSF 及所含组件许可，Windows 使用固定哈希的官方 x64 embed，Mac 使用可重定位 Universal 输入；原许可、第三方通知和打包修改摘要随包保留 |
| PyUSB 1.3.1 | Mac 二代 CTRL 接口；保留其 BSD 许可，Windows 二代直接使用系统 WinUSB |

二代运行库的公开许可输入位于 `assets/python-licenses`，来源 URL 与文本 SHA-256 在 `sources.json`。Python 安装发行版中的原始许可另随打包输入复制；通知涵盖 OpenSSL、NCurses、XZ、libmpdec 等所含组件。构建会重定位 Mac 动态库登记路径并移除无用的重复 libusb，保留运行库修改摘要。参见 [Python 官方许可说明](https://docs.python.org/3.13/license.html)。

Windows 0.2.0-preview.1 使用官方 libusb 1.0.29 MinGW64 DLL，动态加载并可替换。
包内 `Licenses` 包含 LGPL 通知、对应 libusb 原始源码与重建说明；应用许可不限制修改 libusb 后的调试、替换或 LGPL 权利。
Windows ADB 37.0.1 与两个匹配 DLL 来自官方 platform-tools，同目录保留其完整 NOTICE。

源码包不含依赖的二进制发行版或厂商固件。重新分发 App 时应保留本项目及实际所用组件的通知，
并根据所选组件发行版附带其要求的材料；仅包含本项目 LICENSE 文件不能替代组件许可。

`0.1.4-cc.1` 安装包中的 libusb 1.0.30 使用 LGPL 2.1 或后续版本，源码位于安装 ZIP 的
`第三方源码/libusb-1.0.30.tar.bz2`，重建与替换步骤见同目录说明。本项目不限制为调试修改后的 libusb
而进行的逆向工程，也不限制 LGPL 授予的 libusb 修改及再分发权利；Commons Clause 仅适用于项目原创部分。

USB 工厂通道来自既有研究：VID `2756`、PID `0009/000A`、接口 3、257 字节帧、command 65、CRC-XMODEM。
本项目 C 桥接实现和固定合成请求样本曾与同一作者的另一桌面工具纯协议构建函数逐字节比对；
源码包不复制该工具的相机功能载荷、产品入口或依赖发行包，正常构建和测试不需要它。

原厂 `eagle-backend.so` 仅作为构建者自行准备的本地输入，必须匹配固定 SHA-256。
适配差异、本地模拟与运行时复用原厂绘制循环，均不改变厂商原始资源的权属；原厂 H、大地与周年图片不随源码分发。
