"""离线组装 Windows x64 测试包；显式输入 SDK、绘图适配层和官方 USB / ADB 依赖。"""
import argparse
import gen2_package
import hashlib
import json
import os
import shutil
import struct
import subprocess
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
VERSION = '0.4.0-gen2-preview.1'


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
    assert adapter['adapterSHA256'] == '768b6f5d53cccbd6db90b053f1614d7de363462c4ed360ee8ffd10c0611d14ef'
    assert len((args.adapter / 'patch.bin').read_bytes()) == sum(p['size'] for p in adapter['patches'])
    env = os.environ.copy()
    env['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    env['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
    env.setdefault('DOTNET_CLI_HOME', str(ROOT / 'outputs/windows-toolchain/home'))
    env.setdefault('NUGET_PACKAGES', str(ROOT / 'outputs/windows-toolchain/nuget'))
    command=[str(args.dotnet.resolve()), 'publish', str(ROOT / 'windows/Studio.App/Studio.App.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--disable-build-servers', '-p:UseSharedCompilation=false', '-p:DebugType=None', '-p:InformationalVersion=' + version, '--artifacts-path', str(ROOT / 'outputs/windows-build'), '-o', str(output)]
    if args.nuget_source:command+=['-p:RestoreSources='+str(args.nuget_source.resolve()),'-p:NuGetAudit=false']
    subprocess.run(command, env=env, check=True)
    assert pe_machine(output / 'HasselbladStartupStudio.exe') == 0x8664
    assert pe_machine(args.libusb) == 0x8664
    shutil.copyfile(args.libusb, output / 'libusb-1.0.dll')
    resources = output / 'Resources'
    resources.mkdir(exist_ok=True)
    gen2_package.windows(ROOT,resources,args.gen2_python_zip)
    shutil.copyfile(ROOT / 'assets/app-icon/AppIcon.png', resources / 'AppIcon.png')
    for name in ['adapter.json', 'patch.bin']:
        shutil.copyfile(args.adapter / name, resources / name)
    legacy_hash = 'a2edfe28ad8dbf91ed19fb87526cc6eceb21ceb00a40347ee58e9afaec6929e0'
    legacy = json.loads((args.legacy_adapter / 'adapter.json').read_text(encoding='utf-8'))
    assert legacy['adapterSHA256'] == legacy_hash and legacy['originalSHA256'] == adapter['originalSHA256']
    original_patch = (args.legacy_adapter / 'patch.bin').read_bytes()
    assert len(original_patch) == sum(p['size'] for p in legacy['patches'])
    historical = resources / 'legacy' / legacy_hash
    historical.mkdir(parents=True)
    for name in ['adapter.json', 'patch.bin']:
        shutil.copyfile(args.legacy_adapter / name, historical / name)
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
    (output / 'USB识别诊断.cmd').write_bytes(b'@echo off\r\ncd /d "%~dp0"\r\nset "REPORT=%LOCALAPPDATA%\\X2DStartupStudio\\Logs\\usb-diagnostics\\result.json"\r\nif exist "%REPORT%" del /q "%REPORT%"\r\nstart /wait "" "%~dp0HasselbladStartupStudio.exe" --usb-diagnostics\r\nif errorlevel 1 (\r\n  echo USB diagnostic run failed; no current report is available.\r\n  pause\r\n  exit /b 1\r\n)\r\nif exist "%REPORT%" (\r\n  start "" notepad.exe "%REPORT%"\r\n) else (\r\n  echo USB diagnostic report could not be created.\r\n  pause\r\n)\r\n')
    (output / '只读传输检测.cmd').write_bytes(b'@echo off\r\ncd /d "%~dp0"\r\necho Close Startup Studio first. Keep one camera connected and awake.\r\necho This test downloads the stock library and temporarily switches USB mode.\r\necho No image will be installed. Press Ctrl+C to cancel, or any key to begin.\r\npause >nul\r\nset "REPORTDIR=%LOCALAPPDATA%\\X2DStartupStudio\\Logs\\transfer-diagnostics"\r\nif exist "%REPORTDIR%\\result.txt" del /q "%REPORTDIR%\\result.txt"\r\nstart /wait "" "%~dp0HasselbladStartupStudio.exe" --transfer-diagnostics\r\nif errorlevel 2 (\r\n echo Close the running app and try again.\r\n pause\r\n exit /b 2\r\n)\r\nif exist "%REPORTDIR%\\result.txt" (\r\n start "" notepad.exe "%REPORTDIR%\\result.txt"\r\n start "" "%REPORTDIR%"\r\n) else (\r\n echo Report could not be created. Check folder permissions.\r\n pause\r\n)\r\n')
    (output / '检测说明.txt').write_text('第一代 X2D / CFV 100C：\n1. 完整解压，关闭之前打开的换图 App。\n2. 相机开机并连接电脑，双击“只读传输检测.cmd”，按任意键开始。\n3. 等待结果自动弹出，把 result.txt 发回即可。\n\n检测只下载校验原厂文件，不安装图片。检测期间保持连接。\n\nX2D II：请使用 App 的“连接相机”或“USB识别诊断.cmd”；后者只检查驱动登记，包含 MI_04 / MI_06 信息，不发送相机命令。“只读传输检测.cmd”仍是一代入口。\n', encoding='utf-8-sig')
    if args.licenses:
        shutil.copytree(args.licenses, output / 'Licenses')
    required = ['coreclr.dll', 'hostfxr.dll', 'HasselbladStartupStudio.dll', 'Studio.Core.dll', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll']
    for name in required:
        assert (output / name).is_file(), f'Missing self-contained dependency: {name}'
    for path in output.rglob('*'):
        if path.is_file():
            assert path.name not in ['original-backend.so','original-logo.bin', 'baseline.json', 'transaction.json'], 'Private backup included'
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
    p.add_argument('--nuget-source',type=Path,help='Optional local NuGet feed for offline builds')
    p.add_argument('--gen2-python-zip',type=Path,required=True)
    p.add_argument('--adapter', type=Path, required=True)
    p.add_argument('--legacy-adapter', type=Path, required=True)
    p.add_argument('--libusb', type=Path, required=True)
    p.add_argument('--adb', type=Path, required=True)
    p.add_argument('--licenses', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--version', help='Optional local test label extending the source version')
    package(p.parse_args())


if __name__ == '__main__':
    main()
