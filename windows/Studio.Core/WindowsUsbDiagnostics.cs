using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
namespace StartupStudio;

public sealed record WindowsUsbNode(string InstanceId, string PhysicalId, string Service, uint? Problem);

// PnP metadata only: no CreateFile, WinUSB, libusb, ADB or camera commands.
public static class WindowsUsbDiagnostics
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceData { public int Size; public Guid Class; public uint DevInst; public IntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr guid, string enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref DeviceData data, uint property, out uint type, byte[] buffer, uint size, out uint required);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint device, uint flags);

    private static ushort ProductId(string id) => id.StartsWith(@"USB\VID_2756&PID_0009\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"USB\VID_2756&PID_0009&MI_", StringComparison.OrdinalIgnoreCase) ? (ushort)9
        : id.StartsWith(@"USB\VID_2756&PID_000A\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"USB\VID_2756&PID_000A&MI_", StringComparison.OrdinalIgnoreCase) ? (ushort)10 : (ushort)0;
    public static string Report(IEnumerable<WindowsUsbNode> nodes, string version)
    {
        var relevant = nodes.Where(node => ProductId(node.InstanceId) != 0).ToArray();
        var parents = relevant.Where(node => WindowsAdbInterface.PhysicalProductId(node.PhysicalId) != 0)
            .Select(node => node.PhysicalId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var rows = relevant.Select(node =>
        {
            var hardware = node.InstanceId.Split('\\')[1]; var mi = hardware.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase);
            var driver = node.Service.ToLowerInvariant();
            // Do not export arbitrary service names, IDs, serials or paths.
            if (driver is not ("winusb" or "usbccgp" or "usbstor" or "usbser" or "rndismp")) driver = driver.Length == 0 ? "missing" : "other";
            return new { camera = Array.FindIndex(parents, id => id.Equals(node.PhysicalId, StringComparison.OrdinalIgnoreCase)) + 1,
                model = ProductId(node.InstanceId) == 9 ? "X2D 100C" : "CFV 100C", usbInterface = mi >= 0 && hardware.Length >= mi + 6 ? hardware.Substring(mi + 4, 2).ToUpperInvariant() : "physical",
                driver, problemCode = node.Problem, topologyResolved = WindowsAdbInterface.PhysicalProductId(node.PhysicalId) != 0 };
        }).ToArray();
        return JsonSerializer.Serialize(new { schema = 1, version, windows = Environment.OSVersion.Version.ToString(),
            metadataOnly = true, cameraEndpointAccessed = false, physicalCameras = parents.Length, nodes = rows }, new JsonSerializerOptions { WriteIndented = true });
    }
    public static string Collect(string version)
    {
        Check.That(OperatingSystem.IsWindows(), "USB 识别诊断仅支持 Windows。");
        var nodes = new List<WindowsUsbNode>(); var set = SetupDiGetClassDevsW(IntPtr.Zero, "USB", IntPtr.Zero, 6);
        Check.That(set != new IntPtr(-1), "无法读取 Windows USB 设备登记。");
        try
        {
            for (uint index = 0; index < 65536; index++)
            {
                var data = new DeviceData { Size = Marshal.SizeOf<DeviceData>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref data))
                {
                    Check.That(Marshal.GetLastWin32Error() == 259, "Windows USB 设备登记读取失败。");
                    return Report(nodes, version);
                }
                var id = WindowsAdbInterface.DeviceId(data.DevInst);
                if (ProductId(id) == 0) continue;
                var bytes = new byte[1024];
                var service = SetupDiGetDeviceRegistryPropertyW(set, ref data, 4, out var type, bytes, (uint)bytes.Length, out _) && type == 1
                    ? Encoding.Unicode.GetString(bytes).TrimEnd('\0') : "";
                uint? problem = CM_Get_DevNode_Status(out _, out var code, data.DevInst, 0) == 0 ? code : null;
                nodes.Add(new(id, WindowsAdbInterface.PhysicalId(data.DevInst), service, problem));
            }
            throw new StudioException("Windows USB 设备登记列表异常。");
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
}
