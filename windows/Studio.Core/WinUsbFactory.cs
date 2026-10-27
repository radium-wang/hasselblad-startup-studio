using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace StartupStudio;

// Open only the factory MI_03 path. libusb's Windows composite open also opens
// MI_06, which fails while the private ADB server owns that interface.
public sealed class WinUsbFactory : IUsbBulk, IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct InterfaceDescriptor { public byte Length, Type, Number, Alternate, Endpoints, Class, SubClass, Protocol, Index; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PipeInformation { public int Type; public byte Address; public ushort PacketSize; public byte Interval; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_Initialize(SafeFileHandle device, out IntPtr usb);
    [DllImport("winusb.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_Free(IntPtr usb);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_QueryInterfaceSettings(IntPtr usb, byte alternate, out InterfaceDescriptor descriptor);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_GetCurrentAlternateSetting(IntPtr usb, out byte alternate);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_QueryPipe(IntPtr usb, byte alternate, byte index, out PipeInformation pipe);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_SetPipePolicy(IntPtr usb, byte pipe, uint policy, uint size, ref uint value);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_ReadPipe(IntPtr usb, byte pipe, [Out] byte[] bytes, uint length, out uint transferred, IntPtr overlapped);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_WritePipe(IntPtr usb, byte pipe, [In] byte[] bytes, uint length, out uint transferred, IntPtr overlapped);
    private SafeFileHandle? device; private IntPtr handle;
    public ushort ProductId { get; }
    public static bool Available => WindowsAdbInterface.FactoryPaths().Length > 0;
    public static int ErrorCode(int error) => error switch { 5 => -3, 6 or 22 or 1167 => -4, 121 or 1460 => -7, 50 => -12, 32 => -6, _ => -1 };
    private static void Require(bool success, string stage)
    {
        if (!success) NativeUsb.Require(ErrorCode(Marshal.GetLastWin32Error()), "WinUSB " + stage);
    }
    public WinUsbFactory(Action<string>? trace = null)
    {
        var cameras = WindowsAdbInterface.PresentPhysicalDevices();
        var paths = WindowsAdbInterface.FactoryPaths();
        trace?.Invoke($"Windows USB 识别：{cameras.Length} 台物理相机，{paths.Length} 个工厂接口（同设备别名已合并）");
        var selected = WindowsAdbInterface.SelectFactory(cameras, paths);
        ProductId = WindowsAdbInterface.PhysicalProductId(selected.PhysicalId);
        try
        {
            device = CreateFileW(selected.Path, 0xc0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            Require(!device.IsInvalid, "接口3打开"); Require(WinUsb_Initialize(device, out handle), "接口3初始化");
            Require(WinUsb_GetCurrentAlternateSetting(handle, out var alternate), "当前接口检查");
            Check.That(alternate == 0, "工厂 WinUSB 当前接口设置不受支持。");
            Require(WinUsb_QueryInterfaceSettings(handle, 0, out var descriptor), "接口描述检查");
            Check.That(descriptor.Number == 3 && descriptor.Alternate == 0 && descriptor.Endpoints is >= 2 and <= 16, "工厂 WinUSB 接口描述不匹配。");
            var endpoints = 0;
            for (byte index = 0; index < descriptor.Endpoints; index++)
            {
                Require(WinUsb_QueryPipe(handle, 0, index, out var pipe), "端点检查");
                if (pipe.Type == 2 && pipe.Address == 4) endpoints |= 1;
                if (pipe.Type == 2 && pipe.Address == 0x85) endpoints |= 2;
            }
            Check.That(endpoints == 3, "工厂 WinUSB 端点不匹配，已停止操作。");
        }
        catch { Dispose(); throw; }
    }
    public BulkResult Transfer(byte endpoint, byte[] bytes, uint timeout)
    {
        Check.That(handle != IntPtr.Zero && endpoint is 4 or 0x85 && timeout > 0, "工厂 WinUSB 请求无效。");
        // This sets a host-side timeout only; no USB mode/alternate setting change.
        if (!WinUsb_SetPipePolicy(handle, endpoint, 3, sizeof(uint), ref timeout)) return new(ErrorCode(Marshal.GetLastWin32Error()), 0);
        uint transferred;
        var success = endpoint == 0x85 ? WinUsb_ReadPipe(handle, endpoint, bytes, (uint)bytes.Length, out transferred, IntPtr.Zero)
            : WinUsb_WritePipe(handle, endpoint, bytes, (uint)bytes.Length, out transferred, IntPtr.Zero);
        return new(success ? 0 : ErrorCode(Marshal.GetLastWin32Error()), checked((int)transferred));
    }
    public void Dispose()
    {
        if (handle != IntPtr.Zero) { WinUsb_Free(handle); handle = IntPtr.Zero; }
        device?.Dispose(); device = null;
    }
}
