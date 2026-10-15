using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using SkiaSharp;
using StartupStudio;

static class Tests
{
    static int passed;
    static void Test(string name, Action body) { body(); passed++; Console.WriteLine("PASS " + name); }
    static void Equal(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    static void Reject(Action body) { var rejected = false; try { body(); } catch (Exception e) when (e is StudioException or FormatException or JsonException) { rejected = true; } Equal(rejected); }
    static byte[] Reply(uint cookie, string output = "", uint function = 4, uint status = 0)
    {
        var bytes = new byte[257]; new byte[] { 9, 0, 5, 8, 252 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), 65); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(9), function);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(13), cookie); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(21), status);
        Encoding.UTF8.GetBytes(output).CopyTo(bytes, 25); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(17), UsbProtocol.Crc(bytes.AsSpan(21, 236))); return bytes;
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            if (args.FirstOrDefault() == "--real-adb-smoke") return RealAdbSmoke(args[1]);
            var repo = Path.GetFullPath(args.Length > 0 ? args[0] : "."); var stockPath = args.Length > 2 ? Path.GetFullPath(args[2]) : null;
            Directory.CreateDirectory(Path.Combine(repo, "outputs"));
            var temp = Path.Combine(Path.GetTempPath(), "startup-win-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
            var syntheticAdapter = args.Length < 2;
            var resources = syntheticAdapter ? Path.Combine(temp, "synthetic-resources") : Path.GetFullPath(args[1]);
            if (syntheticAdapter)
            {
                Directory.CreateDirectory(resources);
                Check.Atomic(Path.Combine(resources, "adapter.json"), Check.Encode(new Adapter(3, Adapter.StockHash, new string('a', 64), 2051168, [new(152, 16), new(513444, 4), new(513748, 4), new(1939088, 16)])));
                Check.Atomic(Path.Combine(resources, "patch.bin"), new byte[40]);
            }
            var adapter = Check.Decode<Adapter>(File.ReadAllBytes(Path.Combine(resources, "adapter.json"))); var patch = File.ReadAllBytes(Path.Combine(resources, "patch.bin"));
            try
            {
                Test("adapter-range", () => adapter.Validate(patch));
                Test("adapter-short-refused", () => Reject(() => adapter.Validate(patch[..^1])));
                Test("adapter-mode-refused", () => Reject(() => (adapter with { Mode = 2 }).Validate(patch)));
                Test("adapter-out-of-range-refused", () => Reject(() => (adapter with { Patches = [.. adapter.Patches[..3], new(2051167, patch.Length - 24)] }).Validate(patch)));
                Test("libusb-x64-native-ABI", () =>
                {
                    var assembly = typeof(UsbLink).Assembly;
                    var config = assembly.GetType("StartupStudio.NativeUsb+Config")!; var iface = assembly.GetType("StartupStudio.NativeUsb+Interface")!;
                    var alt = assembly.GetType("StartupStudio.NativeUsb+Alt")!; var ep = assembly.GetType("StartupStudio.NativeUsb+Endpoint")!;
                    Equal(Marshal.SizeOf(config) == 40 && Marshal.OffsetOf(config, "Interface").ToInt32() == 16 && Marshal.SizeOf(iface) == 16 && Marshal.SizeOf(alt) == 40 && Marshal.OffsetOf(alt, "Endpoint").ToInt32() == 16 && Marshal.SizeOf(ep) == 32 && Marshal.OffsetOf(ep, "Extra").ToInt32() == 16);
                });
                using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "CodeTests", "Fixtures", "usb-shell-request.json")));
                Test("usb-request-identical-to-C-fixture", () => Equal(Convert.ToHexStringLower(UsbProtocol.Request(fixture.RootElement.GetProperty("command").GetString()!, fixture.RootElement.GetProperty("cookie").GetUInt32())) == fixture.RootElement.GetProperty("hex").GetString()));
                Test("usb-command-limit", () => Reject(() => UsbProtocol.Request(new string('x', 232), 1)));
                Test("usb-command-utf8-limit", () => Reject(() => UsbProtocol.Request(new string('图', 78), 1)));
                Test("usb-zero-cookie", () => Reject(() => UsbProtocol.Request("x", 0)));
                Test("usb-command-nul", () => Reject(() => UsbProtocol.Request("x\0y", 1)));
                var response = Reply(123, "hello 世界\n").Concat(Reply(123, function: 1)).ToArray();
                foreach (var chunk in new[] { 1, 4, 17, 256, 257, 1024 }) Test("usb-segmented-" + chunk, () => { var parser = new ReplyParser(123); for (var at = 0; at < response.Length; at += chunk) parser.Feed(response.AsSpan(at, Math.Min(chunk, response.Length - at))); Equal(parser.Complete && parser.Result == "hello 世界"); });
                Test("usb-1024-padding", () => { var parser = new ReplyParser(123); var padded = new byte[1024]; response.CopyTo(padded, 0); parser.Feed(padded); Equal(parser.Result == "hello 世界"); });
                Test("usb-notifications-alone-not-complete", () => { var p = new ReplyParser(123); p.Feed(Reply(123, "partial")); Equal(!p.Complete); Reject(() => _ = p.Result); });
                Test("usb-foreign-cookie", () => { var p = new ReplyParser(123); p.Feed(Reply(124, "wrong").Concat(response).ToArray()); Equal(p.Foreign == 1 && p.Result == "hello 世界"); });
                Test("usb-crc-resync", () => { var p = new ReplyParser(123); var b = Reply(123, "bad"); b[100] ^= 1; p.Feed(b.Concat(response).ToArray()); Equal(p.BadCrc == 1 && p.Result == "hello 世界"); });
                Test("usb-false-header-resync", () => { var p = new ReplyParser(123); p.Feed(new byte[] { 9, 0, 5, 8, 252, 1, 2 }.Concat(response).ToArray()); Equal(p.BadCrc > 0 && p.Result == "hello 世界"); });
                Test("usb-error-status-refused", () => Reject(() => new ReplyParser(123).Feed(Reply(123, function: 1, status: 7))));
                Test("usb-buffer-limit", () => Reject(() => new ReplyParser(1).Feed(new byte[8193])));
                Test("usb-output-limit", () => { var p = new ReplyParser(1); var frame = Reply(1, new string('a', 232)); Reject(() => { for (var i = 0; i < 20000; i++) p.Feed(frame); }); });
                Test("bulk-timeout-with-final-bytes", () => { var bulk = new FakeBulk(response, -7); Equal(UsbExchange.Send(bulk, "getprop ro.product.device", 123, 1000, _ => { }) == "hello 世界" && bulk.ReadSize == 1024); });
                Test("bulk-short-write-refused", () => Reject(() => UsbExchange.Send(new FakeBulk(response) { WriteLength = 256 }, "x", 123, 1000, _ => { })));
                Test("bulk-disconnect-refused", () => Reject(() => UsbExchange.Send(new FakeBulk([], -4), "x", 123, 1000, _ => { })));
                Test("bulk-no-final-timeout", () => { long t = 0; Reject(() => UsbExchange.Send(new FakeBulk(Reply(123, "notification only"), -7), "x", 123, 20, _ => { }, () => ++t)); });
                Test("bulk-write-error-refused", () => Reject(() => UsbExchange.Send(new FakeBulk(response) { WriteCode = -1 }, "x", 123, 1000, _ => { })));
                Test("read-only-retry-whitelist", () => Equal(TransportRules.SafeReadRetry("getprop sys.usb.state") && TransportRules.SafeReadRetry("sha256sum /system/a") && !TransportRules.SafeReadRetry("sha256sum /system/a;rm /system/a") && !TransportRules.SafeReadRetry("setprop sys.usb.config none")));
                Test("adb-physical-filter", () => Equal(TransportRules.PhysicalCameras("camera device usb:1 device:eagle2_ec1706_native\n1.2.3.4:5555 device usb:1 device:eagle2_ec1706_native\nx offline usb:1 device:eagle2_ec1706_native\nphone device usb:1 device:phone").SequenceEqual(["camera"])));
                Test("adb-unsafe-path", () => Reject(() => TransportRules.ValidPath("/tmp/a;rm -rf /")));
                Test("adb-safe-path", () => TransportRules.ValidPath("/blackbox/.x2d-l3-1234abcd/image"));
                Test("windows-adb-usb-selector", () => Equal(TransportRules.UsbSerial("physical-device\r\n") == "physical-device"));
                Test("windows-adb-network-selector-refused", () => Reject(() => TransportRules.UsbSerial("192.168.1.2:5555")));
                Test("windows-adb-unknown-selector-refused", () => Reject(() => TransportRules.UsbSerial("unknown")));
                Test("adb-startup-missing-dll", () => Equal(AdbStartupFailure.Category(unchecked((int)0xc0000135), "") == "runtime"));
                Test("adb-startup-unsupported-listener", () => Equal(AdbStartupFailure.Category(1, "listening on specified hostname currently unsupported") == "listener"));
                Test("adb-startup-no-private-error-leak", () => Equal(!AdbStartupFailure.Message(1, "private-path device-identifier").Contains("private-path") && !AdbStartupFailure.Message(1, "private-path device-identifier").Contains("device-identifier")));
                var image = new StartupImage(321, 239, Enumerable.Range(0, 321 * 239).Select(i => (byte)(i % 251)).ToArray());
                Test("gray-roundtrip", () => Equal(StartupImage.Decode(image.Envelope()).Pixels.SequenceEqual(image.Pixels)));
                Test("gray-max-canvas", () => Equal(new StartupImage(1024, 768, new byte[1024 * 768]).Envelope().Length == 786464));
                Test("gray-empty-refused", () => Reject(() => new StartupImage(0, 1, []).Envelope()));
                Test("gray-width-refused", () => Reject(() => new StartupImage(1025, 1, new byte[1025]).Envelope()));
                Test("gray-height-refused", () => Reject(() => new StartupImage(1, 769, new byte[769]).Envelope()));
                Test("gray-checksum-refused", () => { var b = image.Envelope(); b[32] ^= 1; Reject(() => StartupImage.Decode(b)); });
                Test("gray-trailing-refused", () => Reject(() => StartupImage.Decode([.. image.Envelope(), 0])));
                Test("gray-reserved-refused", () => { var b = image.Envelope(); b[28] = 1; Reject(() => StartupImage.Decode(b)); });
                Test("crop-pixels", () => { var c = image.Crop(1, 1, 17, 13); Equal(c.Width == 17 && c.Height == 13 && c.Pixels[0] == image.Pixels[322]); });
                Test("crop-clamped", () => { var c = image.Crop(10000, -100, 1000, 1000); Equal(c.Width == 1 && c.Height == 239); });
                foreach (var model in new[] { Models.X2D, Models.Cfv })
                {
                    Test("model-" + model, () => Equal(Models.Detect("eagle2_ec1706_native", model == Models.X2D ? "0x0009" : "0x000A", model, (ushort)(model == Models.X2D ? 9 : 10)) == model));
                    Test("transport-model-mismatch-" + model, () => Reject(() => Models.Detect("eagle2_ec1706_native", "0x0009", model, 10)));
                }
                Test("unsupported-platform", () => Reject(() => Models.Detect("other", "0x0009", Models.X2D, 9)));
                Test("cfv-earth-refused", () => Reject(() => Models.VerifyVariant(Models.Cfv, 2)));
                var records = new List<RecoveryRecord>();
                foreach (var (model, variant, key) in new[] { (Models.X2D, 0, "KEY=\nRC=255"), (Models.X2D, 0, "KEY=MA==\nRC=0"), (Models.X2D, 2, "KEY=Mg==\nRC=0"), (Models.Cfv, 0, "KEY=\nRC=255"), (Models.Cfv, 0, "KEY=MA==\nRC=0") })
                {
                    var s = new CameraSnapshot(new string('b', 64), variant, key, "755 0 0 2051168 123\nu:object_r:system_file:s0", adapter.OriginalSHA256, model);
                    var r = new RecoveryRecord(1, s, adapter.AdapterSHA256, ["/tmp/x2d-l3-1234abcd"]); records.Add(r);
                    Test("record-" + records.Count, () => r.Validate(s with { LibraryHash = adapter.AdapterSHA256 }, adapter));
                    Test("wrong-camera-" + records.Count, () => Reject(() => r.Validate(s with { FactoryHash = new string('c', 64) }, adapter)));
                    Test("changed-key-" + records.Count, () => Reject(() => r.Validate(s with { DisplayKey = "KEY=MQ==\nRC=0" }, adapter)));
                    Test("cross-model-" + records.Count, () => Reject(() => r.Validate(s with { Model = model == Models.X2D ? Models.Cfv : Models.X2D }, adapter)));
                    Test("unknown-library-" + records.Count, () => Reject(() => r.Validate(s with { LibraryHash = new string('e', 64) }, adapter)));
                    Test("record-path-refused-" + records.Count, () => Reject(() => (r with { Roots = ["/blackbox/other-tool"] }).Validate(s, adapter)));
                    var scripts = new { record = r, install = Scripts.Install(r, adapter, Check.Hash(image.Envelope()), r.Roots[0], adapter.OriginalSHA256), update = Scripts.Install(r, adapter, Check.Hash(image.Envelope()), r.Roots[0], adapter.AdapterSHA256), restore = Scripts.Restore(r, adapter, r.Roots[0]) };
                    Test("no-factory-writers-" + records.Count, () => Equal(!scripts.install.Contains("unrd -s") && !scripts.install.Contains("prodconfig-tool --set") && !scripts.restore.Contains("unrd -s")));
                    Check.Atomic(Path.Combine(resources, "test-scripts-" + records.Count + ".json"), Check.Encode(scripts));
                    if (stockPath != null)
                    {
                        var host = new CameraController(new NeverUsb(), resources, Path.Combine(temp, "Backups-" + records.Count)); var stock = File.ReadAllBytes(stockPath);
                        Test("patch-complete-hash-" + records.Count, () => Equal(Check.Hash(adapter.Apply(stock, patch)) == adapter.AdapterSHA256));
                        Test("immutable-first-backup-" + records.Count, () => { host.SaveHost(r, stock); var first = File.ReadAllBytes(Path.Combine(host.Folder(s), "baseline.json")); host.SaveHost(r with { Roots = [.. r.Roots, "/blackbox/.x2d-l3-abcdef12"] }, stock); Equal(first.SequenceEqual(File.ReadAllBytes(Path.Combine(host.Folder(s), "baseline.json")))); });
                        Test("host-fallback-" + records.Count, () => { host.MarkActive(r); Equal(host.Fallback(s)!.Baseline.SameBaseline(s)); });
                        Test("host-changed-baseline-refused-" + records.Count, () => Reject(() => host.SaveHost(r with { Baseline = s with { Variant = 1 } })));
                    }
                }
                Test("legacy-x2d-record", () => { var b = Check.Encode(records[0]); var text = Encoding.UTF8.GetString(b).Replace(",\"model\":\"X2D 100C\"", ""); var legacy = Check.Decode<RecoveryRecord>(Encoding.UTF8.GetBytes(text)); Equal(legacy.Baseline.SameBaseline(records[0].Baseline)); legacy.Validate(records[0].Baseline, adapter); });
                Test("earth-ordinary-key-refused", () => Reject(() => Models.VerifyKey("KEY=MA==\nRC=0", 2)));
                Test("binary-key", () => Models.VerifyKey("KEY=AgAAAA==\nRC=0", 2));
                Test("read-only-inspection", () => { var link = new SnapshotLink(); var c = new CameraController(link, resources, Path.Combine(temp, "Inspect")); Equal(c.Inspect().Snapshot.CameraModel == Models.Cfv && link.Writes == 0); });
                Test("unknown-firmware-before-write", () => { var link = new SnapshotLink { LibraryHash = new string('e', 64) }; var c = new CameraController(link, resources, Path.Combine(temp, "Unknown")); Reject(() => c.Inspect()); Equal(link.Writes == 0); });
                if (stockPath != null)
                {
                    var stock = File.ReadAllBytes(stockPath);
                    foreach (var r in records)
                    {
                        var b = r.Baseline; var fixtureLink = new ControllerFixture(stock, adapter, patch, b.CameraModel, b.Variant, b.DisplayKey);
                        var c = new CameraController(fixtureLink, resources, Path.Combine(temp, "Lifecycle-" + Guid.NewGuid().ToString("N"))) { Wait = _ => { } };
                        Test("controller-adb-start-failure-before-mutation-" + b.CameraModel + "-" + b.DisplayKey, () =>
                        {
                            fixtureLink.FailNextRead = true; Reject(() => c.Install(image));
                            Equal(Check.Hash(fixtureLink.Files[CameraPaths.Library]) == adapter.OriginalSHA256 && fixtureLink.Files.Count == 1 && fixtureLink.Roots.Count == 0 && !Directory.Exists(c.BackupRoot));
                            Equal(!fixtureLink.Commands.Any(command => command.StartsWith("mount -o remount,rw") || command.StartsWith("nohup sh ")));
                        });
                        Test("controller-install-replace-restore-" + b.CameraModel + "-" + b.DisplayKey, () =>
                        {
                            c.Install(image); var first = File.ReadAllBytes(Path.Combine(c.Folder(b), "baseline.json"));
                            c.Install(image.Crop(1, 1, 17, 13)); Equal(first.SequenceEqual(File.ReadAllBytes(Path.Combine(c.Folder(b), "baseline.json"))));
                            Equal(StartupImage.Decode(fixtureLink.Files[CameraPaths.Image]).Width == 17); c.Restore(); c.Restore();
                            Equal(Check.Hash(fixtureLink.Files[CameraPaths.Library]) == adapter.OriginalSHA256 && CameraPaths.Owned.All(p => !fixtureLink.Files.ContainsKey(p)) && fixtureLink.Roots.Count == 0);
                            Equal(!File.Exists(Path.Combine(c.Folder(b), "active")) && File.Exists(Path.Combine(c.Folder(b), "original-backend.so")));
                        });
                        Test("controller-job-failure-auto-restores-" + b.CameraModel + "-" + b.DisplayKey, () =>
                        {
                            fixtureLink.FailNextJob = true; Reject(() => c.Install(image));
                            Equal(Check.Hash(fixtureLink.Files[CameraPaths.Library]) == adapter.OriginalSHA256 && CameraPaths.Owned.All(p => !fixtureLink.Files.ContainsKey(p)) && fixtureLink.Roots.Count == 0);
                        });
                    }
                }
                if (!OperatingSystem.IsWindows()) TestAdb(temp); else TestWindowsAdb();
                UsbRecoveryChecks.Run(Test);
                WindowsDeviceChecks.Run(Test);
                TestImages(temp, image);
                if (stockPath != null) RgbChecks.Recovery(Test, resources, stockPath, temp);
                TestUi(repo, image, temp, resources, stockPath, adapter, patch);
                RgbChecks.Run(Test, temp);
                ImageEditingChecks.Run(Test, temp, repo);
                Directory.CreateDirectory(Path.Combine(repo, "outputs"));
                var report = JsonSerializer.Serialize(new { passed = true, checks = passed, deviceAccessed = false, host = RuntimeInformation.OSDescription, syntheticAdapter, actualFirmwareTested = stockPath != null, windowsDriverTested = false }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(resources, "windows-tests.json"), report);
                File.WriteAllText(Path.Combine(repo, "outputs", "windows-tests-" + (OperatingSystem.IsWindows() ? "windows" : "mac") + ".json"), report);
                Console.WriteLine($"PASS {passed} checks; no USB accessed"); return 0;
            }
            finally { Directory.Delete(temp, true); }
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static void TestWindowsAdb()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Studio.FakeAdb", "release", "Studio.FakeAdb.exe"));
        Equal(File.Exists(path)); var p = new AdbProcess(path);
        Test("adb-large-output-no-deadlock", () => Equal(p.Run(["large"], selected: false).Length == 262144));
        Test("adb-permission-error", () => Reject(() => p.Run(["denied"], selected: false)));
        Test("adb-client-timeout", () => Reject(() => p.Run(["slow"], 100, false)));
        Test("adb-argument-unicode-spaces", () => Equal(Encoding.UTF8.GetString(p.Run(["argument", "图片 空格 ' $()"], selected: false)) == "图片 空格 ' $()"));
        Test("windows-adb-d-argument", () => { p.UsbOnly = true; p.Serial = "ignored-serial"; Equal(Encoding.UTF8.GetString(p.Run(["argument", "physical USB"])) == "physical USB"); });
        Test("windows-adb-multiple-usb-error", () => Reject(() => p.Run(["many"])));
        Test("windows-adb-private-client-address", () => { p.ServerPort = 49152; Equal(Encoding.UTF8.GetString(p.Run(["argument"])) == "127.0.0.1:49152"); });
        Test("owned-adb-server-lifecycle", () => { var server = new OwnedAdbServer(path); var pid = server.ProcessId; Equal(server.Running && server.Port > 1024); server.Dispose(); var exited = false; try { using var process = Process.GetProcessById(pid); exited = process.HasExited; } catch (ArgumentException) { exited = true; } Equal(exited); });
        Test("owned-adb-start-failure-is-cleaned-up", () => { var before = Environment.GetEnvironmentVariable("STARTUP_STUDIO_FAKE_FAIL"); try { Environment.SetEnvironmentVariable("STARTUP_STUDIO_FAKE_FAIL", "1"); Reject(() => new OwnedAdbServer(path)); } finally { Environment.SetEnvironmentVariable("STARTUP_STUDIO_FAKE_FAIL", before); } });
    }
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    static void TestAdb(string temp)
    {
        var path = Path.Combine(temp, "fake adb"); File.WriteAllText(path, "#!/bin/sh\nif [ \"$1\" = '-H' ];then printf '%s:%s' \"$2\" \"$4\";exit;fi\nif [ \"$1\" = '-d' ];then shift;fi\ncase \"$1\" in\nlarge) /usr/bin/head -c 262144 /dev/zero;;\ndenied) echo 'Permission denied' >&2;exit 1;;\nmany) echo 'more than one device/emulator' >&2;exit 1;;\nslow) exec /bin/sleep 3;;\nargument) printf '%s' \"$2\";;\nesac\n"); File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var p = new AdbProcess(path);
        Test("adb-large-output-no-deadlock", () => Equal(p.Run(["large"], selected: false).Length == 262144));
        Test("adb-permission-error", () => Reject(() => p.Run(["denied"], selected: false)));
        Test("adb-client-timeout", () => Reject(() => p.Run(["slow"], 100, false)));
        Test("adb-argument-unicode-spaces", () => Equal(Encoding.UTF8.GetString(p.Run(["argument", "图片 空格 ' $()"], selected: false)) == "图片 空格 ' $()"));
        Test("windows-adb-d-argument", () => { p.UsbOnly = true; p.Serial = "ignored-serial"; Equal(Encoding.UTF8.GetString(p.Run(["argument", "physical USB"])) == "physical USB"); });
        Test("windows-adb-multiple-usb-error", () => Reject(() => p.Run(["many"])));
        Test("windows-adb-private-client-address", () => { p.ServerPort = 49152; Equal(Encoding.UTF8.GetString(p.Run(["argument"])) == "127.0.0.1:49152"); });
        var serverPath = Path.Combine(temp, "fake-server");
        File.WriteAllText(serverPath, "#!/usr/bin/env python3\nimport socket,sys\nassert sys.argv[1]=='-L' and sys.argv[2].startswith('tcp:localhost:') and sys.argv[3:]==['nodaemon','server']\ns=socket.socket();s.bind(('127.0.0.1',int(sys.argv[2].rsplit(':',1)[1])));s.listen()\nwhile True:\n c,a=s.accept()\n with c:\n  b=b''\n  while len(b)<16:\n   p=c.recv(16-len(b))\n   if not p:break\n   b+=p\n  if b==b'000chost:version':c.sendall(b'OKAY00040029')\n");
        File.SetUnixFileMode(serverPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Test("owned-adb-server-lifecycle", () => { var server = new OwnedAdbServer(serverPath); var pid = server.ProcessId; Equal(server.Running && server.Port > 1024); server.Dispose(); var exited = false; try { using var process = Process.GetProcessById(pid); exited = process.HasExited; } catch (ArgumentException) { exited = true; } Equal(exited); });
        Test("owned-adb-start-failure-is-cleaned-up", () => Reject(() => new OwnedAdbServer(path, 1000)));
    }
    static int RealAdbSmoke(string executable)
    {
        Equal(OperatingSystem.IsWindows()); executable = Path.GetFullPath(executable);
        Check.That(WindowsAdbInterface.SupportedDeviceCount == 0, "真实 ADB 离线检查前必须断开相机；不能只依赖 --one-device 过滤隔离真实硬件。");
        Check.That(WindowsAdbInterface.RegisteredInterfaceClasses().Any(), "Windows 设备接口类枚举不可用。");
        var version = Encoding.UTF8.GetString(new AdbProcess(executable).Run(["version"], selected: false)); Equal(version.Contains("Android Debug Bridge version"));
        var diagnostics = new List<string>(); int pid, port;
        using (var server = new OwnedAdbServer(executable, diagnostic: diagnostics.Add, deviceFilter: "startup-studio-offline-no-device"))
        {
            pid = server.ProcessId; port = server.Port; Equal(server.Running);
            var client = new AdbProcess(executable) { ServerPort = server.Port };
            var list = Encoding.UTF8.GetString(client.Run(["devices"], selected: false)); Equal(list.Trim() == "List of devices attached");
        }
        var gone = false; try { using var process = Process.GetProcessById(pid); gone = process.HasExited; } catch (ArgumentException) { gone = true; } Equal(gone);
        Directory.CreateDirectory("outputs/windows-adb-probe");
        File.WriteAllText("outputs/windows-adb-probe/production-result.json", JsonSerializer.Serialize(new { passed = true, realBundledAdb = true, productionServer = true, protocolReady = true, processExited = gone, port, deviceAccessed = false, cameraTested = false, diagnostics }));
        Console.WriteLine("PASS real bundled Windows ADB: version, production listener, private client, no devices, owned process exit"); return 0;
    }
    static void TestImages(string temp, StartupImage image)
    {
        var png = Path.Combine(temp, "gray.png");
        Test("gray-png-export-import-exact", () => { Images.Export(image, png); var decoded = Images.Import(png); Equal(decoded.Width == image.Width && decoded.Height == image.Height && decoded.Pixels.SequenceEqual(image.Pixels)); });
        Test("rgba-black-composite", () => { using var b = new SKBitmap(2, 1); b.SetPixel(0, 0, new SKColor(255, 0, 0, 255)); b.SetPixel(1, 0, new SKColor(0, 255, 0, 0)); using var bytes = b.Encode(SKEncodedImageFormat.Png, 100); File.WriteAllBytes(png, bytes.ToArray()); var d = Images.Import(png); Equal(d.Pixels.SequenceEqual(new byte[] { 76, 0 })); });
        Test("png-oversize-automatically-fits", () => { using var b = new SKBitmap(1025, 1); using var bytes = b.Encode(SKEncodedImageFormat.Png, 100); File.WriteAllBytes(png, bytes.ToArray()); var fitted = Images.Import(png); Equal(fitted.Width == 1024 && fitted.Height == 1); });
        Test("corrupt-image-refused", () => { File.WriteAllText(png, "invalid"); Reject(() => Images.Import(png)); });
        for (var origin = 1; origin <= 8; origin++) { var o = origin; Test("exif-orientation-map-" + o, () => { var w = o >= 5 ? 2 : 3; var h = o >= 5 ? 3 : 2; var coords = new HashSet<(int, int)>(); for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) { var p = Images.Orient(o, x, y, 3, 2); Equal(p.X is >= 0 and < 3 && p.Y is >= 0 and < 2); coords.Add(p); } Equal(coords.Count == 6); }); }
        using var jpegBitmap = new SKBitmap(3, 2);
        byte[] tones = [40, 80, 120, 160, 200, 240];
        for (var i = 0; i < 6; i++) jpegBitmap.SetPixel(i % 3, i / 3, new SKColor(tones[i], tones[i], tones[i]));
        using var jpeg = jpegBitmap.Encode(SKEncodedImageFormat.Jpeg, 100);
        for (var origin = 1; origin <= 8; origin++)
        {
            var o = origin; Test("actual-jpeg-exif-decode-" + o, () =>
            {
                byte[] exif = [69, 120, 105, 102, 0, 0, 73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0, (byte)o, 0, 0, 0, 0, 0, 0, 0];
                var bytes = jpeg.ToArray(); var path = Path.Combine(temp, "orientation.jpg");
                File.WriteAllBytes(path, [.. bytes[..2], 255, 225, 0, (byte)(exif.Length + 2), .. exif, .. bytes[2..]]);
                var d = Images.Import(path); Equal(d.Width == (o >= 5 ? 2 : 3) && d.Height == (o >= 5 ? 3 : 2));
                for (var y = 0; y < d.Height; y++) for (var x = 0; x < d.Width; x++) { var (sx, sy) = Images.Orient(o, x, y, 3, 2); Equal(Math.Abs(d.Pixels[y * d.Width + x] - tones[sy * 3 + sx]) <= 2); }
            });
        }
    }
    static void TestUi(string repo, StartupImage image, string temp, string resources, string? stockPath, Adapter adapter, byte[] patch)
    {
        AppBuilder.Configure<StudioApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
        var window = new StudioWindow();
        Test("ui-no-auto-connect", () => Equal(!window.Install.IsEnabled && !window.Restore.IsEnabled && window.Connect.IsEnabled));
        Test("ui-import-preview", () => { window.SetImage(image, "offline.png"); Equal(window.Preview.Image!.Pixels.SequenceEqual(Images.CameraCanvas(image).Pixels) && !window.Install.IsEnabled); });
        Test("preview-native-integer-centering", () => Equal(CameraPreview.CenterRect(image) == new Rect(351, 264, 321, 239)));
        Test("ui-crop-exact", () => { window.Mode.SelectedIndex = 1; window.Editor.Crop = new Rect(1, 1, 17, 13); Equal(window.Output!.Width == 1024 && window.Output.Pixels[((768 - 13) / 2) * 1024 + (1024 - 17) / 2] == image.Pixels[322]); });
        Test("ui-contain-reset", () => { window.Mode.SelectedIndex = 0; Equal(window.Output!.Width == 1024 && window.Output.Height == 768 && window.Output.Pixels.SequenceEqual(Images.CameraCanvas(image).Pixels)); });
        Test("ui-real-controls-render", () => { window.Width = 1260; window.Height = 800; window.Show(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = window.CaptureRenderedFrame(); Equal(frame != null); frame!.Save(Path.Combine(repo, "outputs", "windows-ui-preview.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); });
        Test("ui-real-mouse-can-move-crop", () =>
        {
            window.Mode.SelectedIndex = 1; window.Editor.Crop = new Rect(30, 30, 160, 120); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var k = Math.Min((window.Editor.Bounds.Width - 24) / image.Width, (window.Editor.Bounds.Height - 24) / image.Height);
            var point = new Point((window.Editor.Bounds.Width - image.Width * k) / 2 + 100 * k, (window.Editor.Bounds.Height - image.Height * k) / 2 + 80 * k);
            var start = window.Editor.TranslatePoint(point, window)!.Value; var end = start + new Vector(20 * k, 10 * k);
            window.MouseDown(start, Avalonia.Input.MouseButton.Left); window.MouseMove(end, Avalonia.Input.RawInputModifiers.LeftMouseButton); window.MouseUp(end, Avalonia.Input.MouseButton.Left);
            Equal(Math.Abs(window.Editor.Crop.X - 50) < 1 && Math.Abs(window.Editor.Crop.Y - 40) < 1); window.Mode.SelectedIndex = 0;
        });
        Test("ui-small-window-layout", () => { window.Width = 1080; window.Height = 640; Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); var bottom = window.Install.TranslatePoint(new Point(0, window.Install.Bounds.Height), window); Equal(bottom != null && bottom.Value.Y <= window.Bounds.Height - 20 && window.Preview.Bounds.Height >= 160); using var frame = window.CaptureRenderedFrame(); Equal(frame != null); frame!.Save(Path.Combine(repo, "outputs", "windows-ui-small.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); });
        Test("ui-connection-button-shares-bottom-right-action-row", () =>
        {
            var connect = window.Connect.TranslatePoint(default, window)!.Value; var restore = window.Restore.TranslatePoint(default, window)!.Value; var install = window.Install.TranslatePoint(default, window)!.Value;
            Equal(Math.Abs(connect.Y - restore.Y) < 2 && Math.Abs(connect.Y - install.Y) < 2 && connect.X < restore.X && restore.X < install.X && connect.Y > window.Bounds.Height - 100);
        });
        Test("ui-color-preview-and-grayscale-selection", () =>
        {
            window.SetImage(RgbChecks.Colors, "rgb.png"); Equal(window.Output!.Channels == 4 && window.Preview.Image!.Pixels.SequenceEqual(Images.CameraCanvas(RgbChecks.Colors).Pixels));
            window.Colors.SelectedIndex = 1; Equal(window.Output!.Channels == 1 && window.Output.Pixels.SequenceEqual(Images.CameraCanvas(RgbChecks.Colors.Grayscale()).Pixels));
            window.Colors.SelectedIndex = 0; window.SetImage(image, "offline.png");
        });
        Test("ui-friendly-errors", () => Equal(!StudioWindow.FriendlyFailure("LIBUSB_ERROR_TIMEOUT").Contains("LIBUSB") && StudioWindow.FriendlyFailure("LIBUSB_ERROR_IO\n自动恢复未确认").Contains("保留电脑备份")));
        Test("ui-preserves-first-failure-with-cleanup-failure", () =>
        {
            var text = StudioWindow.FriendlyFailure("等待文件传输通道超时，安装尚未开始。\n收尾失败：USB 原状态恢复未确认");
            Equal(text.StartsWith("等待文件传输通道超时") && text.Contains("另外") && text.Contains("USB 连接状态"));
            Equal(StudioWindow.FriendlyFailure("等待文件传输通道超时\nUSB 原状态恢复未确认").StartsWith("等待文件传输通道超时"));
        });
        Test("ui-completed-install-is-distinct-from-cleanup-failure", () =>
        {
            Equal(StudioWindow.CompletionStatus(1, "普通版", "USB 原状态恢复未确认").StartsWith("安装完成；USB"));
            Equal(StudioWindow.CompletionStatus(2, "大地版", "USB 原状态恢复未确认").StartsWith("已恢复大地版原厂画面；USB"));
        });
        void PumpOffline(Task task) { while (!task.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(2); } task.GetAwaiter().GetResult(); Dispatcher.UIThread.RunJobs(); }
        window.ResourcesPath = resources; window.BackupRoot = Path.Combine(temp, "CleanupBackup"); window.DiagnosticsRoot = Path.Combine(temp, "CleanupLogs");
        Test("ui-finishes-once-and-retains-completed-inspection", () =>
        {
            var link = new CleanupFixture(false); window.LinkFactory = (_, _) => link;
            PumpOffline(window.Perform(0));
            Equal(link.Finished == 1 && window.Status.Text!.StartsWith("相机检查完成；USB") && !window.Install.IsEnabled && window.Connect.IsEnabled);
        });
        Test("ui-operation-and-cleanup-failures-remain-visible", () =>
        {
            var link = new CleanupFixture(true); window.LinkFactory = (_, _) => link;
            PumpOffline(window.Perform(0));
            Equal(link.Finished == 1 && window.Status.Text!.StartsWith("等待文件传输通道超时") && window.Status.Text.Contains("USB 连接状态") && !window.Install.IsEnabled);
        });
        if (stockPath != null)
        {
            var fixture = new ControllerFixture(File.ReadAllBytes(stockPath), adapter, patch, Models.X2D, 2, "KEY=Mg==\nRC=0");
            fixture.Files["/tmp/x2d-native-menu-attempt"] = "existing"u8.ToArray();
            window.ResourcesPath = resources; window.BackupRoot = Path.Combine(temp, "UiBackup"); window.DiagnosticsRoot = Path.Combine(temp, "UiLogs"); window.LinkFactory = (_, _) => fixture;
            void Pump(Task task) { while (!task.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(2); } task.GetAwaiter().GetResult(); Dispatcher.UIThread.RunJobs(); }
            Test("ui-detect-and-buttons", () => { var task = window.Perform(0); Equal(!window.Connect.IsEnabled && !window.Import.IsEnabled); Pump(task); Equal(window.Install.IsEnabled && !window.Restore.IsEnabled); });
            Test("ui-install-and-menu-reboot-notice", () => { Pump(window.Perform(1)); Equal(window.Restore.IsEnabled && window.Status.Text!.Contains("关机再开机") && fixture.Files.ContainsKey("/tmp/x2d-native-menu-attempt")); });
            Test("ui-failed-display-service-preserves-restore-entry", () =>
            {
                fixture.ServicesFailed = true; Pump(window.Perform(0)); Equal(window.Restore.IsEnabled && !window.Install.IsEnabled);
                fixture.ServicesFailed = false;
            });
            Test("ui-restore-earth-and-preserve-other-menu-guard", () => { Pump(window.Perform(2)); if (window.Restore.IsEnabled || !window.Status.Text!.Contains("大地版") || !fixture.Files.ContainsKey("/tmp/x2d-native-menu-attempt") || fixture.Finished != 4) throw new Exception($"Restore UI: status={window.Status.Text}, enabled={window.Restore.IsEnabled}, finish={fixture.Finished}"); });
            Test("ui-connection-failure-disables-install", () => { window.LinkFactory = (_, _) => throw new StudioException("LIBUSB_ERROR_BUSY"); Pump(window.Perform(0)); Equal(!window.Install.IsEnabled && !window.Restore.IsEnabled && window.Connect.IsEnabled && window.Status.Text!.Contains("其他程序")); });
        }
        window.Close();
    }
    sealed class NeverUsb : ICameraLink { public ushort? ProductId => null; public string Command(string text, uint timeout = 12000) => throw new Exception("Unexpected device access in offline test"); }
    sealed class CleanupFixture(bool failOperation) : IManagedCameraLink
    {
        readonly SnapshotLink snapshot = new(); public int Finished;
        public ushort? ProductId => snapshot.ProductId;
        public string Command(string text, uint timeout = 12000) => failOperation ? throw new StudioException("等待文件传输通道超时，安装尚未开始。") : snapshot.Command(text, timeout);
        public byte[] ReadFile(string path, int size) => throw new Exception("Unexpected file read");
        public void WriteFile(byte[] bytes, string path) => throw new Exception("Unexpected file write");
        public void Finish() { Finished++; throw new StudioException("USB 原状态恢复未确认"); }
        public void Dispose() { }
    }
    sealed class FakeBulk(byte[] response, int readCode = 0) : IUsbBulk
    {
        bool sent, delivered; public int ReadSize; public int WriteLength = 257, WriteCode;
        public BulkResult Transfer(byte endpoint, byte[] bytes, uint timeout)
        {
            if (endpoint == 4) { sent = true; Equal(bytes.Length == 257); return new(WriteCode, WriteLength); }
            ReadSize = bytes.Length; Equal(bytes.Length == 1024);
            if (!sent || delivered) return new(-7, 0); delivered = true; response.CopyTo(bytes, 0); return new(readCode, response.Length);
        }
    }
    sealed class SnapshotLink : ICameraLink
    {
        public ushort? ProductId => 10; public int Writes; public string LibraryHash = Adapter.StockHash;
        public string Command(string text, uint timeout = 12000)
        {
            if (text.Contains("mount ") || text.Contains("setprop ") || text.Contains("nohup ")) { Writes++; throw new Exception("Unexpected write"); }
            return text switch
            {
                "getprop ro.product.device" => "eagle2_ec1706_native", "getprop hbl.usb_pid" => "0x000a", "getprop hbl.usb_prod_name" => Models.Cfv,
                "sha256sum /factory_data/settings.ini" => new string('b', 64) + "  settings.ini", "prodconfig-tool --print 2>&1 | sed -n '/^SuVariant:/p'" => "SuVariant: 0",
                Scripts.KeyCommand => "KEY=\nRC=255", "sha256sum " + CameraPaths.Library => LibraryHash + "  library",
                _ when text.StartsWith("stat -c '") => "755 0 0 2051168 123", _ when text.StartsWith("ls -Z ") => "u:object_r:system_file:s0",
                _ when text.StartsWith("[ -f ") || text.StartsWith("[ ! -L ") => "0", _ when text.StartsWith("[ -e ") => "1",
                _ => throw new Exception("Unexpected offline command")
            };
        }
    }
}
