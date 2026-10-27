using System.Runtime.InteropServices;
using System.Security.Cryptography;
namespace StartupStudio;

internal static class NativeUsb
{
    private const string Library = "startup-libusb";
    static NativeUsb()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeUsb).Assembly, (name, _, _) => name == Library ? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "libusb-1.0.dll")) : IntPtr.Zero);
    }
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libusb_init(out IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libusb_exit(IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint libusb_get_device_list(IntPtr context, out IntPtr list);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libusb_free_device_list(IntPtr list, int unref);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libusb_get_device_descriptor(IntPtr device, [Out] byte[] descriptor);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libusb_open(IntPtr device, out IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libusb_claim_interface(IntPtr handle, int number);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libusb_release_interface(IntPtr handle, int number);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libusb_set_interface_alt_setting(IntPtr handle, int number, int alternate);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libusb_get_active_config_descriptor(IntPtr device, out IntPtr descriptor);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libusb_free_config_descriptor(IntPtr descriptor);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libusb_bulk_transfer(IntPtr handle, byte endpoint, [In, Out] byte[] bytes, int length, out int transferred, uint timeout);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libusb_close(IntPtr handle);
    internal static string Error(int rc) => rc switch { -1 => "LIBUSB_ERROR_IO", -3 => "LIBUSB_ERROR_ACCESS", -4 => "LIBUSB_ERROR_NO_DEVICE", -5 => "LIBUSB_ERROR_NOT_FOUND", -6 => "LIBUSB_ERROR_BUSY", -7 => "LIBUSB_ERROR_TIMEOUT", -8 => "LIBUSB_ERROR_OVERFLOW", -9 => "LIBUSB_ERROR_PIPE", -12 => "LIBUSB_ERROR_NOT_SUPPORTED", _ => "LIBUSB_ERROR_" + rc };
    internal static void Require(int rc, string stage)
    {
        if (rc == 0) return;
        var help = rc is -3 or -5 or -12 ? "请按包内驱动说明检查相机工厂接口的 WinUSB 驱动。" : rc == -6 ? "请关闭其他相机工具后重试。" : "请保持相机开机并检查数据线。";
        throw new StudioException("USB " + stage + "失败：" + Error(rc) + "。" + help);
    }
    // libusb C structs use native alignment. These offsets are independently checked against the x64 header ABI.
    [StructLayout(LayoutKind.Sequential)] internal struct Config { internal byte Length, Type; internal ushort Total; internal byte Interfaces, Value, Index, Attributes, Power; internal IntPtr Interface, Extra; internal int ExtraLength; }
    [StructLayout(LayoutKind.Sequential)] internal struct Interface { internal IntPtr Alternates; internal int Count; }
    [StructLayout(LayoutKind.Sequential)] internal struct Alt { internal byte Length, Type, Number, Alternate, Endpoints, Class, SubClass, Protocol, Index; internal IntPtr Endpoint, Extra; internal int ExtraLength; }
    [StructLayout(LayoutKind.Sequential)] internal struct Endpoint { internal byte Length, Type, Address, Attributes; internal ushort Packet; internal byte Interval, Refresh, Synch; internal IntPtr Extra; internal int ExtraLength; }
}
public sealed class UsbLink : IUsbCameraLink
{
    // Windows changes the composite interface set when ADB is enabled. A reopened
    // session must enumerate with a fresh context instead of keeping the old topology.
    private IntPtr context;
    private IntPtr handle; private bool claimed;
    private WinUsbFactory? factory;
    private readonly Action<string> trace;
    public ushort? ProductId { get; private set; }
    public UsbLink(Action<string>? trace = null)
    {
        Check.That(OperatingSystem.IsWindows() && Environment.Is64BitProcess, "真实 USB 连接仅在 Windows 64 位版启用。");
        this.trace = trace ?? (_ => { });
        try { Open(); } catch { Dispose(); throw; }
    }
    private void Open()
    {
        if (WinUsbFactory.Available)
        {
            factory = new WinUsbFactory(trace); ProductId = factory.ProductId;
            trace("工厂 WinUSB 会话已打开：仅接口3，OUT 0x04 / IN 0x85；ADB 接口独立"); return;
        }
        // A Windows composite libusb open grabs other WinUSB interfaces too.
        // Avoid that path while ADB is exposed if a targeted factory path is absent.
        Check.That(!WindowsAdbInterface.Present(9) && !WindowsAdbInterface.Present(10), "工厂 WinUSB 接口未就绪，无法与 ADB 同时访问；请检查相机工厂接口驱动后重新检测。");
        OpenLibusb();
    }
    private void OpenLibusb()
    {
        if (context == IntPtr.Zero) NativeUsb.Require(NativeUsb.libusb_init(out context), "初始化");
        var count = NativeUsb.libusb_get_device_list(context, out var list); if (count < 0) NativeUsb.Require((int)count, "设备枚举");
        Check.That(count <= 1024, "USB 设备列表异常。");
        try
        {
            var found = IntPtr.Zero; var matches = 0;
            for (var i = 0; i < count; i++)
            {
                var device = Marshal.ReadIntPtr(list, checked(i * IntPtr.Size)); var d = new byte[18];
                if (NativeUsb.libusb_get_device_descriptor(device, d) != 0) continue;
                var vid = d[8] | d[9] << 8; var pid = d[10] | d[11] << 8;
                if (vid == 0x2756 && pid is 9 or 10) { found = device; matches++; ProductId = (ushort)pid; }
            }
            trace($"libusb USB 识别：{matches} 个受支持设备；未发现已登记的工厂 WinUSB 接口");
            Check.That(matches == 1, matches == 0 ? "未检测到支持的相机，请连接数据线并保持相机开机。" : $"libusb 检测到 {matches} 个相机设备，无法确认访问目标；请只连接一台相机并生成 USB 识别诊断报告。");
            NativeUsb.Require(NativeUsb.libusb_open(found, out handle), "设备打开");
            NativeUsb.Require(NativeUsb.libusb_claim_interface(handle, 3), "接口3占用"); claimed = true;
            NativeUsb.Require(NativeUsb.libusb_set_interface_alt_setting(handle, 3, 0), "接口3配置");
            NativeUsb.Require(NativeUsb.libusb_get_active_config_descriptor(found, out var configPtr), "端点检查");
            try
            {
                var config = Marshal.PtrToStructure<NativeUsb.Config>(configPtr); var endpoints = 0;
                for (var i = 0; i < config.Interfaces; i++)
                {
                    var iface = Marshal.PtrToStructure<NativeUsb.Interface>(config.Interface + i * Marshal.SizeOf<NativeUsb.Interface>());
                    Check.That(iface.Count is >= 0 and <= 256, "USB 接口描述异常。");
                    for (var j = 0; j < iface.Count; j++)
                    {
                        var alt = Marshal.PtrToStructure<NativeUsb.Alt>(iface.Alternates + j * Marshal.SizeOf<NativeUsb.Alt>());
                        if (alt.Number != 3 || alt.Alternate != 0) continue;
                        for (var k = 0; k < alt.Endpoints; k++)
                        {
                            var ep = Marshal.PtrToStructure<NativeUsb.Endpoint>(alt.Endpoint + k * Marshal.SizeOf<NativeUsb.Endpoint>());
                            if ((ep.Attributes & 3) == 2) { if (ep.Address == 4) endpoints |= 1; if (ep.Address == 0x85) endpoints |= 2; }
                        }
                    }
                }
                Check.That(endpoints == 3, "工厂 USB 端点不匹配，请检查接口驱动。");
            }
            finally { NativeUsb.libusb_free_config_descriptor(configPtr); }
        }
        finally { NativeUsb.libusb_free_device_list(list, 1); }
        trace("工厂 USB 会话已打开：接口3，回包上限1024字节");
    }
    private string Send(string command, uint timeout)
    {
        Check.That((factory != null || handle != IntPtr.Zero) && timeout > 0, "USB 会话已断开。");
        var cookie = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)); if (cookie == 0) cookie = 1;
        return UsbExchange.Send(factory ?? (IUsbBulk)new NativeBulk(handle), command, cookie, timeout, trace);
    }
    public string Command(string text, uint timeout = 12000)
    {
        try { return Send(text, timeout); }
        catch (StudioException e) when (TransportRules.SafeReadRetry(text) && new[] { "LIBUSB_ERROR_TIMEOUT", "LIBUSB_ERROR_IO", "LIBUSB_ERROR_NO_DEVICE" }.Any(e.Message.Contains))
        {
            trace("只读检查重开 USB 会话后重试一次"); Dispose(); Open(); return Send(text, timeout);
        }
    }
    public void Dispose()
    {
        factory?.Dispose(); factory = null;
        if (handle != IntPtr.Zero)
        {
            if (claimed) NativeUsb.libusb_release_interface(handle, 3);
            NativeUsb.libusb_close(handle); handle = IntPtr.Zero; claimed = false;
        }
        if (context != IntPtr.Zero) { NativeUsb.libusb_exit(context); context = IntPtr.Zero; }
    }
    private sealed class NativeBulk(IntPtr handle) : IUsbBulk
    {
        public BulkResult Transfer(byte endpoint, byte[] bytes, uint timeout) => new(NativeUsb.libusb_bulk_transfer(handle, endpoint, bytes, bytes.Length, out var transferred, timeout), transferred);
    }
}
