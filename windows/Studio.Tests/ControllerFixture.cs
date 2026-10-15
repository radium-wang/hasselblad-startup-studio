using System.Text;
using System.Text.RegularExpressions;
using StartupStudio;

// State-machine fixture for production controller sequencing. Shell execution is checked separately.
internal sealed class ControllerFixture : IManagedCameraLink
{
    internal readonly Dictionary<string, byte[]> Files = [];
    internal readonly HashSet<string> Roots = [];
    internal readonly List<string> Commands = [];
    internal bool FailNextJob;
    internal bool FailNextRead;
    internal int Finished;
    internal bool FailRestoreJob;
    internal bool FailRestoreServices;
    internal bool FailCleanup;
    internal bool RestorationInterrupted;
    internal bool ServicesFailed;
    public void Finish() => Finished++;
    public void Dispose() { }
    readonly Adapter adapter;
    readonly byte[] patch;
    readonly string model, key;
    readonly int variant;
    internal const string Fingerprint = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public ushort? ProductId => (ushort)(model == Models.X2D ? 9 : 10);
    internal ControllerFixture(byte[] stock, Adapter adapter, byte[] patch, string model, int variant, string key)
    {
        this.adapter = adapter; this.patch = patch; this.model = model; this.variant = variant; this.key = key;
        Files[CameraPaths.Library] = stock.ToArray();
    }
    public byte[] ReadFile(string path, int size) { if (FailNextRead) { FailNextRead = false; throw new StudioException("文件传输服务启动失败"); } var bytes = Files[path]; Check.That(size == bytes.Length, "Fixture size"); return bytes.ToArray(); }
    public void WriteFile(byte[] bytes, string path) => Files[path] = bytes.ToArray();
    public string Command(string text, uint timeout = 12000)
    {
        Commands.Add(text);
        var metadata = "755 0 0 2051168 123";
        if (text == "getprop ro.product.device") return "eagle2_ec1706_native";
        if (text == "getprop hbl.usb_pid") return model == Models.X2D ? "0x0009" : "0x000a";
        if (text == "getprop hbl.usb_prod_name") return model;
        if (text == "sha256sum /factory_data/settings.ini") return Fingerprint + "  settings.ini";
        if (text.StartsWith("sha256sum ")) { var path = text[10..]; return Check.Hash(Files[path]) + "  " + path; }
        if (text.StartsWith("prodconfig-tool --print")) return "SuVariant: " + variant;
        if (text == Scripts.KeyCommand) return key;
        if (text.StartsWith("stat -c '%a")) return metadata;
        if (text.StartsWith("stat -c %s ")) return Files[text[11..]].Length.ToString();
        if (text.StartsWith("ls -Z ")) return "u:object_r:system_file:s0";
        if (text.StartsWith("[ -f ")) return "0";
        if (text.StartsWith("[ ! -L ")) return "0";
        if (text.StartsWith("[ -e ")) { var path = text.Split(' ')[2]; return Files.ContainsKey(path) || Roots.Contains(path) ? "0" : "1"; }
        if (text.StartsWith("toybox dd if="))
        {
            var path = text[13..].Split(' ')[0]; var block = int.Parse(Regex.Match(text, "skip=([0-9]+)").Groups[1].Value); var bytes = Files[path];
            return Convert.ToBase64String(bytes.Skip(block * 16384).Take(16384).ToArray());
        }
        if (text.StartsWith("mount -o remount,rw")) return "0";
        if (text == "sync;mount -o remount,ro /system") return "";
        if (text.StartsWith("umask 077;mkdir "))
        {
            var root = text[16..].Split(' ')[0]; Roots.Add(root); Files[root + "/owner-id"] = Encoding.ASCII.GetBytes(Fingerprint); return "0";
        }
        if (text.StartsWith("set -e;cp "))
        {
            var parts = text[10..].Split(' '); Files[parts[1].Split(';')[0]] = Files[parts[0]].ToArray(); return "OK";
        }
        if (text.StartsWith("mv -f ")) { var p = text[6..].Split(' '); Files[p[1].Split(';')[0]] = Files[p[0]]; Files.Remove(p[0]); return "OK"; }
        if (text.StartsWith("nohup sh "))
        {
            var path = text[9..].Split(' ')[0]; var root = path[..path.LastIndexOf('/')];
            if (FailRestoreJob && path.EndsWith("restore.sh")) { Files[root + "/result"] = "FAILED"u8.ToArray(); return "STARTED"; }
            if (FailNextJob) { FailNextJob = false; Files[root + "/result"] = "FAILED"u8.ToArray(); return "STARTED"; }
            if (path.EndsWith("install.sh"))
            {
                if (!Files.ContainsKey(CameraPaths.Backup)) Files[CameraPaths.Backup] = Files[CameraPaths.Library].ToArray();
                Files[CameraPaths.Library] = adapter.Apply(Files[CameraPaths.Backup], patch);
                Files[CameraPaths.Image] = Files[root + "/image"].ToArray(); Files[CameraPaths.Mode] = "3"u8.ToArray();
            }
            else
            {
                if (Files.TryGetValue(CameraPaths.Backup, out var original)) { Files[CameraPaths.Library] = original.ToArray(); }
                foreach (var p in new[] { CameraPaths.Mode, CameraPaths.Image, CameraPaths.ImageStage, CameraPaths.Stage }) Files.Remove(p);
            }
            if (path.EndsWith("restore.sh"))
            {
                if (FailRestoreServices) ServicesFailed = true;
                if (RestorationInterrupted) { RestorationInterrupted = false; throw new StudioException("模拟恢复中断"); }
            }
            Files[root + "/result"] = "OK"u8.ToArray(); return "STARTED";
        }
        if (text.StartsWith("if [ -f ")) { var path = text.Split(' ')[3]; return Files.TryGetValue(path, out var result) ? Encoding.UTF8.GetString(result) : ""; }
        if (text.StartsWith("getprop init.svc.")) return ServicesFailed ? "stopped" : "running";
        if (text.StartsWith("cat /proc/mounts")) return "/dev/system /system ext4 ro,relatime 0 0";
        if (text == "pidof weston") return "123";
        if (text.Contains("/maps |")) return "10000000-11000000 r-xp 00000000 00:00 1 " + CameraPaths.Library;
        if (text.StartsWith("od -An"))
        {
            var address = ulong.Parse(Regex.Match(text, "-j ([0-9]+)").Groups[1].Value); var offset = (int)(address - 0x10000000);
            return string.Join(" ", Files[CameraPaths.Library][offset..(offset + 4)].Select(b => b.ToString("x2")));
        }
        if (text.StartsWith("cat ")) return Encoding.UTF8.GetString(Files[text[4..].Split(' ')[0]]);
        if (text.StartsWith("rm -rf ")) { var root = text[7..].Split(';')[0]; Roots.Remove(root); foreach (var path in Files.Keys.Where(p => p.StartsWith(root + "/")).ToArray()) Files.Remove(path); return "0"; }
        if (text.StartsWith("rm -f ")) { if (FailCleanup) throw new StudioException("模拟清理中断"); Files.Remove(text[6..]); return ""; }
        throw new Exception("Unimplemented controller fixture command: " + text);
    }
}
