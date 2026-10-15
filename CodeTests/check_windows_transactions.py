"""执行 Windows 版实际生成的相机 shell；只使用临时桌面文件系统，不连接设备。"""
import argparse
import hashlib
import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run_case(source, adapter, stock, patch, case, shell="/bin/bash"):
    record = case['record']
    original_root = record['roots'][0]
    baseline = record['baseline']
    label = baseline['metadata'].splitlines()[1]
    key, rc = baseline['displayKey'].splitlines()
    import base64
    key_text = base64.b64decode(key[4:]).decode()
    with tempfile.TemporaryDirectory(prefix='startup-win-shell-') as temp:
        base = Path(temp)
        library = base / 'system/lib64/weston/eagle-backend.so'
        library.parent.mkdir(parents=True)
        (base / 'system/bin').mkdir()
        root = base / original_root.lstrip('/')
        root.mkdir(parents=True)
        (base / 'factory_data').mkdir()
        weston = base / 'system/bin/weston'
        weston.write_text('#!/bin/sh\nexit 0\n')
        weston.chmod(0o755)
        host_tools = base / 'host-tools.py'
        host_tools.write_text('''import base64,hashlib,os,sys
from pathlib import Path
command,*args=sys.argv[1:]
if command=='hash':
 p=Path(args[0]);print(hashlib.sha256(p.read_bytes()).hexdigest()+'  '+str(p))
elif command=='chmod':os.chmod(args[1],int(args[0],8))
elif command=='base64':sys.stdout.write(base64.b64encode(sys.stdin.buffer.read()).decode())
elif command=='dd':
 o=dict(a.split('=',1) for a in args);b=int(o['bs']);data=Path(o['if']).read_bytes();begin=int(o.get('skip','0'))*b;data=data[begin:begin+int(o['count'])*b]
 with open(o['of'],'r+b') as f:f.seek(int(o['seek'])*b);f.write(data)
''', encoding='utf-8')
        helper = shlex.quote(Path(sys.executable).as_posix()) + ' ' + shlex.quote(host_tools.as_posix())
        hash_command = helper + ' hash' if os.name == 'nt' else '/usr/bin/shasum -a 256'
        shim = f"""
mount() {{ return 0; }}
start() {{ return 0; }}
stop() {{ return 0; }}
chown() {{ return 0; }}
sleep() {{ return 0; }}
sync() {{ return 0; }}
getprop() {{ echo running; }}
unrd() {{ printf '%s' {shlex.quote(key_text)}; return {rc[3:]}; }}
sha256sum() {{ case "$1" in */factory_data/settings.ini) echo '{baseline['factoryHash']}  settings.ini';; *) {hash_command} "$1";; esac; }}
ls() {{ printf '%s\\n' '{label}'; }}
stat() {{ printf '%s\\n' '{baseline['metadata'].splitlines()[0]}'; }}
toybox() {{ cmd=$1;shift; "$cmd" "$@"; }}
"""
        if os.name == 'nt':
            shim += f'base64() {{ {helper} base64; }}\nchmod() {{ {helper} chmod "$@"; }}\ndd() {{ {helper} dd "$@"; }}\n'
        def execute(text, expected=0):
            text = text.replace('export PATH=/system/bin:/system/xbin:/sbin', 'export PATH=/usr/bin:/bin:/usr/sbin:/sbin')
            text = text.replace('/system/', (base / 'system').as_posix() + '/').replace('/factory_data/', (base / 'factory_data').as_posix() + '/').replace(original_root, root.as_posix())
            if os.name == 'nt':
                # Windows cannot load an Android ELF; run the same Weston stub
                # without passing LD_PRELOAD into MSYS's own dynamic loader.
                text = re.sub(r'LD_PRELOAD=\S+ ', '', text)
            text = text.replace('set -eu', 'set -eu\n' + shim, 1)
            script = base / 'transaction.sh'
            script.write_text(text)
            subprocess.run([shell, '-n', str(script)], check=True)
            result = subprocess.run([shell, str(script)], capture_output=True, timeout=15)
            assert result.returncode == expected, result.stderr.decode(errors='replace')[-3000:]

        backup = Path(str(library) + '.logo3-original')
        owned = [backup, Path(str(library) + '.logo3-next')]
        owned += [library.parent / n for n in ['x2d-startup-mode3', 'x2d-startup-gray3.bin', 'x2d-startup-gray3.bin.next']]
        def reset():
            for path in owned:
                path.unlink(missing_ok=True)
            shutil.copyfile(stock, library)
            library.chmod(0o755)
            os.utime(library, (123, 123))
            shutil.copyfile(patch, root / 'patch')
            image = bytes((i % 251 for i in range(321 * 239)))
            import struct
            fnv = 2166136261
            for byte in image:
                fnv = ((fnv ^ byte) * 16777619) & 0xffffffff
            (root / 'image').write_bytes(b'X2DLOGO3' + struct.pack('<6I', 1, 321, 239, len(image), fnv, 0) + image)

        reset()
        execute(case['install'])
        assert digest(library) == adapter['adapterSHA256']
        assert digest(backup) == adapter['originalSHA256']
        execute(case['update'])
        assert digest(backup) == adapter['originalSHA256']
        execute(case['restore'])
        execute(case['restore'])
        assert digest(library) == adapter['originalSHA256']
        assert library.stat().st_mtime == 123
        if os.name != 'nt':
            assert library.stat().st_mode & 0o777 == 0o755
        assert not any(p.exists() for p in owned[1:])
        reset()
        backup.write_bytes(b'partial')
        execute(case['restore'])
        assert digest(library) == adapter['originalSHA256']
        reset()
        execute(case['install'])
        backup.write_bytes(b'damaged')
        before = library.read_bytes()
        execute(case['restore'], expected=1)
        assert library.read_bytes() == before
        boundaries = ['cp -p /system/lib64/weston/eagle-backend.so.logo3-original', 'toybox dd', 'LD_PRELOAD=', 'cp ' + original_root + '/image', 'stop camera-gui', 'mv -f /system/lib64/weston/eagle-backend.so.logo3-next', 'mv -f /system/lib64/weston/x2d-startup-gray3.bin.next', "printf '%s' 3", 'chmod 644 /system/lib64/weston/x2d-startup-mode3', '\nstart weston\n']
        for boundary in boundaries:
            reset()
            index = case['install'].index(boundary)
            execute(case['install'][:index] + '\nexit 85\n', expected=85)
            execute(case['restore'])
            assert digest(library) == adapter['originalSHA256']
            assert not any(p.exists() for p in owned[1:])
        restore_boundaries = ['stop camera-gui', 'cp -p /system/lib64/weston/eagle-backend.so.logo3-original', 'hashok /system/lib64/weston/eagle-backend.so.logo3-next', 'mv -f /system/lib64/weston/eagle-backend.so.logo3-next', 'chmod 755 /system/lib64/weston/eagle-backend.so', '\nstart weston\n', '\nstart camera-gui\n']
        for boundary in restore_boundaries:
            reset()
            execute(case['install'])
            before = backup.read_bytes()
            index = case['restore'].index(boundary)
            execute(case['restore'][:index] + '\nexit 85\n' + case['restore'][index:], expected=85)
            assert backup.read_bytes() == before
            execute(case['restore'])
            assert digest(library) == adapter['originalSHA256'] and backup.read_bytes() == before
        reset()
        execute(case['install'])
        failed_service = case['restore'].replace('start weston\nsleep 3', 'start weston\nexit 86\nsleep 3')
        execute(failed_service, expected=86)
        assert digest(backup) == adapter['originalSHA256']
        execute(case['restore'])
        assert digest(library) == adapter['originalSHA256']
        print('PASS actual Windows shell: install / replace / restore / corrupt backup / 10 install + 7 restore interruptions / failed service withdrawal:', baseline['model'], baseline['variant'], rc, flush=True)
        return len(boundaries) + len(restore_boundaries)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--resources', type=Path, required=True)
    parser.add_argument('--stock', type=Path, required=True)
    parser.add_argument('--shell', default='/bin/bash')
    args = parser.parse_args()
    adapter = json.loads((args.resources / 'adapter.json').read_text())
    assert digest(args.stock) == adapter['originalSHA256']
    count = 0
    cases = sorted(args.resources.glob('test-scripts-*.json'))
    assert len(cases) == 5
    for case in cases:
        count += run_case(args.resources, adapter, args.stock, args.resources / 'patch.bin', json.loads(case.read_text()), args.shell)
    (args.resources / 'windows-transactions.json').write_text(json.dumps(dict(passed=True, scenarios=5, interruptions=count, deviceAccessed=False, servicesAndLabels='fixtures; not real camera validation', windowsPermissionsSimulated=os.name=='nt', failedNativeWithdrawalScenarios=5), indent=2) + '\n')
    print('PASS 5 scenarios and', count, 'interruption recoveries; no device access')


if __name__ == '__main__':
    main()
