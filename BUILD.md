# 独立构建与离线验证

Windows 的构建、驱动与测试边界见 [Windows 文档](research/WINDOWS.md)。
Windows 使用 .NET 10 / Avalonia，一代沿用同一模式 3 适配层与记录；二代使用两端共用的 `gen2/` 引擎。

当前 Mac / Windows RGB 测试版需要同时生成 `--rgb` 适配层和原样历史灰度适配层，并通过 `--legacy-adapter` 打入后者。不能将 RGB 资源交给旧版灰度读取器，完整方法与证据见 [RGB 构建及恢复说明](research/WINDOWS_RGB.md)。

所有命令从本项目根目录运行。Python 3.11+；macOS 编译需要 Xcode Command Line Tools、Swift 和 clang。
下列默认构建与测试 **不连接或修改相机**。

## 无固件检查

```sh
python3 tools/check_source.py
python3 CodeTests/check_source_tools.py
python3 -m compileall -q tools CodeTests
```

源码审查检查白名单、符号链接、文本／二进制边界、个人路径／凭据模式与本地文档链接。
它是自动辅助审查，不承诺自动发现一切敏感内容；实际公开清单还应人工核对。
不可变源码包可以额外运行 `python3 tools/check_source.py --verify-release`，核对 `release-source.json` 中全部源码 SHA-256。
后续开发默认不核对旧发行包哈希；重新导出会生成新的文件清单。

## 准备输入

一代的 ELF／模拟依赖只用于构建及模拟；二代另内置独立 Python 运行库，用户不需要安装。开发依赖：

```sh
python3 -m venv .venv
source .venv/bin/activate
python3 -m pip install -r requirements-dev.txt
```

自行准备下列输入，并通过命令参数显式指定；不要将输入加入仓库：

- 原厂 4.2.0 的 `eagle-backend.so`，2051168 字节，SHA-256 必须为 `a0ac02a51d87d08fa61fd0d1e79af15db248af4b5b32603b9483d09f9b7f6217`。
- libusb 头文件目录，以及含 arm64／x86_64 的 macOS 动态库和对应发行版 COPYING。
- 含 arm64／x86_64 的 macOS `adb` 可执行文件，以及其对应许可文件。

**不能直接把常见的单架构 Homebrew 动态库当作 Universal 输入。** 所有 Mach-O 及其非系统依赖最终必须位于 App 内，且最低系统不高于 macOS 13。
源码仓库不包含固件提取器或 Universal 依赖构建器；提取与依赖准备是构建者的外部前提。
两机型共用本模块绑定的原库，不使用 50C 的资源或地址。周年图片由用户自行导入。

以下变量是示例本地输入位置，请按实际文件设置。`inputs/` 为忽略目录：

```sh
STARTUP_STOCK=inputs/eagle-backend.so
STARTUP_USB_LIBRARY=inputs/libusb-1.0.dylib
STARTUP_USB_HEADERS=inputs/include/libusb-1.0
STARTUP_USB_LICENSE=inputs/libusb-COPYING
STARTUP_ADB=inputs/adb
STARTUP_ADB_LICENSE=inputs/Android-platform-tools.txt
```

## 生成适配层与 ARM64 验证

```sh
python3 tools/build_adapter.py "$STARTUP_STOCK" outputs/adapter --rgb
python3 CodeTests/check_loader.py "$STARTUP_STOCK" outputs/adapter
python3 tools/build_adapter.py "$STARTUP_STOCK" outputs/legacy-adapter
```

先校验原库完整哈希，再编译 `native/loader.S`，保留原厂 H／大地资源和选择逻辑。
适配层模拟执行实际 ARM64 指令，共 41 项，覆盖灰度／RGB、完整 framebuffer、短读、无效资源、分配失败、原厂回退及边界守卫。
生成 `adapter.so`、`patch.bin`、清单与报告均留在 `outputs/`，不提交。
Unicorn 需要允许本地 JIT 的执行环境；受限环境曾出现 SIGILL，不能把模拟器启动失败视为通过。

## 构建 Universal App

```sh
python3 tools/build_mac.py \
  --adapter outputs/adapter \
  --legacy-adapter outputs/legacy-adapter \
  --libusb "$STARTUP_USB_LIBRARY" --libusb-license "$STARTUP_USB_LICENSE" \
  --usb-include "$STARTUP_USB_HEADERS" \
  --adb "$STARTUP_ADB" --adb-license "$STARTUP_ADB_LICENSE" \
  --gen2-runtime inputs/python313-universal-runtime \
  --gen2-python-license inputs/Python-installer.rtf \
  --pyusb inputs/usb --pyusb-license inputs/PyUSB-license.txt
```

默认输出 `outputs/macOS-Universal/X2D Startup Studio.app`。
分别编译两个架构，合并主程序，内置 libusb／ADB 与通知，进行本地临时签名，再审查所有 Mach-O。
App 不依赖用户安装 Homebrew，启动不会自动连接相机。当前 bundle identifier 与旧版保持一致，以保留首次备份兼容。
不要覆写正在运行的 App；可用 `--output` 指定另一个本地目录。

二代需要可重定位的 Python 3.13.15 Universal 运行库（`bin/python3.13`、`lib/Python`、标准库及扩展）；所有 Mach-O 都必须满足同一双架构和最低系统检查。PyUSB 1.3.1 源包只复制 `usb/` 及对应许可。上述输入和依赖二进制不进入源码仓库。Windows 构建新增 `--gen2-python-zip inputs/python-3.13.15-embed-amd64.zip`，必须匹配 SHA-256 `d1f04d990aee1253d8569e8e5104e30fa9f5fa830899f14843448872d936a2cf`；可用 `--nuget-source` 指定本地离线 feed。

二代完整离线事务检查（可选提供原厂原图，默认使用合成内容，不连接设备）：

```sh
python3 CodeTests/check_gen2.py --report outputs/gen2-offline.json
python3 CodeTests/check_gen2.py --original inputs/original-logo.bin --report outputs/gen2-official-input.json
python3 CodeTests/check_gen2_runtime.py "outputs/macOS-Universal/X2D Startup Studio.app" --report outputs/gen2-runtime.json
```

两种输入都执行相同控制器和实际生成的脚本；UID、SELinux 及 USB 仍为替身。实机验收边界见 [二代适配说明](research/GEN2.md)。

## USB 替身检查

```sh
python3 CodeTests/check_usb_transport.py --usb-include "$STARTUP_USB_HEADERS"
```

仅需要 libusb 头文件和编译器，不链接真实 libusb，也不访问相机。
使用本仓库固定的合成只读请求样本验证实际 C 代码，运行 20 项 C 检查（ASAN／UBSAN）和 11 项 Swift/C 集成。
`--reference` 是可选的额外纯协议对照，不是构建或默认测试依赖。

## 原生与包检查

```sh
STARTUP_APP="outputs/macOS-Universal/X2D Startup Studio.app"
arch -arm64 "$STARTUP_APP/Contents/MacOS/StartupStudio" --self-test --stock "$STARTUP_STOCK" --self-test-root outputs --report outputs/native-arm64.json
arch -x86_64 "$STARTUP_APP/Contents/MacOS/StartupStudio" --self-test --stock "$STARTUP_STOCK" --self-test-root outputs --report outputs/native-x86_64.json
python3 tools/mac_compatibility.py "$STARTUP_APP" --report outputs/mac-audit.json
python3 CodeTests/check_mac_compatibility.py "$STARTUP_APP" --report outputs/mac-rejection-tests.json
```

每个架构 75 项原生／本地文件系统检查；包括 RGB 图片往返、连续安装、恢复、旧 X2D 记录兼容、跨机型拒绝和 50 处中断。
Apple Silicon 执行 x86_64 检查需要 Rosetta；Intel 本机只能直接执行 x86_64，可分别在两种硬件执行。
Rosetta 通过不是实际 Intel 或 macOS 13 最低系统验收。包检查覆盖 8 种正确／错误发行包条件。

## 导出源码

```sh
python3 tools/export_source.py --output outputs/source-release/Hasselblad-Startup-Studio
```

导出路径必须尚不存在。按白名单导出独立源码树、SHA-256 文件清单、源码 ZIP 与校验文件；不含 App、厂商库、补丁二进制、照片和设备数据。
导出树中的命令、依赖及文档不依赖外层研究目录，适合初始化为独立 Git 仓库。

## 实机入口的边界

`CodeTests/LiveTransferTests.swift` 会连接相机、切换运行时 USB 通道，并创建／清理带归属标记的临时资源。
`CodeTests/LiveRecoveryTests.swift` 会撤回这台 Mac 首次备份所登记的开机图安装，改动相机文件并重启显示服务。
二者不在默认构建、离线测试和 CI 中编译执行，需要操作者明确选择并具备恢复依据。
没有提供一键自动执行实机测试的构建命令。验证层级与已知限制见 [README](README.md)。
