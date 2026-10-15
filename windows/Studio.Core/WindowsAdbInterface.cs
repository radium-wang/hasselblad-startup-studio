using System.Runtime.InteropServices;
using System.Text;
namespace StartupStudio;

public sealed record WindowsUsbRegistration(string Path, string InstanceId, string PhysicalId);

public static class WindowsAdbInterface
{
    private static readonly Guid ClassId = new("F72FE0D4-CBCB-407D-8814-9ED673D0DD6B");
    private static readonly Guid FactoryClassId = new("CC135687-5267-4313-B0C7-C344609D6EF0");
    private static readonly Guid DeviceClassId = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceData { public int Size; public Guid Class; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceData { public int Size; public Guid Class; public uint DevInst; public IntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, ref DeviceData device);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Enumerate_Classes(uint index, out Guid classGuid, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Get_Device_IDW(uint device, StringBuilder id, uint length, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint device, uint flags);

    public static bool Matches(string path, ushort productId) =>
        productId is 9 or 10 && path.Contains("#vid_2756&pid_" + productId.ToString("x4") + "&mi_06#", StringComparison.OrdinalIgnoreCase);

    // Inspect only Windows interface registration. Do not open an endpoint or
    // change the driver's configuration while waiting for gadget enumeration.
    public static bool Present(ushort productId) => Paths(ClassId).Any(path => Matches(path, productId));
    public static bool MatchesFactory(string path, ushort productId) =>
        productId is 9 or 10 && path.Contains("#vid_2756&pid_" + productId.ToString("x4") + "&mi_03#", StringComparison.OrdinalIgnoreCase);
    public static bool SupportedDevicePath(string path) => new[] { "#vid_2756&pid_0009#", "#vid_2756&pid_000a#" }.Any(value => path.Contains(value, StringComparison.OrdinalIgnoreCase));
    public static ushort PhysicalProductId(string id)
    {
        var parts = id.Split('\\');
        if (parts.Length != 3 || parts[2].Length == 0 || !parts[0].Equals("USB", StringComparison.OrdinalIgnoreCase)) return 0;
        return parts[1].Equals("VID_2756&PID_0009", StringComparison.OrdinalIgnoreCase) ? (ushort)9
            : parts[1].Equals("VID_2756&PID_000A", StringComparison.OrdinalIgnoreCase) ? (ushort)10 : (ushort)0;
    }
    public static string[] PhysicalDevices(IEnumerable<WindowsUsbRegistration> registrations) => registrations
        .Where(item => PhysicalProductId(item.PhysicalId) != 0)
        .Select(item => item.PhysicalId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static int SupportedDeviceCount => PhysicalDevices(Registrations(DeviceClassId)).Length;
    internal static WindowsUsbRegistration[] FactoryPaths()
    {
        // The observed factory GUID is fast, but a valid WinUSB installation can
        // register another interface GUID. Search registered interface classes only
        // when that path is absent; WinUsbFactory still verifies MI_03 and endpoints.
        var preferred = SelectFactoryRegistrations(Registrations(FactoryClassId));
        if (preferred.Length != 0) return preferred;
        if (SupportedDeviceCount == 0) return [];
        var found = new List<WindowsUsbRegistration>();
        foreach (var guid in RegisteredInterfaceClasses())
        {
            if (guid == FactoryClassId || guid == ClassId || guid == DeviceClassId) continue;
            found.AddRange(Registrations(guid));
        }
        return SelectFactoryRegistrations(found);
    }
    // One MI_03 devnode may expose several symbolic links / interface GUIDs.
    // Collapse only identical devnodes, never all interfaces of the same model.
    public static WindowsUsbRegistration[] SelectFactoryRegistrations(IEnumerable<WindowsUsbRegistration> registrations) => registrations
        .Where(item => FactoryInstance(item.InstanceId, PhysicalProductId(item.PhysicalId)) && MatchesFactory(item.Path, PhysicalProductId(item.PhysicalId)))
        .DistinctBy(item => item.InstanceId.ToUpperInvariant() + "|" + item.PhysicalId.ToUpperInvariant()).ToArray();
    private static bool FactoryInstance(string id, ushort pid)
    {
        var parts = id.Split('\\');
        return pid is 9 or 10 && parts.Length == 3 && parts[2].Length != 0 && parts[0].Equals("USB", StringComparison.OrdinalIgnoreCase)
            && parts[1].Equals("VID_2756&PID_" + pid.ToString("X4") + "&MI_03", StringComparison.OrdinalIgnoreCase);
    }
    public static WindowsUsbRegistration SelectFactory(string[] physicalDevices, WindowsUsbRegistration[] paths)
    {
        Check.That(physicalDevices.Length == 1, physicalDevices.Length == 0 ? "未检测到支持的相机，请保持开机后重新检测。" : $"Windows 检测到 {physicalDevices.Length} 台物理相机，请只连接一台 X2D 或 CFV 100C 相机。");
        Check.That(paths.Length == 1, paths.Length == 0 ? "未检测到支持的相机工厂 WinUSB 接口，请重新检测。" : $"同一相机检测到 {paths.Length} 个不同的工厂接口，无法确认访问目标；请查看 USB 识别诊断报告。");
        Check.That(paths[0].PhysicalId.Equals(physicalDevices[0], StringComparison.OrdinalIgnoreCase), "工厂接口与物理相机不匹配，请重新检测。");
        return paths[0];
    }
    internal static string[] PresentPhysicalDevices() => PhysicalDevices(Registrations(DeviceClassId));
    public static (string Path, ushort ProductId)[] SelectFactoryPaths(IEnumerable<string> paths) => paths
        .Select(path => (Path: path, ProductId: (ushort)(MatchesFactory(path, 9) ? 9 : MatchesFactory(path, 10) ? 10 : 0)))
        .Where(item => item.ProductId != 0).ToArray();
    public static IEnumerable<Guid> RegisteredInterfaceClasses()
    {
        Check.That(OperatingSystem.IsWindows(), "USB 接口枚举仅支持 Windows。");
        for (uint index = 0; index < 65536; index++)
        {
            var result = CM_Enumerate_Classes(index, out var guid, 1);
            if (result == 0x25) yield break; // CR_NO_SUCH_VALUE
            if (result == 0x1f) continue; // CR_INVALID_DATA: an empty class entry
            Check.That(result == 0, "Windows USB 接口类枚举失败。");
            yield return guid;
        }
        throw new StudioException("Windows USB 接口类列表异常。");
    }
    private static string[] Paths(Guid guid) => Registrations(guid).Select(item => item.Path).ToArray();
    internal static string DeviceId(uint device)
    {
        var id = new StringBuilder(512);
        return CM_Get_Device_IDW(device, id, (uint)id.Capacity, 0) == 0 ? id.ToString() : "";
    }
    internal static string PhysicalId(uint device)
    {
        for (var depth = 0; depth < 16; depth++)
        {
            var id = DeviceId(device);
            if (PhysicalProductId(id) != 0) return id;
            if (CM_Get_Parent(out var parent, device, 0) != 0 || parent == device) break;
            device = parent;
        }
        return "";
    }
    private static WindowsUsbRegistration[] Registrations(Guid guid)
    {
        Check.That(OperatingSystem.IsWindows(), "USB 接口枚举仅支持 Windows。");
        var paths = new List<WindowsUsbRegistration>(); var set = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0x12);
        Check.That(set != new IntPtr(-1), "无法枚举 Windows ADB 接口。");
        try
        {
            for (uint i = 0; i < 1024; i++)
            {
                var data = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data))
                {
                    Check.That(Marshal.GetLastWin32Error() == 259, "Windows USB 接口枚举失败。"); return paths.ToArray();
                }
                var device = new DeviceData { Size = Marshal.SizeOf<DeviceData>() };
                SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out var length, ref device);
                // A registered interface can disappear between the two calls.
                if (length < 8) continue;
                Check.That(length <= 65536, "Windows ADB 接口路径无效。");
                var buffer = Marshal.AllocHGlobal((int)length);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetailW(set, ref data, buffer, length, out _, ref device))
                    {
                        var path = Marshal.PtrToStringUni(buffer + 4) ?? "";
                        if (SupportedDevicePath(path) || MatchesFactory(path, 9) || MatchesFactory(path, 10) || Matches(path, 9) || Matches(path, 10))
                        {
                            var instance = DeviceId(device.DevInst); var physical = PhysicalId(device.DevInst);
                            Check.That(instance.Length != 0 && physical.Length != 0, "Windows 相机设备关系读取失败，请重新检测或生成 USB 识别诊断报告。");
                            paths.Add(new(path, instance, physical));
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            throw new StudioException("Windows ADB 接口列表异常。");
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
}
