"""独立验证 Windows 包的文件、架构、依赖、哈希与私有数据边界；不执行 EXE。"""
import argparse
import hashlib
import json
import struct
import zipfile
from pathlib import Path


def pe(path):
    data = path.read_bytes()
    assert data[:2] == b'MZ', path.name
    at = struct.unpack_from('<I', data, 0x3c)[0]
    assert data[at:at + 4] == b'PE\0\0'
    machine, sections = struct.unpack_from('<HH', data, at + 4)
    optional_size = struct.unpack_from('<H', data, at + 20)[0]
    optional = at + 24
    magic = struct.unpack_from('<H', data, optional)[0]
    directory = optional + (112 if magic == 0x20b else 96)
    import_rva = struct.unpack_from('<I', data, directory + 8)[0]
    maps = []
    for i in range(sections):
        off = optional + optional_size + i * 40
        size, rva, raw_size, raw = struct.unpack_from('<IIII', data, off + 8)
        maps.append((rva, max(size, raw_size), raw))
    def offset(rva):
        for start, size, raw in maps:
            if start <= rva < start + size:
                return raw + rva - start
        raise AssertionError('Unmapped PE RVA')
    imports = []
    if import_rva:
        off = offset(import_rva)
        while any(data[off:off + 20]):
            name = offset(struct.unpack_from('<I', data, off + 12)[0])
            imports.append(data[name:data.index(0, name)].decode('ascii').lower())
            off += 20
    return machine, imports


def audit(root):
    manifest = json.loads((root / 'package-manifest.json').read_text())
    expected = manifest['files']
    files = {p.relative_to(root).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in root.rglob('*') if p.is_file() and p.name != 'package-manifest.json'}
    assert expected == files, 'Manifest / file tree mismatch'
    assert manifest['target'] == 'win-x64' and not manifest['realWindowsValidated']
    for name in ['HasselbladStartupStudio.exe', 'libusb-1.0.dll', 'coreclr.dll', 'hostfxr.dll', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll']:
        assert pe(root / name)[0] == 0x8664, 'Wrong x64 architecture: ' + name
    adb = root / 'Resources/bin'
    adb_arch = pe(adb / 'adb.exe')[0]
    assert adb_arch in (0x14c, 0x8664)
    assert all(pe(adb / n)[0] == adb_arch for n in ['AdbWinApi.dll', 'AdbWinUsbApi.dll'])
    assert (adb / 'NOTICE.txt').stat().st_size > 10000
    known_system = {'kernel32.dll', 'advapi32.dll', 'user32.dll', 'gdi32.dll', 'msvcrt.dll', 'shell32.dll', 'shlwapi.dll', 'ole32.dll', 'oleaut32.dll', 'ws2_32.dll', 'setupapi.dll', 'cfgmgr32.dll', 'winusb.dll', 'iphlpapi.dll', 'crypt32.dll', 'ntdll.dll', 'bcrypt.dll', 'version.dll', 'winmm.dll', 'comdlg32.dll', 'comctl32.dll', 'secur32.dll', 'dnsapi.dll', 'netapi32.dll', 'ucrtbase.dll', 'powrprof.dll', 'normaliz.dll', 'mswsock.dll', 'psapi.dll'}
    known_system.update({'dbghelp.dll', 'fontsub.dll', 'bcryptprimitives.dll', 'dxgi.dll'})
    for path in [root / 'libusb-1.0.dll', root / 'libSkiaSharp.dll', root / 'libHarfBuzzSharp.dll', root / 'av_libglesv2.dll', root / 'coreclr.dll', root / 'hostfxr.dll', adb / 'adb.exe', adb / 'AdbWinApi.dll', adb / 'AdbWinUsbApi.dll']:
        for dependency in pe(path)[1]:
            assert dependency in known_system or dependency.startswith(('api-ms-win-', 'ext-ms-win-')) or (path.parent / dependency).exists() or any(p.name.lower() == dependency for p in path.parent.iterdir()), 'Missing non-system dependency: ' + dependency
    assert not any(p.name in ['original-backend.so', 'baseline.json', 'transaction.json', 'settings.ini'] or p.suffix in ['.log', '.cim', '.so'] for p in root.rglob('*') if p.is_file())
    for name in ['Studio.Core.dll', 'HasselbladStartupStudio.dll']:
        data = (root / name).read_bytes()
        for marker in ['/Users/', '/home/', 'github_pat_']:
            assert marker.encode() not in data and marker.encode('utf-16le') not in data, 'Private compiler path / token in ' + name
    adapter = json.loads((root / 'Resources/adapter.json').read_text())
    assert adapter['mode'] == 3 and not adapter['factoryIdentityWrite'] and not adapter['environmentWrite']
    assert len((root / 'Resources/patch.bin').read_bytes()) == sum(p['size'] for p in adapter['patches'])
    if manifest['version'].startswith('0.3.0-rgb'):
        assert (root / 'USB识别诊断.cmd').is_file()
        assert adapter['adapterSHA256'] == '768b6f5d53cccbd6db90b053f1614d7de363462c4ed360ee8ffd10c0611d14ef'
        legacy_hash = 'a2edfe28ad8dbf91ed19fb87526cc6eceb21ceb00a40347ee58e9afaec6929e0'
        legacy_root = root / 'Resources/legacy' / legacy_hash
        legacy = json.loads((legacy_root / 'adapter.json').read_text())
        assert legacy['adapterSHA256'] == legacy_hash and legacy['originalSHA256'] == adapter['originalSHA256']
        assert len((legacy_root / 'patch.bin').read_bytes()) == sum(p['size'] for p in legacy['patches'])
    licenses = root / 'Licenses'
    for name in ['Avalonia-MIT.txt', 'dotnet-MIT.txt', 'dotnet-THIRD-PARTY-NOTICES.txt', 'libusb-LGPL-2.1.txt', 'libusb-1.0.29.tar.bz2', 'SkiaSharp-LICENSE.txt', 'HarfBuzzSharp-LICENSE.txt', 'ANGLE-LICENSE.txt', 'libusb-重建说明.txt']:
        assert (licenses / name).stat().st_size > 100, 'Missing license / source material: ' + name
    archive = Path(str(root) + '.zip')
    with zipfile.ZipFile(archive) as z:
        packaged = {n[len(root.name) + 1:]: hashlib.sha256(z.read(n)).hexdigest() for n in z.namelist() if not n.endswith('/')}
    assert packaged == {**files, 'package-manifest.json': hashlib.sha256((root / 'package-manifest.json').read_bytes()).hexdigest()}
    assert hashlib.sha256(archive.read_bytes()).hexdigest() == Path(str(archive) + '.sha256').read_text().split()[0]
    return dict(passed=True, deviceAccessed=False, actualWindowsExecuted=False, fileCount=len(packaged), adbClientMachine=hex(adb_arch), checks=['manifest', 'PE architecture', 'USB / ADB dependencies', 'runtime', 'adapter', 'private-data boundary', 'licenses', 'ZIP bytes', 'ZIP hash'])


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('package', type=Path)
    p.add_argument('--report', type=Path)
    args = p.parse_args()
    result = audit(args.package)
    if args.report:
        args.report.write_text(json.dumps(result, indent=2) + '\n')
    print('PASS independent Windows package audit:', result['fileCount'], 'files; Windows EXE not executed')


if __name__ == '__main__':
    main()
