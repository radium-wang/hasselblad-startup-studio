"""离线组装 Windows x64 测试包；显式输入 SDK、绘图适配层和官方 USB / ADB 依赖。"""
import argparse
import hashlib
import json
import os
import shutil
import struct
import subprocess
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
VERSION = '0.2.0-preview.3'


def pe_machine(path):
    data = path.read_bytes()
    assert data[:2] == b'MZ', f'Not a Windows PE: {path.name}'
    offset = struct.unpack_from('<I', data, 0x3c)[0]
    assert data[offset:offset + 4] == b'PE\0\0', f'Invalid PE header: {path.name}'
    return struct.unpack_from('<H', data, offset + 4)[0]


def package(args):
    output = args.output.resolve()
    version = args.version or VERSION
    assert version == VERSION or version.startswith(VERSION + '-'), 'Local build label must extend the source version'
    assert not output.exists(), 'Choose a new output directory'
    assert f'<InformationalVersion>{VERSION}</InformationalVersion>' in (ROOT / 'windows/Studio.App/Studio.App.csproj').read_text(encoding='utf-8'), 'Package / application version mismatch'
    adapter = json.loads((args.adapter / 'adapter.json').read_text(encoding='utf-8'))
    assert adapter['mode'] == 3 and adapter['originalSHA256'] == 'a0ac02a51d87d08fa61fd0d1e79af15db248af4b5b32603b9483d09f9b7f6217'
    assert adapter['adapterSHA256'] == 'a2edfe28ad8dbf91ed19fb87526cc6eceb21ceb00a40347ee58e9afaec6929e0'
    assert len((args.adapter / 'patch.bin').read_bytes()) == sum(p['size'] for p in adapter['patches'])
    env = os.environ.copy()
    env['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    env['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
    env.setdefault('DOTNET_CLI_HOME', str(ROOT / 'outputs/windows-toolchain/home'))
    env.setdefault('NUGET_PACKAGES', str(ROOT / 'outputs/windows-toolchain/nuget'))
    subprocess.run([str(args.dotnet.resolve()), 'publish', str(ROOT / 'windows/Studio.App/Studio.App.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--disable-build-servers', '-p:UseSharedCompilation=false', '-p:DebugType=None', '-p:InformationalVersion=' + version, '--artifacts-path', str(ROOT / 'outputs/windows-build'), '-o', str(output)], env=env, check=True)
    assert pe_machine(output / 'HasselbladStartupStudio.exe') == 0x8664
    assert pe_machine(args.libusb) == 0x8664
    shutil.copyfile(args.libusb, output / 'libusb-1.0.dll')
    resources = output / 'Resources'
    resources.mkdir()
    for name in ['adapter.json', 'patch.bin']:
        shutil.copyfile(args.adapter / name, resources / name)
    (resources / 'bin').mkdir()
    adb_machine = pe_machine(args.adb / 'adb.exe')
    assert adb_machine in (0x14c, 0x8664), 'Unsupported official ADB architecture'
    for name in ['adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll']:
        path = args.adb / name
        # Google's official Windows platform-tools currently ships a matched x86 client.
        # Windows x64 runs this separate process through WOW64; it is not loaded into our x64 app.
        assert pe_machine(path) == adb_machine, f'Mismatched ADB dependency architecture: {name}'
        shutil.copyfile(path, resources / 'bin' / name)
    shutil.copyfile(args.adb / 'NOTICE.txt', resources / 'bin' / 'NOTICE.txt')
    shutil.copyfile(args.adb / 'source.properties', resources / 'bin' / 'source.properties')
    shutil.copyfile(ROOT / 'LICENSE', output / 'LICENSE.txt')
    shutil.copyfile(ROOT / 'THIRD_PARTY_NOTICES.md', output / 'THIRD_PARTY_NOTICES.md')
    guide = (ROOT / 'research/WINDOWS.md').read_text(encoding='utf-8').replace('(../BUILD.md)', '(https://github.com/radium-wang/hasselblad-startup-studio/blob/main/BUILD.md)')
    (output / 'Windows使用与驱动说明.txt').write_text(guide, encoding='utf-8-sig')
    (output / 'USB识别诊断.cmd').write_bytes(b'@echo off\r\ncd /d "%~dp0"\r\nstart /wait "" "%~dp0HasselbladStartupStudio.exe" --usb-diagnostics\r\nif exist "%~dp0usb-diagnostics\\result.json" (\r\n  start "" notepad.exe "%~dp0usb-diagnostics\\result.json"\r\n) else (\r\n  echo USB diagnostic report could not be created.\r\n  pause\r\n)\r\n')
    if args.licenses:
        shutil.copytree(args.licenses, output / 'Licenses')
    required = ['coreclr.dll', 'hostfxr.dll', 'HasselbladStartupStudio.dll', 'Studio.Core.dll', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll']
    for name in required:
        assert (output / name).is_file(), f'Missing self-contained dependency: {name}'
    for path in output.rglob('*'):
        if path.is_file():
            assert path.name not in ['original-backend.so', 'baseline.json', 'transaction.json'], 'Private backup included'
    files = {p.relative_to(output).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(output.rglob('*')) if p.is_file()}
    (output / 'package-manifest.json').write_text(json.dumps(dict(version=version, target='win-x64', deviceAccessed=False, realWindowsValidated=False, files=files), indent=2) + '\n', encoding='utf-8')
    archive = Path(str(output) + '.zip')
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as z:
        for path in sorted(output.rglob('*')):
            if path.is_file():
                z.write(path, output.name + '/' + path.relative_to(output).as_posix())
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    Path(str(archive) + '.sha256').write_text(digest + '  ' + archive.name + '\n', encoding='utf-8')
    print('PASS Windows x64 self-contained package; real Windows USB test pending:', archive)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--dotnet', type=Path, required=True)
    p.add_argument('--adapter', type=Path, required=True)
    p.add_argument('--libusb', type=Path, required=True)
    p.add_argument('--adb', type=Path, required=True)
    p.add_argument('--licenses', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--version', help='Optional local test label extending the source version')
    package(p.parse_args())


if __name__ == '__main__':
    main()
