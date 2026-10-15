using System.Text;
using System.Text.RegularExpressions;
namespace StartupStudio;

public sealed class CameraController
{
    public ICameraLink Link { get; }
    public Adapter Adapter { get; }
    public byte[] Patch { get; }
    public AdapterCatalog Catalog { get; }
    public string BackupRoot { get; }
    public Action<string, double> Progress { get; set; } = (_, _) => { };
    public Action<int> Wait { get; set; } = Thread.Sleep;
    public CameraController(ICameraLink link, string resources, string backupRoot)
    {
        Link = link; BackupRoot = backupRoot;
        Adapter = Check.Decode<Adapter>(File.ReadAllBytes(Path.Combine(resources, "adapter.json")));
        Patch = File.ReadAllBytes(Path.Combine(resources, "patch.bin")); Adapter.Validate(Patch);
        Catalog = new(resources, Adapter);
    }
    public string Call(string command, uint timeout = 12000) => Link.Command(command, timeout);
    public string Hash(string path)
    {
        var h = Call("sha256sum " + path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        Check.That(Check.IsHash(h), "无法校验相机文件。"); return h;
    }
    public bool Exists(string path) => Call("[ -e " + path + " ] || [ -L " + path + " ];echo $?") == "0";
    public string Identify() => Models.Detect(Call("getprop ro.product.device"), Call("getprop hbl.usb_pid"), Call("getprop hbl.usb_prod_name"), Link.ProductId);
    public string Label(string path)
    {
        var match = Regex.Match(Call("ls -Z " + path), "u:object_r:[A-Za-z0-9_]+:s0");
        Check.That(match.Success, "无法核对原厂文件标签。"); return match.Value;
    }
    public CameraSnapshot Snapshot()
    {
        var model = Identify(); var factory = Hash("/factory_data/settings.ini");
        var text = Call("prodconfig-tool --print 2>&1 | sed -n '/^SuVariant:/p'");
        var match = Regex.Match(text, "[0-9]+$");
        Check.That(match.Success && int.TryParse(match.Value, out _), "无法确认相机原厂版本。");
        var variant = int.Parse(match.Value); Models.VerifyVariant(model, variant);
        var key = Call(Scripts.KeyCommand); Models.VerifyKey(key, variant);
        var metadata = Call("stat -c '%a %u %g %s %Y' " + CameraPaths.Library) + "\n" + Label(CameraPaths.Library);
        Check.That(Call("[ -f " + CameraPaths.Library + " ] && [ ! -L " + CameraPaths.Library + " ];echo $?") == "0", "绘图库不是原始常规文件。");
        return new(factory, variant, key, metadata, Hash(CameraPaths.Library), model);
    }
    public byte[] Read(string path)
    {
        Check.That(int.TryParse(Call("stat -c %s " + path), out var size) && size is >= 0 and <= 4 * 1024 * 1024, "文件大小无效。");
        if (size > 16384 && Link is IFileCameraLink files) return files.ReadFile(path, size);
        using var result = new MemoryStream(size);
        for (var offset = 0; offset < size; offset += 16384)
        {
            var data = Convert.FromBase64String(Call($"toybox dd if={path} bs=16384 skip={offset / 16384} count=1 2>/dev/null | toybox base64", 30000));
            Check.That(data.Length == Math.Min(16384, size - offset), "相机文件分块读取不完整。"); result.Write(data);
        }
        return result.ToArray();
    }
    public RecoveryRecord? Record()
    {
        if (!Exists(CameraPaths.Journal)) return null;
        try { return Check.Decode<RecoveryRecord>(Read(CameraPaths.Journal)); }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or StudioException) { throw new StudioException("恢复记录损坏；请保留电脑恢复包，暂勿安装。"); }
    }
    public string Folder(CameraSnapshot s) { Check.That(Check.IsHash(s.FactoryHash), "设备指纹无效。"); return Path.Combine(BackupRoot, s.FactoryHash); }
    public void SaveHost(RecoveryRecord r, byte[]? stock = null)
    {
        var dir = Folder(r.Baseline); Directory.CreateDirectory(dir);
        var baseline = Path.Combine(dir, "baseline.json"); var original = Path.Combine(dir, "original-backend.so");
        if (File.Exists(baseline))
        {
            var old = Check.Decode<RecoveryRecord>(File.ReadAllBytes(baseline));
            Catalog.For(old); Catalog.For(r);
            Check.That(old.Baseline.SameBaseline(r.Baseline), "已有首次恢复基线不符，禁止覆盖。");
        }
        else Check.Atomic(baseline, Check.Encode(r), false);
        if (stock != null)
        {
            Check.That(Check.Hash(stock) == Adapter.OriginalSHA256, "原厂备份校验失败。");
            if (File.Exists(original)) Check.That(Check.Hash(File.ReadAllBytes(original)) == Adapter.OriginalSHA256, "首次原库备份损坏。");
            else Check.Atomic(original, stock, false);
        }
        Check.Atomic(Path.Combine(dir, "transaction.json"), Check.Encode(r));
    }
    public void MarkActive(RecoveryRecord r) => Check.Atomic(Path.Combine(Folder(r.Baseline), "active"), "pending"u8.ToArray());
    public RecoveryRecord? Fallback(CameraSnapshot s) => File.Exists(Path.Combine(Folder(s), "active")) ? Check.Decode<RecoveryRecord>(File.ReadAllBytes(Path.Combine(Folder(s), "transaction.json"))) : null;
    public void CheckHost(RecoveryRecord r)
    {
        var dir = Folder(r.Baseline); var first = Check.Decode<RecoveryRecord>(File.ReadAllBytes(Path.Combine(dir, "baseline.json")));
        Catalog.For(first); Catalog.For(r);
        Check.That(first.Baseline.SameBaseline(r.Baseline), "电脑备份与相机恢复记录不符。");
        Check.That(Check.Hash(File.ReadAllBytes(Path.Combine(dir, "original-backend.so"))) == Adapter.OriginalSHA256, "电脑原厂备份缺失或损坏。请使用原先备份所在电脑，或迁移整份备份。");
    }
    public void CheckFactory(RecoveryRecord r)
    {
        Check.That(Identify() == r.Baseline.CameraModel && Hash("/factory_data/settings.ini") == r.Baseline.FactoryHash && Call(Scripts.KeyCommand) == r.Baseline.DisplayKey, "原厂身份或显示键发生变化，已停止操作。");
    }
    public void Upload(byte[] bytes, string path)
    {
        Check.That(Link is IFileCameraLink, "文件传输通道未准备好。"); ((IFileCameraLink)Link).WriteFile(bytes, path);
    }
    public void WriteJournal(RecoveryRecord r, string root)
    {
        var encoded = Check.Encode(r); Upload(encoded, root + "/owner");
        Check.That(Call("mount -o remount,rw /system;echo $?") == "0", "系统分区无法写入。");
        try
        {
            Check.That(Call($"set -e;cp {root}/owner {CameraPaths.JournalStage};chmod 600 {CameraPaths.JournalStage};echo OK") == "OK", "恢复记录写入失败。");
            Check.That(Hash(CameraPaths.JournalStage) == Check.Hash(encoded), "恢复记录校验失败。");
            Check.That(Call($"mv -f {CameraPaths.JournalStage} {CameraPaths.Journal};sync;echo OK") == "OK", "恢复记录提交失败。");
        }
        finally { try { Call("sync;mount -o remount,ro /system"); } catch (Exception) { /* Original failure remains actionable; host transaction remains. */ } }
    }
    public string MakeRoot()
    {
        var root = "/blackbox/.x2d-l3-" + Guid.NewGuid().ToString("N")[..8];
        Check.That(!Exists(root), "暂存目录已存在，已停止操作。"); return root;
    }
    public void CreateRoot(string root, RecoveryRecord r) => Check.That(Call("umask 077;mkdir " + root + " && printf '%s' " + r.Baseline.FactoryHash + " > " + root + "/owner-id;echo $?") == "0", "无法创建安装暂存目录。");
    public void CleanRoot(string root, RecoveryRecord r)
    {
        if (!Exists(root)) return;
        Check.That(Call("[ ! -L " + root + " ];echo $?") == "0" && Call("cat " + root + "/owner-id 2>/dev/null") == r.Baseline.FactoryHash, "暂存目录归属不符，未删除。");
        Check.That(Call("rm -rf " + root + ";echo $?") == "0", "暂存目录清理失败。");
    }
    public void RunScript(string text, string root, string name)
    {
        Upload(Encoding.UTF8.GetBytes(text), root + "/" + name);
        Check.That(Call($"nohup sh {root}/{name} > {root}/log 2>&1 </dev/null & echo STARTED", 1000) == "STARTED", "相机任务未启动。");
        for (var i = 0; i < 90; i++)
        {
            Wait(500); var status = Call($"if [ -f {root}/result ];then cat {root}/result;fi", 2000);
            if (status == "OK") return;
            if (status == "FAILED") throw new StudioException("相机任务校验失败，请使用恢复按钮。");
        }
        throw new StudioException("相机任务未确认完成，请重新连接后恢复。");
    }
    public void ServicesAndMapping(bool active)
    {
        Check.That(Call("getprop init.svc.weston") == "running" && Call("getprop init.svc.camera-gui") == "running", "显示服务尚未恢复。");
        var mounts = Call("cat /proc/mounts | sed -n '\\| /system |p'").Split(' ');
        Check.That(mounts.Length > 3 && mounts[3].StartsWith("ro,"), "系统未恢复只读。");
        var pid = Call("pidof weston"); Check.That(Check.Match(pid, "[0-9]+"), "无法确认显示进程。");
        var lines = Call("cat /proc/" + pid + "/maps | sed -n '\\|eagle-backend.so|p'").Split('\n').Where(l => l.Contains("r-xp 00000000") && l.EndsWith(CameraPaths.Library)).ToArray();
        Check.That(lines.Length == 1 && ulong.TryParse(lines[0].Split('-')[0], System.Globalization.NumberStyles.HexNumber, null, out _), "无法核对实际加载的绘图库。");
        var address = ulong.Parse(lines[0].Split('-')[0], System.Globalization.NumberStyles.HexNumber);
        var stock = File.ReadAllBytes(Path.Combine(Folder(Snapshot()), "original-backend.so")); var want = active ? Adapter.Apply(stock, Patch) : stock;
        foreach (var offset in new[] { 513444, 513748 })
        {
            var got = Call($"od -An -tx1 -N4 -j {address + (uint)offset} /proc/{pid}/mem");
            var expected = string.Join(" ", want[offset..(offset + 4)].Select(b => b.ToString("x2")));
            Check.That(string.Join(" ", got.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) == expected, "实际绘图库分支核验失败。");
        }
    }
    public (CameraSnapshot Snapshot, RecoveryRecord? Record) Inspect()
    {
        var s = Snapshot(); Check.That(Catalog.Recognized(s.LibraryHash), "当前固件或绘图库不受支持。");
        foreach (var p in CameraPaths.Owned) Check.That(Call("[ ! -L " + p + " ];echo $?") == "0", "检测到异常链接，禁止修改。");
        var r = Record() ?? Fallback(s);
        if (r != null) { r.Validate(s, Catalog.For(r)); CheckHost(r); }
        else
        {
            foreach (var p in CameraPaths.Owned) Check.That(!Exists(p), "检测到未归属的旧修改文件，请使用原先电脑完成恢复。");
            Check.That(s.LibraryHash == Adapter.OriginalSHA256, "缺少这台相机的恢复记录。");
        }
        if (r == null && File.Exists(Path.Combine(Folder(s), "original-backend.so"))) ServicesAndMapping(false);
        return (s, r);
    }
    public bool HasMenuRetryGuard() => Exists("/tmp/x2d-native-menu-attempt");
    public void Install(StartupImage image)
    {
        var envelope = image.Envelope(); var (s, old) = Inspect();
        Check.That(old == null || old.AdapterHash == Adapter.AdapterSHA256, "检测到旧版灰度安装；请先使用本软件恢复原厂，再安装新版彩色画面。首次备份不会覆盖。");
        var root = MakeRoot(); RecoveryRecord r;
        Progress("正在保存并核对首次恢复备份", 0);
        if (old != null)
        {
            Check.That(Hash(CameraPaths.Backup) == Adapter.OriginalSHA256, "机内原库备份损坏。");
            r = old with { Roots = [.. old.Roots, root] }; SaveHost(r);
        }
        else { r = new(1, s, Adapter.AdapterSHA256, [root]); SaveHost(r, Read(CameraPaths.Library)); }
        r.Validate(s, Adapter);
        try
        {
            MarkActive(r); CreateRoot(root, r); WriteJournal(r, root); Progress("正在上传自定义画面", .4);
            Upload(Patch, root + "/patch"); Upload(envelope, root + "/image"); CheckFactory(r);
            Progress("正在安装并核对显示服务", .7);
            RunScript(Scripts.Install(r, Adapter, Check.Hash(envelope), root, s.LibraryHash), root, "install.sh");
            Check.That(Hash(CameraPaths.Library) == Adapter.AdapterSHA256 && Hash(CameraPaths.Image) == Check.Hash(envelope) && Read(CameraPaths.Mode).SequenceEqual("3"u8.ToArray()), "安装读回校验失败。");
            CheckFactory(r); ServicesAndMapping(true); foreach (var p in r.Roots) CleanRoot(p, r);
        }
        catch (Exception installation)
        {
            Progress("安装未完成，正在尝试恢复原厂", 0);
            try { Restore(); } catch (Exception recovery) { throw new StudioException(installation.Message + "\n自动恢复未确认：" + recovery.Message + "\n请保留电脑备份并重新连接后恢复。"); }
            throw new StudioException("安装未通过检查，已自动恢复原厂。请保留恢复包并反馈结果。");
        }
    }
    public void Restore()
    {
        var (s, found) = Inspect(); if (found == null) return;
        var restoreAdapter = Catalog.For(found);
        var root = MakeRoot(); var r = found with { Roots = [.. found.Roots, root] }; r.Validate(s, restoreAdapter);
        SaveHost(r); MarkActive(r); CreateRoot(root, r); WriteJournal(r, root); CheckFactory(r);
        if (Exists(CameraPaths.Backup))
        {
            if (Hash(CameraPaths.Backup) != Adapter.OriginalSHA256) Check.That(s.LibraryHash == Adapter.OriginalSHA256 && s.Metadata == r.Baseline.Metadata, "原厂备份哈希不符，禁止恢复。");
        }
        else Check.That(s.LibraryHash == Adapter.OriginalSHA256, "机内原库备份缺失，已停止操作。");
        Progress("正在恢复这台相机的原厂画面", 0);
        RunScript(Scripts.Restore(r, restoreAdapter, root), root, "restore.sh");
        var restored = Snapshot(); Check.That(restored.LibraryHash == Adapter.OriginalSHA256 && restored.Metadata == r.Baseline.Metadata, "原厂库或文件属性未完全恢复。");
        CheckFactory(r); ServicesAndMapping(false);
        Check.That(Call("mount -o remount,rw /system;echo $?") == "0", "无法完成最后清理。");
        try
        {
            foreach (var p in CameraPaths.Owned) Call("rm -f " + p);
            Call("sync;mount -o remount,ro /system"); foreach (var p in r.Roots) CleanRoot(p, r);
            foreach (var p in CameraPaths.Owned.Concat(r.Roots)) Check.That(!Exists(p), "恢复成功，但安装文件尚未清理完成。");
            File.Delete(Path.Combine(Folder(r.Baseline), "active")); Progress("已恢复原厂，安装文件已清理", 1);
        }
        finally { try { Call("sync;mount -o remount,ro /system"); } catch (Exception) { /* Do not hide a failed cleanup. */ } }
    }
}
