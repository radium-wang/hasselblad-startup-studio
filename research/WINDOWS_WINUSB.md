# Windows 工厂接口与 ADB 共存

## 问题及实现

用户在 Windows 连接检查成功后，安装停在首次原厂备份读取前：先出现文件传输准备超时，再出现 USB 原状态恢复未确认。旧界面优先显示收尾失败，掩盖了首次失败。

在包内 libusb 1.0.29 与 ADB 37.0.1 上做接口打开诊断，未启动 ADB 时设备打开及接口 3 claim 返回 0；启动私有 ADB 后，设备打开返回 -3。libusb 的 Windows `winusbx_open` 会为组合设备的多个 WinUSB 接口打开句柄，其中 MI_06 返回 Windows 错误 5（访问拒绝）。只调整等待时间或重建上下文不能解决接口占用冲突；失败的组合打开也可能保留部分已打开接口的句柄。

`WinUsbFactory` 通过 SetupAPI 查找已登记的 Hasselblad 工厂接口类，匹配 VID 2756、PID 0009/000A、MI_03，再直接调用系统 WinUSB API。已实测的接口 GUID 优先；找不到时枚举 Windows 登记的设备接口类，按同样的 VID/PID 与 MI_03 匹配，并继续核对唯一受支持物理相机、接口 3、当前 alternate 0，以及 bulk OUT 0x04 / IN 0x85。仅改变主机侧超时策略。USB 请求、CRC/cookie、回包解析和身份校验沿用原实现。ADB 已暴露但目标工厂接口尚未登记时，有限等待重试，不尝试会打开其他接口的组合 libusb 路径。

当前直接路径支持已登记的 WinUSB 工厂接口；不自动安装驱动，其他驱动配置的真实相机兼容性仍待验收。Mac 代码、安装/恢复脚本、固定固件门禁与首次备份保护未改变。

## 单相机计数修订（未发布）

preview.3 用户反馈在只接一台 X2D、原厂数据线和 4.2.0 固件时提示“请只连接一台相机”。截图不能区分触发的是物理路径、工厂路径还是 libusb 设备计数，尚没有受影响电脑的登记数据。源码中可确定的缺陷是前两项按接口路径条数计数：多个登记别名不能代表多台设备。

修订使用 `SetupDiGetDeviceInterfaceDetailW` 返回的 `SP_DEVINFO_DATA.DevInst`，再用 `CM_Get_Device_IDW`、`CM_Get_Parent` 查找确切 VID/PID 的 USB 物理父设备。按物理设备实例去重，工厂路径按 MI_03 设备实例去重；实例比较忽略大小写。无法解析父设备、不同工厂实例或接口与父设备不匹配时仍拒绝访问，不用“忽略多相机检查”绕过门禁。接口消失导致关系无法读取时只在现有恢复超时边界内重试。

增加每条后端的匿名数量日志；libusb 发现多个设备时仍拒绝猜测目标。诊断入口独立读取当前 USB devnode 的驱动类别及 Windows 问题码，没有相机端点、libusb 或 ADB 调用。报告不包含设备实例、序列号、设备路径、电脑用户名或完整驱动服务名。

新增离线检查涵盖多个 GUID／路径、父节点和子接口别名、大小写、同型号两台相机、不同工厂 devnode、错误／未知父设备、CFV、SetupAPI x64 ABI、诊断匿名化及恢复边界。受影响 Win10／Win11 电脑上的实际连接、驱动和完整操作仍待验收。

2026-10-13，本地 Windows x64：编译零警告，163 项离线检查、原生窗口启动、245 个发行文件与 ZIP 校验通过。显式诊断入口在本机无相机状态下返回零台设备并标明只读元数据；这验证了诊断入口能运行，未验证有相机时的驱动读数或受影响用户电脑。源码工具检查 3 项通过，1 项因 Windows 符号链接权限跳过。

依据：[Microsoft 设备接口详情](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdigetdeviceinterfacedetailw)、[设备实例结构](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/ns-setupapi-sp_devinfo_data)、[父设备关系](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/determining-the-parent-of-a-device)。

## 已完成的验证

- Windows 11 x64、.NET 10：148 项合成数据、进程替身及控件离线检查通过。新增 WinUSB ABI、错误映射、单物理相机门禁、恢复确认与界面错误保留检查。
- 原生 Windows EXE 的 `--smoke-ui` 窗口启动通过；该入口禁止建立 USB 会话。
- 2026-10-12，一台第一代 X2D 100C：生产 `WinUsbFactory` 在 ADB 启动前、运行中、重复打开及退出后均能打开工厂接口并验证端点。
- 同一相机上，使用生产 USB 请求及回包代码读取 `ro.product.device`、`sys.usb.config`、`sys.usb.state`；ADB 并发运行时成功，诊断前后属性相同。没有安装画面、写相机文件或发送 USB 配置变更命令。
- 2026-10-13 修订在 macOS 上交叉编译无警告，150 项合成数据、进程替身与控件离线检查通过；其中新增工厂接口使用其他 GUID 的路径选择、接口登记延迟后的 USB 恢复检查。Windows 云端检查将另行运行；其他 GUID 的真实驱动尚未实机验收。

上述为只读实机证据，不代表完整安装/连续换图/恢复、断电断线或其他机型的验收。原始日志、相机身份、电脑路径、固件、用户图片和备份不随 PR 提交。

## 复现离线检查

```sh
dotnet build windows/Studio.Tests/Studio.Tests.csproj -c Release --artifacts-path outputs/windows-build
dotnet outputs/windows-build/bin/Studio.Tests/release/Studio.Tests.dll .
dotnet publish windows/Studio.App/Studio.App.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None --artifacts-path outputs/windows-build -o outputs/windows-ci-app
```

在 Windows 上运行发布 EXE 的 `--smoke-ui`，核对 `smoke-results/result.json`。真实 ADB 离线入口要求先断开所有受支持相机；不依赖 `--one-device` 作为硬件隔离。

待设备所有者后续验收：先只读检查并保留首次备份，再安装测试图片、连续换图、恢复原厂，并确认 USB 状态及原厂属性。CI 和默认测试不执行这些操作。

参考：[libusb 1.0.29 Windows 后端](https://github.com/libusb/libusb/blob/v1.0.29/libusb/os/windows_winusb.c)、[Microsoft WinUSB 使用说明](https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/using-winusb-api-to-communicate-with-a-usb-device)。
