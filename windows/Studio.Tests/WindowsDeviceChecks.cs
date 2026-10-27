using System.Runtime.InteropServices;
using System.Text.Json;
using StartupStudio;

static class WindowsDeviceChecks
{
    static void Require(bool condition) { if (!condition) throw new Exception("Windows device selection assertion failed"); }
    static void Refused(Action body, string message)
    {
        try { body(); } catch (StudioException e) { Require(e.Message.Contains(message)); return; }
        throw new Exception("Ambiguous USB selection was accepted");
    }
    const string Camera = @"USB\VID_2756&PID_0009\sample-camera-A";
    const string Second = @"USB\VID_2756&PID_0009\sample-camera-B";
    static WindowsUsbRegistration Factory(string guid, string physical = Camera, string instance = @"USB\VID_2756&PID_0009&MI_03\sample-interface-A")
        => new(@"\\?\usb#vid_2756&pid_0009&mi_03#sample-interface-A#{" + guid + "}", instance, physical);
    public static void Run(Action<string, Action> test)
    {
        test("windows-physical-count-merges-parent-and-interface-aliases", () =>
        {
            var rows = new[] { new WindowsUsbRegistration(@"\\?\usb#vid_2756&pid_0009#sample#{a}", Camera, Camera), Factory("a"), Factory("b") };
            Require(WindowsAdbInterface.PhysicalDevices(rows).SequenceEqual([Camera]));
        });
        test("windows-physical-count-keeps-two-same-model-cameras", () =>
        {
            var parents = WindowsAdbInterface.PhysicalDevices([Factory("a"), Factory("a", Second, @"USB\VID_2756&PID_0009&MI_03\sample-interface-B")]);
            Require(parents.Length == 2);
            Refused(() => WindowsAdbInterface.SelectFactory(parents, [Factory("a")]), "2 台物理相机");
        });
        test("windows-factory-aliases-select-one-devnode", () =>
        {
            var selected = WindowsAdbInterface.SelectFactoryRegistrations([Factory("preferred"), Factory("another")]);
            Require(selected.Length == 1 && WindowsAdbInterface.SelectFactory([Camera], selected).Path.Contains("preferred"));
        });
        test("windows-device-identity-is-case-insensitive", () =>
        {
            var alias = Factory("b") with { InstanceId = Factory("b").InstanceId.ToLowerInvariant(), PhysicalId = Camera.ToLowerInvariant() };
            var selected = WindowsAdbInterface.SelectFactoryRegistrations([Factory("a"), alias]);
            Require(selected.Length == 1 && WindowsAdbInterface.PhysicalDevices([Factory("a"), alias]).Length == 1);
        });
        test("windows-two-factory-devnodes-are-not-aliases", () =>
        {
            var selected = WindowsAdbInterface.SelectFactoryRegistrations([Factory("a"), Factory("b", Camera, @"USB\VID_2756&PID_0009&MI_03\sample-interface-B")]);
            Require(selected.Length == 2);
            Refused(() => WindowsAdbInterface.SelectFactory([Camera], selected), "不同的工厂接口");
        });
        test("windows-conflicting-parent-metadata-does-not-collapse", () =>
        {
            Require(WindowsAdbInterface.SelectFactoryRegistrations([Factory("a"), Factory("b", Second)]).Length == 2);
            Refused(() => WindowsAdbInterface.SelectFactory([Second], [Factory("a")]), "不匹配");
        });
        test("windows-empty-count-is-not-multiple-cameras", () =>
        {
            Refused(() => WindowsAdbInterface.SelectFactory([], []), "未检测到支持的相机");
            Refused(() => WindowsAdbInterface.SelectFactory([Camera], []), "未检测到支持的相机工厂");
        });
        test("windows-topology-reenumeration-keeps-bounded-recovery", () =>
        {
            Require(UsbStateRecovery.Transient("Windows 相机设备关系读取失败"));
            Require(UsbStateRecovery.Transient("工厂接口与物理相机不匹配"));
            Require(!UsbStateRecovery.Transient("同一相机检测到 2 个不同的工厂接口"));
            Require(!UsbStateRecovery.Transient("Windows 检测到 2 台物理相机"));
        });
        test("windows-factory-rejects-unresolved-parent-and-adb-instance", () =>
        {
            Require(WindowsAdbInterface.SelectFactoryRegistrations([
                Factory("a", ""), Factory("a", Camera, @"USB\VID_2756&PID_0009&MI_06\sample-interface-A"),
                Factory("a", @"USB\VID_2756&PID_000A\sample-camera-C"), Factory("a") with { InstanceId = "" }
            ]).Length == 0);
            Require(WindowsAdbInterface.PhysicalProductId(@"USB\VID_2756&PID_0009&MI_03\sample-interface-A") == 0);
        });
        test("windows-cfv-parent-and-factory-aliases", () =>
        {
            var cfv = Factory("a") with { Path = Factory("a").Path.Replace("0009", "000a"), InstanceId = Factory("a").InstanceId.Replace("0009", "000A"), PhysicalId = Camera.Replace("0009", "000A") };
            Require(WindowsAdbInterface.SelectFactoryRegistrations([cfv, cfv with { Path = cfv.Path.Replace("{a}", "{b}") }]).Length == 1);
            Require(WindowsAdbInterface.PhysicalDevices([Factory("a"), cfv]).Length == 2);
        });
        test("windows-setupapi-devinfo-x64-ABI", () =>
        {
            foreach (var owner in new[] { typeof(WindowsAdbInterface), typeof(WindowsUsbDiagnostics) })
            {
                var data = owner.GetNestedType("DeviceData", System.Reflection.BindingFlags.NonPublic)!;
                Require(Marshal.SizeOf(data) == 32 && Marshal.OffsetOf(data, "DevInst").ToInt32() == 20 && Marshal.OffsetOf(data, "Reserved").ToInt32() == 24);
            }
        });
        test("windows-usb-diagnostic-report-is-anonymous-metadata", () =>
        {
            var report = WindowsUsbDiagnostics.Report([
                new(Camera, Camera, "usbccgp", 0), new(Factory("a").InstanceId, Camera, "WinUSB", 0),
                new(@"USB\VID_2756&PID_0009&MI_06\private-serial", Camera, "private-service", 28),
                new(@"USB\VID_1234&PID_0001\private-phone", "", "private-service", 0)
            ], "test");
            using var json = JsonDocument.Parse(report); var root = json.RootElement;
            Require(root.GetProperty("physicalCameras").GetInt32() == 1 && root.GetProperty("nodes").GetArrayLength() == 3);
            Require(!root.GetProperty("cameraEndpointAccessed").GetBoolean() && root.GetProperty("metadataOnly").GetBoolean());
            Require(!report.Contains("sample-camera") && !report.Contains("private-") && !report.Contains("sample-interface"));
            Require(root.GetProperty("nodes")[2].GetProperty("driver").GetString() == "other" && root.GetProperty("nodes")[2].GetProperty("problemCode").GetUInt32() == 28);
        });
        test("windows-usb-diagnostics-retain-unresolved-driver-node", () =>
        {
            using var json = JsonDocument.Parse(WindowsUsbDiagnostics.Report([new(Factory("a").InstanceId, "", "", null)], "test"));
            Require(json.RootElement.GetProperty("physicalCameras").GetInt32() == 0 && json.RootElement.GetProperty("nodes")[0].GetProperty("driver").GetString() == "missing"
                && !json.RootElement.GetProperty("nodes")[0].GetProperty("topologyResolved").GetBoolean());
        });
    }
}
