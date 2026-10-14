# Windows 版使用与验证

0.2.0-preview.3，2026-10-13。本版为 Windows x64 测试包，界面与核心已完整移植。
包内包含 .NET 10 运行时、libusb 与 ADB；用户不需要安装 Python、.NET 或开发工具。
Windows 11 的 Intel / AMD 64 位电脑是首轮验收目标。Windows 10、ARM 电脑和实际 Windows 的 USB 驱动尚未验收；不支持 Windows 7 / 8。

## 使用

完整解压 ZIP，保持整个文件夹，双击 `HasselbladStartupStudio.exe`。不能只复制 EXE。
相机保持开机，用数据线连接，先点击“连接相机”。只连接一台支持的相机。
支持第一代 X2D 100C 普通 / 大地、907X 与 CFV 100C 标准版，原厂绘图库必须匹配 4.2.0 固定哈希。
不支持 CFV II 50C、第二代 X2D 或其他固件。

左侧导入单张 PNG / JPEG。应用自动按方向解码、合成透明背景为黑色并转换为 8 位灰度。
方向校正后的宽度 ≤ 1024、高度 ≤ 768；超过限制请先在外部调整大小。
“保留黑边”按原尺寸居中；“裁剪”允许框选、移动选框、拖动四角。两种方式都不会放大或拉伸图片。
中间预览使用 1024 × 768 画布，并按相机的整数坐标居中。

点击“安装自定义画面”，首次原厂备份必须完整校验后才开始替换。
可以连续换图，后续换图保留同一份首次备份。
点击“恢复原厂”，恢复这台相机首次登记的普通 / 大地 / 标准版原库及文件属性，并清理本应用的机内文件。
不改产品 `SuVariant`、环境 `su_variant`、相机序列号或原厂图。
电脑上的首次备份保留，不能删除；目录在 `%LOCALAPPDATA%\X2DStartupStudio\Backups`。

若相机曾在 Mac 安装自定义图，请先在原 Mac 恢复，或者完整迁移该设备的首次备份目录到上述 Windows 目录。
记录格式与 Mac 兼容；没有对应电脑备份时拒绝覆盖，不能凭机内库重新猜测原厂状态。

## Windows USB 驱动

未发布的 USB 识别修订按 Windows 设备实例和物理父设备计数，合并同一工厂接口的登记别名；真正连接两台相机仍会停止。新包内可双击 `USB识别诊断.cmd`，或者运行 `HasselbladStartupStudio.exe --usb-diagnostics`。它只读取 Windows USB 登记信息，生成并打开 `usb-diagnostics/result.json`，不发送相机命令、不启动 ADB、不安装驱动。报告包含识别数量、接口编号、驱动类别和 Windows 问题码，不包含相机序列号与用户路径；连接仍失败时可将这份报告用于排查。命令行入口只生成报告，包内脚本负责打开它。

当前源码新增针对已登记工厂接口的 WinUSB 直接路径，只打开 MI_03，避免与 ADB 的 MI_06 发生组合设备打开冲突。优先使用已实测的接口 GUID，其他已登记的接口 GUID 也可被发现；接口描述、单相机和固定端点均需校验通过。工厂接口短暂未就绪时按超时边界等待重连；驱动不会被自动更换。只读实机验证与未完成的完整操作验收见 [WinUSB 验证摘要](WINDOWS_WINUSB.md)。

应用使用相机的工厂 bulk 接口 3（OUT 0x04 / IN 0x85）执行命令；大文件使用临时 ADB。
Windows 必须能通过 WinUSB / libusb 支持的驱动访问该工厂接口，并为出现的 ADB 接口加载对应驱动。
应用不修改或替换 Windows 驱动，也不把管理员权限当成驱动安装成功。

若能在资源管理器看见存储卡，仍不表示这两个接口就绪。
若连接提示“接口驱动”，请在设备管理器确认设备 VID 2756、PID 0009（X2D）或 000A（CFV 100C）与具体接口。
只针对正确的工厂接口配置 WinUSB；不要替换整台复合设备、存储、RNDIS 或串口的驱动。
如使用另一个工具包已经能连接，可先保持原驱动配置，关闭该工具包再运行本应用。
ADB 接口仅在准备文件传输时临时出现；未就绪会超时并停止，电脑待完成恢复记录保留。
libusb 的 Windows 驱动说明：[官方文档](https://github.com/libusb/libusb/wiki/Windows)。

Windows ADB 原生后端不提供 Mac 常见的 `usb:` 路径标签；本版使用 `adb -d` 强制选择唯一物理 USB 传输，
再核对平台、临时传输标识和工厂通道的机型 / 配置指纹，拒绝多 USB 设备和网络连接。
依据：[Google Windows 后端源码](https://android.googlesource.com/platform/packages/modules/adb/+/refs/heads/main/client/usb_windows.cpp)、[ADB 参数说明](https://android.googlesource.com/platform/packages/modules/adb/+/refs/heads/main/docs/user/adb.1.md)。

本应用启动自己管理的前台 ADB 子进程，服务器使用 `tcp:localhost:端口`，客户端使用 `-H 127.0.0.1 -P 端口`，共享同一个私有回环端口。
结束操作时仅停止持有进程句柄的自有子进程，不调用全局 `kill-server`，也不升级 / 关闭已有的其他 ADB 服务。
若另一工具占用相机接口，应关闭该工具后再使用。

## 0.2.0-preview.3 修复说明

此前仓库首页仍提供 0.2.0-preview.2 下载包。它没有包含 10 月 13 日合入的 WinUSB / ADB 共存修复；合并 PR 不会更新既有发布附件。preview.3 才将该修复编入可下载的 EXE，并在窗口标题与启动日志显示版本，以便核对实际运行的程序。
新代码已在一台 X2D 上完成 WinUSB 与 ADB 并发只读验证；跨电脑驱动配置、完整安装／恢复仍待实机验收。准备传输通道失败时，日志应同时保留原始失败原因及 USB 收尾失败，不应只显示后者。

## 扩展菜单共存

0.4.12、0.4.17 与 0.5.0 的扩展菜单有每次开机仅尝试一次的加载保护。
本应用安装 / 恢复会重启显示服务，菜单在本次开机中可能暂时不显示。
检测到该保护记录时，日志与界面明确提示“完整关机再开机”，应用不会删除另一工具包的保护记录。
按加载器设计，完整开机应重新加载菜单；反馈设备上的结果仍待确认。
不要同时在两个应用内操作同一相机；本应用自身也只允许打开一个实例。

## 0.2.0-preview.2 修复说明

上一版将服务端监听地址写为 `tcp:127.0.0.1:端口`，ADB 以不支持指定主机名为由立即退出，用户看到的“请完整解压”是误导性通用提示。
已在 Windows 云端用原安装包真实 adb.exe 复现，改用 `tcp:localhost:端口` 后成功。
新版在准备传输前核对配套 DLL、执行 ADB 版本自检，再通过 ADB 协议确认服务就绪；启动失败记录退出码和分类，不把用户路径或设备标识复制到日志。
首次读取原库时的传输启动失败在五种基线下均验证为尚未写相机；这项是控制器替身检查，不能代替实机检查。

## 已测试与待测试

已执行：实际 C# 代码编译、USB 请求与 Mac / C 固定样本逐字节对照、回包解析与超时带数据、短写 / 断开 / 错误拒绝、
机型门禁、原厂键保存、跨设备 / 跨机型拒绝、不可覆盖首次备份、PNG 灰度与裁剪、无设备真实 UI 控件渲染。
实际 C# 生成的相机安装 / 恢复脚本在 Mac 临时文件系统执行；涵盖五种机型 / 原厂键基线、连续换图、重复恢复、备份损坏拒绝和 50 个中断恢复点。
文件哈希 / 权限 / 时间使用真实本地文件；显示服务与 SELinux 标签在此层为替身。

已在 GitHub Windows Server 2025 x64 runner 执行 132 项无厂商固件检查，并实际启动自包含 Windows EXE、渲染原生 Win32 窗口和自动退出。
最终证据：[Windows 云端测试](https://github.com/radium-wang/hasselblad-startup-studio/actions/runs/38150327577)。真实包内 ADB 另通过版本启动、生产监听地址、协议响应、私有客户端、空设备列表和自有进程退出检查。原生窗口测试入口禁止建立 USB 会话；另用无 USB 的 Windows EXE 替身验证生产进程代码的中文参数、超时、输出管道、私有端口及自有进程退出。
尚未执行：真实 WinUSB / ADB 驱动、真实 Windows 相机连接与完整安装 / 恢复、断电 / 断线、CFV / 大地机验收，以及 Windows 11 / 10 消费者电脑验收。
本机 headless 截图是同一套控件的离线渲染；云端 `native-windows-ui.png` 是 Windows 原生窗口的渲染截图。
Windows 包只有通过以上验收后才能标记为正式稳定版。

本轮 Mac 主机：171 项带本地原厂输入的 .NET 检查通过；无厂商输入的同一套检查在 Windows 云端通过 132 项。
15 份 Mac / Windows 实际脚本除空行外逐行一致。发行 ZIP 独立检查涵盖 244 个文件、各 PE 架构、ADB 配套 DLL、运行时、依赖、许可、哈希与隐私边界。
`.github/workflows/windows-checks.yml` 已在独立 `windows-port-preview` 分支运行通过，可重复执行无厂商固件测试与真正 Win32 窗口启动。
`--smoke-ui` 测试入口禁止建立 USB 会话，仅生成本机测试图、渲染原生窗口并自动退出；它不是相机安装验收。

## 从源码构建

安装 .NET 10 SDK，固定 Avalonia 12.1.4 / SkiaSharp 3.119.4 依赖由项目还原。
本地构建资料放在 `outputs`，不提交厂商库、补丁、ADB / libusb 二进制、相机备份或日志。
先按照 [构建文档](../BUILD.md) 离线生成 `outputs/adapter`。
官方依赖：[libusb 1.0.29](https://github.com/libusb/libusb/releases/tag/v1.0.29)、[Android Platform Tools](https://developer.android.com/tools/releases/platform-tools)。
使用 libusb `MinGW64/dll/libusb-1.0.dll`，和同版本 ADB 的 `adb.exe`、`AdbWinApi.dll`、`AdbWinUsbApi.dll`、`NOTICE.txt`、`source.properties`。

```sh
dotnet build windows/Studio.Tests/Studio.Tests.csproj -c Release --artifacts-path outputs/windows-build
dotnet outputs/windows-build/bin/Studio.Tests/release/Studio.Tests.dll . outputs/adapter /path/to/original/eagle-backend.so
python3 CodeTests/check_windows_transactions.py --resources outputs/adapter --stock /path/to/original/eagle-backend.so
python3 tools/build_windows.py --dotnet /path/to/dotnet --adapter outputs/adapter --libusb /path/to/libusb-1.0.dll --adb /path/to/platform-tools --licenses /path/to/dependency-licenses --output outputs/Hasselblad-Startup-Studio-Windows-x64
```

上述测试默认无 USB 访问。Linux / Windows 上可运行 .NET 核心与 headless UI 检查；文件系统 shell 验证的当前命令面向 Mac。
Windows 验收时再人工连接相机，先只读检查，保留首次备份，然后安装一张测试图、替换另一张、恢复并核对原厂状态。
