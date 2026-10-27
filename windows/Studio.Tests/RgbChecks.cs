using System.Buffers.Binary;
using SkiaSharp;
using StartupStudio;

static class RgbChecks
{
    static void Require(bool value) { if (!value) throw new Exception("RGB / recovery assertion failed"); }
    static void Reject(Action action) { try { action(); } catch (StudioException) { return; } throw new Exception("Expected recovery refusal"); }
    public static readonly StartupImage Colors = new(3, 2, [0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0, 0, 0, 255, 255, 0, 255, 255, 0, 0, 255, 0, 255, 0], 4);
    public static void Run(Action<string, Action> test, string temp)
    {
        test("rgb-bgrx-protocol-roundtrip", () => { var bytes = Colors.Envelope(); var decoded = StartupImage.Decode(bytes); Require(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) == 2 && decoded.Channels == 4 && decoded.Pixels.SequenceEqual(Colors.Pixels)); });
        test("rgb-maximum-resource-bounded", () => Require(new StartupImage(1024, 768, new byte[1024 * 768 * 4], 4).Envelope().Length == 3145760));
        test("rgb-format-version-and-length-mismatch-refused", () => { var bytes = Colors.Envelope(); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1); Reject(() => StartupImage.Decode(bytes)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 3); Reject(() => StartupImage.Decode(bytes)); });
        test("rgb-corrupt-and-trailing-resource-refused", () => { var bytes = Colors.Envelope(); bytes[^4] ^= 1; Reject(() => StartupImage.Decode(bytes)); Reject(() => StartupImage.Decode([.. Colors.Envelope(), 0])); });
        test("rgb-unsupported-channel-layout-refused", () => Reject(() => new StartupImage(1, 1, [1, 2, 3], 3).Envelope()));
        test("rgb-crop-preserves-all-channels", () => { var crop = Colors.Crop(1, 0, 2, 2); Require(crop.Channels == 4 && crop.Pixels.SequenceEqual(Colors.Pixels[4..12].Concat(Colors.Pixels[16..24]))); });
        test("rgb-grayscale-explicit-conversion", () => Require(Colors.Grayscale().Pixels.SequenceEqual(new byte[] { 76, 150, 29, 226, 179, 105 })));
        test("rgb-png-export-import-preserves-channel-order", () => { var path = Path.Combine(temp, "colors.png"); Images.Export(Colors, path); var decoded = Images.Import(path, color: true); Require(decoded.Channels == 4 && decoded.Pixels.SequenceEqual(Colors.Pixels)); });
        test("rgb-alpha-composites-against-black", () =>
        {
            using var bitmap = new SKBitmap(new SKImageInfo(2, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul)); bitmap.SetPixel(0, 0, new SKColor(255, 0, 0, 128)); bitmap.SetPixel(1, 0, new SKColor(0, 255, 0, 0));
            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100); var path = Path.Combine(temp, "rgba.png"); File.WriteAllBytes(path, png.ToArray()); Require(Images.Import(path, true).Pixels.SequenceEqual(new byte[] { 0, 0, 128, 0, 0, 0, 0, 0 }));
        });
        test("rgb-bitmap-preview-is-bgra-opaque", () =>
        {
            using var bitmap = Images.Bitmap(Colors); using var pixels = bitmap.Lock(); var bytes = new byte[12]; System.Runtime.InteropServices.Marshal.Copy(pixels.Address, bytes, 0, bytes.Length);
            Require(bytes.SequenceEqual(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255 }));
        });
    }
    public static void Recovery(Action<string, Action> test, string resources, string stockPath, string temp)
    {
        var stock = File.ReadAllBytes(stockPath); var adapter = Check.Decode<Adapter>(File.ReadAllBytes(Path.Combine(resources, "adapter.json"))); var patch = File.ReadAllBytes(Path.Combine(resources, "patch.bin"));
        foreach (var scenario in new[] { "job", "services", "interrupted", "cleanup" })
        {
            test("rgb-restore-retains-records-after-" + scenario + "-failure", () =>
            {
                var fixture = new ControllerFixture(stock, adapter, patch, Models.X2D, 0, "KEY=\nRC=255");
                var c = new CameraController(fixture, resources, Path.Combine(temp, "Recovery-" + scenario)) { Wait = _ => { } }; c.Install(Colors);
                var folder = c.Folder(c.Snapshot()); var baseline = File.ReadAllBytes(Path.Combine(folder, "baseline.json")); var original = File.ReadAllBytes(Path.Combine(folder, "original-backend.so"));
                fixture.FailRestoreJob = scenario == "job"; fixture.FailRestoreServices = scenario == "services"; fixture.RestorationInterrupted = scenario == "interrupted"; fixture.FailCleanup = scenario == "cleanup";
                Reject(c.Restore);
                Require(File.Exists(Path.Combine(folder, "active")) && File.Exists(Path.Combine(folder, "transaction.json")) && File.ReadAllBytes(Path.Combine(folder, "baseline.json")).SequenceEqual(baseline) && File.ReadAllBytes(Path.Combine(folder, "original-backend.so")).SequenceEqual(original));
                Require(fixture.Files.ContainsKey(CameraPaths.Journal) && Check.Hash(fixture.Files[CameraPaths.Backup]) == Adapter.StockHash && c.Inspect().Record != null);
                fixture.FailRestoreJob = fixture.FailRestoreServices = fixture.FailCleanup = fixture.ServicesFailed = false;
                c.Restore(); c.Restore(); Require(Check.Hash(fixture.Files[CameraPaths.Library]) == Adapter.StockHash && !File.Exists(Path.Combine(folder, "active")) && baseline.SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "baseline.json"))) && File.Exists(Path.Combine(folder, "original-backend.so")));
            });
        }
        var historical = Path.Combine(resources, "legacy", AdapterCatalog.LegacyHash);
        if (!Directory.Exists(historical)) return;
        test("rgb-known-grayscale-install-restores-before-upgrade-preserving-first-backup", () =>
        {
            var old = Check.Decode<Adapter>(File.ReadAllBytes(Path.Combine(historical, "adapter.json"))); var oldPatch = File.ReadAllBytes(Path.Combine(historical, "patch.bin"));
            var fixture = new ControllerFixture(stock, old, oldPatch, Models.X2D, 0, "KEY=\nRC=255"); var backup = Path.Combine(temp, "Migration");
            var oldController = new CameraController(fixture, historical, backup) { Wait = _ => { } }; oldController.Install(Colors.Grayscale());
            var folder = oldController.Folder(oldController.Snapshot()); var first = File.ReadAllBytes(Path.Combine(folder, "baseline.json"));
            var current = new CameraController(fixture, resources, backup) { Wait = _ => { } }; Require(current.Inspect().Record!.AdapterHash == old.AdapterSHA256); Reject(() => current.Install(Colors)); current.Restore();
            Require(Check.Hash(fixture.Files[CameraPaths.Library]) == Adapter.StockHash && first.SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "baseline.json"))));
            // Share exact device state with the new payload fixture for the next install.
            var next = new ControllerFixture(stock, adapter, patch, Models.X2D, 0, "KEY=\nRC=255"); var upgraded = new CameraController(next, resources, backup) { Wait = _ => { } }; upgraded.Install(Colors); upgraded.Restore();
            Require(first.SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "baseline.json"))) && Check.Hash(File.ReadAllBytes(Path.Combine(folder, "original-backend.so"))) == Adapter.StockHash);
        });
    }
}
