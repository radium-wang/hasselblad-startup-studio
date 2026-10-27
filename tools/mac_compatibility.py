"""离线审查 App 中每个 Mach-O 的架构、最低系统及动态依赖。"""
import json
import plistlib
import re
import subprocess
from pathlib import Path

ARCHITECTURES = ('arm64', 'x86_64')
MINIMUM_MACOS = '13.0'
MACHO_MAGIC = {bytes.fromhex(x) for x in ('feedface', 'cefaedfe', 'feedfacf', 'cffaedfe', 'cafebabe', 'bebafeca', 'cafebabf', 'bfbafeca')}

def output(*args):
    return subprocess.check_output(list(map(str, args)), text=True)

def architectures(path):
    return set(output('lipo', '-archs', path).split())

def minimum_versions(commands):
    values = re.findall(r'cmd LC_BUILD_VERSION.*?\n\s*minos ([\d.]+)', commands, re.S)
    values += re.findall(r'cmd LC_VERSION_MIN_MACOSX\n\s*cmdsize \d+\n\s*version ([\d.]+)', commands)
    return values

def version_tuple(value):
    return tuple((list(map(int, value.split('.'))) + [0, 0])[:3])

def require_architectures(path, required):
    found = architectures(path)
    if not set(required) <= found:
        raise RuntimeError(f'{Path(path).name} 缺少架构：{sorted(set(required) - found)}')

def audit_app(app, required=ARCHITECTURES):
    app = Path(app).resolve()
    executable = app/'Contents/MacOS/StartupStudio'
    info = plistlib.loads((app/'Contents/Info.plist').read_bytes())
    if info.get('LSMinimumSystemVersion') != MINIMUM_MACOS:
        raise RuntimeError('Info.plist 最低系统与构建目标不一致')
    if info.get('CFBundleIconFile') != 'AppIcon' or not (app/'Contents/Resources/AppIcon.icns').is_file() or not (app/'Contents/Resources/AppIcon.png').is_file():
        raise RuntimeError('App 缺少已登记的图标资源')
    binaries = []
    for path in sorted(app.rglob('*')):
        if path.is_file():
            with path.open('rb') as file:
                if file.read(4) in MACHO_MAGIC:
                    if app not in path.resolve().parents:
                        raise RuntimeError('App 二进制链接到包外文件')
                    binaries.append(path)
    required_binaries = {executable, app/'Contents/Frameworks/libusb-1.0.0.dylib', app/'Contents/Resources/bin/adb'}
    if not required_binaries <= set(binaries):
        raise RuntimeError('缺少 App 主程序、USB 库或 ADB 可执行文件')
    report = []
    for path in binaries:
        require_architectures(path, required)
        slices = []
        for arch in required:
            commands = output('otool', '-arch', arch, '-l', path)
            versions = minimum_versions(commands)
            if not versions or any(version_tuple(v) > version_tuple(MINIMUM_MACOS) for v in versions):
                raise RuntimeError(f'{path.name} 的 {arch} 最低系统超出 {MINIMUM_MACOS}：{versions}')
            rpaths = re.findall(r'cmd LC_RPATH\n\s*cmdsize \d+\n\s*path (.*?) \(offset', commands)
            if any(p.startswith('/') and not p.startswith(('/usr/lib/', '/System/Library/')) for p in rpaths):
                raise RuntimeError(f'{path.name} 包含宿主专用 rpath')
            deps = [line.strip().split(' (')[0] for line in output('otool', '-arch', arch, '-L', path).splitlines() if line.startswith('\t')]
            for dep in deps:
                if dep.startswith(('/usr/lib/', '/System/Library/')):
                    continue
                def resolve(value):
                    if value.startswith(('/usr/lib/', '/System/Library/')):
                        return Path(value)
                    if value.startswith('@loader_path/'):
                        return path.parent/value[len('@loader_path/'):]
                    if value.startswith('@executable_path/'):
                        return executable.parent/value[len('@executable_path/'):]
                    raise RuntimeError(f'{path.name} 存在未打包依赖：{value}')
                if dep.startswith('@rpath/'):
                    # The main executable owns the framework rpath used by libusb.
                    candidates = [app/'Contents/Frameworks'/dep[len('@rpath/'):]]
                    candidates += [resolve(r)/dep[len('@rpath/'):] for r in rpaths]
                else:
                    candidates = [resolve(dep)]
                if not any(p.resolve().is_file() and app in p.resolve().parents for p in candidates):
                    raise RuntimeError(f'{path.name} 动态依赖不存在或位于 App 外：{dep}')
            slices.append(dict(architecture=arch, minimumVersions=versions, dependencies=deps))
        report.append(dict(path=str(path.relative_to(app)), architectures=sorted(architectures(path)), slices=slices))
    return dict(minimumMacOS=MINIMUM_MACOS, architectures=list(required), deviceAccessed=False, binaries=report)

if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('app', type=Path)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    args.report.write_text(json.dumps(audit_app(args.app), indent=2)+'\n')
    print('PASS: 全部 Mach-O 双架构、macOS 13 最低系统与内置动态依赖核对')
