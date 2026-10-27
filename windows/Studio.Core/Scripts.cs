using System.Globalization;
namespace StartupStudio;

public static class Scripts
{
    public static string Metadata(RecoveryRecord r, string path)
    {
        var lines = r.Baseline.Metadata.Split('\n'); var parts = lines[0].Split(' ');
        if (!Check.Match(r.Baseline.Metadata, "[0-7]{3,4} [0-9]+ [0-9]+ 2051168 [0-9]+\\nu:object_r:[A-Za-z0-9_]+:s0") || !long.TryParse(parts[4], out var epoch)) return "exit 33\n";
        var stamp = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime.ToString("yyyyMMddHHmm.ss", CultureInfo.InvariantCulture);
        return $"chmod {parts[0]} {path}\nchown {parts[1]}:{parts[2]} {path}\nTZ=UTC toybox touch -m -t {stamp} {path}\n[ \"$(stat -c '%a %u %g %s %Y' {path})\" = {Check.Quote(lines[0])} ]\ncase \"$(ls -Z {path})\" in *{lines[1]}*) ;; *) exit 32;; esac\n";
    }
    public static string Header(string root) => $"#!/system/bin/sh\nset -eu\nexport PATH=/system/bin:/system/xbin:/sbin\nfinish() {{ rc=$?; mount -o remount,ro /system; if [ \"$rc\" = 0 ]; then echo OK > {root}/result; else start weston; start camera-gui; echo FAILED > {root}/result; fi; }}\ntrap finish EXIT\nhashok() {{ set -- $(sha256sum \"$1\") \"$2\"; [ \"$1\" = \"$3\" ]; }}";
    public const string KeyCommand = "set -o pipefail;v=$(unrd -p /dev/block/by-name/env -g su_variant 2>/dev/null | toybox base64);r=$?;printf 'KEY=%s\\nRC=%s' \"$v\" \"$r\"";
    public static string Factory(RecoveryRecord r) => "hashok /factory_data/settings.ini " + r.Baseline.FactoryHash + "\n[ \"$(set +e;" + KeyCommand + ")\" = " + Check.Quote(r.Baseline.DisplayKey) + " ]\n";
    public static string Restart() => "sync\nmount -o remount,ro /system\nstart weston\nsleep 3\n[ \"$(getprop init.svc.weston)\" = running ]\nstart camera-gui\nsleep 3\n[ \"$(getprop init.svc.camera-gui)\" = running ]";
    public static string Install(RecoveryRecord r, Adapter a, string imageHash, string root, string current)
    {
        var s = Header(root) + "\n" + Factory(r) + $"hashok {CameraPaths.Library} {current}\nhashok {root}/image {imageHash}\nmount -o remount,rw /system\n";
        s += $"if [ ! -e {CameraPaths.Backup} ]; then hashok {CameraPaths.Library} {a.OriginalSHA256}; cp -p {CameraPaths.Library} {CameraPaths.Backup};\n" + Metadata(r, CameraPaths.Backup) + $"fi\nhashok {CameraPaths.Backup} {a.OriginalSHA256}\nsync\ncp -p {CameraPaths.Backup} {CameraPaths.Stage}\n";
        var skip = 0;
        foreach (var p in a.Patches) { s += $"toybox dd if={root}/patch of={CameraPaths.Stage} bs=1 skip={skip} seek={p.Offset} count={p.Size} conv=notrunc 2>/dev/null\n"; skip += p.Size; }
        var label = r.Baseline.Metadata.Split('\n')[^1];
        s += $"hashok {CameraPaths.Stage} {a.AdapterSHA256}\ncase \"$(ls -Z {CameraPaths.Stage})\" in *{label}*) ;; *) exit 32;; esac\nLD_PRELOAD={CameraPaths.Stage} /system/bin/weston --version >/dev/null 2>&1\ncp {root}/image {CameraPaths.ImageStage}\nchmod 644 {CameraPaths.ImageStage}\nhashok {CameraPaths.ImageStage} {imageHash}\nsync\ncase \"$(ls -Z {CameraPaths.ImageStage})\" in *{label}*) ;; *) exit 32;; esac\n";
        s += $"stop camera-gui\nstop weston\nmv -f {CameraPaths.Stage} {CameraPaths.Library}\nmv -f {CameraPaths.ImageStage} {CameraPaths.Image}\nprintf '%s' 3 > {CameraPaths.Mode}\nchmod 644 {CameraPaths.Mode}\ncase \"$(ls -Z {CameraPaths.Mode})\" in *{label}*) ;; *) exit 32;; esac\n";
        return s + Restart() + "\n" + Factory(r) + $"hashok {CameraPaths.Library} {a.AdapterSHA256}\nhashok {CameraPaths.Backup} {a.OriginalSHA256}\n";
    }
    public static string Restore(RecoveryRecord r, Adapter a, string root) => Header(root) + "\n" + Factory(r) + $"current=$(sha256sum {CameraPaths.Library});current=${{current%% *}}\ncase \"$current\" in {a.OriginalSHA256}|{a.AdapterSHA256}) ;; *) exit 31;; esac\nusebackup=0\nif [ -e {CameraPaths.Backup} ]; then if hashok {CameraPaths.Backup} {a.OriginalSHA256}; then usebackup=1; else hashok {CameraPaths.Library} {a.OriginalSHA256}; fi; fi\nstop camera-gui\nstop weston\nmount -o remount,rw /system\nif [ \"$usebackup\" = 1 ]; then cp -p {CameraPaths.Backup} {CameraPaths.Stage}; hashok {CameraPaths.Stage} {a.OriginalSHA256}; mv -f {CameraPaths.Stage} {CameraPaths.Library}; fi\nhashok {CameraPaths.Library} {a.OriginalSHA256}\n" + Metadata(r, CameraPaths.Library) + $"\nrm -f {CameraPaths.Mode} {CameraPaths.Image} {CameraPaths.ImageStage} {CameraPaths.Stage}\n" + Restart() + "\n" + Factory(r) + "\n";
}
