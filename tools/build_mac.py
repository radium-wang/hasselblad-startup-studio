"""生成原生 macOS 本地测试 .app，构建不访问 USB。"""
import argparse,json,plistlib,shutil,subprocess
from pathlib import Path
from mac_compatibility import ARCHITECTURES,MINIMUM_MACOS,require_architectures,audit_app
ROOT=Path(__file__).resolve().parents[1]
VERSION='0.3.0-rgb-preview.5'
def main():
 p=argparse.ArgumentParser(description=__doc__)
 p.add_argument('--debug',action='store_true');p.add_argument('--adapter',type=Path,required=True);p.add_argument('--legacy-adapter',type=Path,required=True);p.add_argument('--libusb',type=Path,required=True);p.add_argument('--libusb-license',type=Path);p.add_argument('--adb',type=Path,required=True);p.add_argument('--adb-license',type=Path,required=True);p.add_argument('--usb-include',type=Path,required=True);p.add_argument('--output',type=Path,default=ROOT/'outputs/macOS-Universal')
 a=p.parse_args();a.output.mkdir(parents=True,exist_ok=True)
 for dependency in (a.libusb,a.adb):require_architectures(dependency,ARCHITECTURES)
 manifest=json.loads((a.adapter/'adapter.json').read_text());verification=json.loads((a.adapter/'loader-verification.json').read_text())
 assert verification['adapterSHA256']==manifest['adapterSHA256'] and len(verification['cases'])>=29 and all(x['passed'] for x in verification['cases'])
 assert manifest['mode']==3 and manifest['originalSHA256']=='a0ac02a51d87d08fa61fd0d1e79af15db248af4b5b32603b9483d09f9b7f6217' and manifest['adapterSHA256']=='768b6f5d53cccbd6db90b053f1614d7de363462c4ed360ee8ffd10c0611d14ef'
 legacy=json.loads((a.legacy_adapter/'adapter.json').read_text())
 assert legacy['adapterSHA256']=='a2edfe28ad8dbf91ed19fb87526cc6eceb21ceb00a40347ee58e9afaec6929e0' and legacy['originalSHA256']==manifest['originalSHA256']
 assert len((a.legacy_adapter/'patch.bin').read_bytes())==sum(part['size'] for part in legacy['patches'])
 app=a.output/'X2D Startup Studio.app';contents=app/'Contents';binpath=contents/'MacOS';resources=contents/'Resources';frameworks=contents/'Frameworks'
 for folder in (binpath,resources,frameworks):folder.mkdir(parents=True,exist_ok=True)
 for name in ('patch.bin','adapter.json'):shutil.copy2(a.adapter/name,resources/name)
 shutil.copy2(ROOT/'assets/app-icon/AppIcon.icns',resources/'AppIcon.icns')
 shutil.copy2(ROOT/'assets/app-icon/AppIcon.png',resources/'AppIcon.png')
 historical=resources/'legacy'/legacy['adapterSHA256'];historical.mkdir(parents=True,exist_ok=True)
 for name in ('patch.bin','adapter.json'):shutil.copy2(a.legacy_adapter/name,historical/name)
 for name in ('LICENSE','THIRD_PARTY_NOTICES.md'):shutil.copy2(ROOT/name,resources/name)
 if (frameworks/'libusb-1.0.0.dylib').exists():(frameworks/'libusb-1.0.0.dylib').chmod(0o644)
 shutil.copy2(a.libusb.resolve(),frameworks/'libusb-1.0.0.dylib')
 (frameworks/'libusb-1.0.0.dylib').chmod(0o644)
 license_file=a.libusb_license or a.libusb.resolve().parents[1]/'COPYING'
 if not license_file.is_file():raise FileNotFoundError('请通过 --libusb-license 指定 libusb 的 COPYING 许可证文件')
 shutil.copy2(license_file,resources/'libusb-COPYING.txt')
 (resources/'bin').mkdir(exist_ok=True)
 shutil.copy2(a.adb,resources/'bin/adb');(resources/'bin/adb').chmod(0o755)
 shutil.copy2(a.adb_license,resources/'Android-platform-tools.txt')
 subprocess.run(['install_name_tool','-id','@rpath/libusb-1.0.0.dylib',str(frameworks/'libusb-1.0.0.dylib')],check=True)
 sources=[ROOT/'native/Core.swift',ROOT/'native/Transport.swift',ROOT/'native/App.swift',ROOT/'CodeTests/SelfTests.swift']
 slices=[]
 for arch in ARCHITECTURES:
  target=arch+'-apple-macosx'+MINIMUM_MACOS;obj=a.output/('USBBridge-'+arch+'.o');cache=a.output/('module-cache-'+arch);binary=a.output/('StartupStudio-'+arch)
  subprocess.run(['clang','-target',target,'-c',str(ROOT/'native/USBBridge.c'),'-I'+str(a.usb_include),'-o',str(obj)],check=True)
  subprocess.run(['swiftc','-swift-version','5','-target',target,'-Onone' if a.debug else '-O','-module-cache-path',str(cache),'-Xcc','-fmodules-cache-path='+str(cache/'clang'),'-import-objc-header',str(ROOT/'native/USBBridge.h'),*map(str,sources),str(obj),str(frameworks/'libusb-1.0.0.dylib'),'-lz','-Xlinker','-rpath','-Xlinker','@executable_path/../Frameworks','-o',str(binary)],check=True)
  slices.append(binary)
 subprocess.run(['lipo','-create',*map(str,slices),'-output',str(binpath/'StartupStudio')],check=True)
 # Some Homebrew versions use an absolute dylib ID; rewrite only this dependency.
 deps=subprocess.check_output(['otool','-L',str(binpath/'StartupStudio')],text=True)
 for line in deps.splitlines()[1:]:
  dep=line.strip().split(' (')[0]
  if 'libusb' in dep and not dep.startswith('@rpath'):
   subprocess.run(['install_name_tool','-change',dep,'@rpath/libusb-1.0.0.dylib',str(binpath/'StartupStudio')],check=True)
 info=dict(CFBundleName='X2D Startup Studio',CFBundleDisplayName='X2D / 907X 开机画面',CFBundleIdentifier='local.x2d.startupstudio',CFBundleVersion='6',CFBundleShortVersionString='0.3.0',CFBundleGetInfoString=VERSION,CFBundleExecutable='StartupStudio',CFBundleIconFile='AppIcon',CFBundlePackageType='APPL',LSMinimumSystemVersion=MINIMUM_MACOS,NSHighResolutionCapable=True,NSUSBUsageDescription='通过 USB 检查相机、安装自定义开机画面及恢复首次原厂备份。')
 (contents/'Info.plist').write_bytes(plistlib.dumps(info))
 subprocess.run(['codesign','--force','--sign','-',str(frameworks/'libusb-1.0.0.dylib')],check=True)
 subprocess.run(['codesign','--force','--sign','-',str(resources/'bin/adb')],check=True)
 subprocess.run(['codesign','--force','--sign','-',str(app)],check=True)
 subprocess.run(['codesign','--verify','--deep','--strict',str(app)],check=True)
 (a.output/'macOS-compatibility-audit.json').write_text(json.dumps(audit_app(app),indent=2)+'\n')
 (a.output/'build.json').write_text(json.dumps(dict(app=app.name,arch='Universal',architectures=list(ARCHITECTURES),minimumMacOS=MINIMUM_MACOS,version=VERSION,mode=3,factoryWrites=False,deviceAccessed=False,adapterSHA256=manifest['adapterSHA256'],signature='local ad-hoc',notarized=False),indent=2)+'\n')
 print(app)
if __name__=='__main__':main()
