"""在本地临时副本验证错误的通用包被拒绝；不访问 USB。"""
import argparse
import json
import plistlib
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'tools'))
from mac_compatibility import audit_app

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('app', type=Path)
    parser.add_argument('--report',type=Path,required=True)
    args=parser.parse_args()
    audit_app(args.app)
    cases=['valid-universal-app']
    with tempfile.TemporaryDirectory(prefix='startup-mac-compat-',dir='/private/tmp') as temporary:
        target=Path(temporary)/args.app.name
        def reset():
            if target.exists(): shutil.rmtree(target)
            shutil.copytree(args.app,target)
        def refused(name):
            try: audit_app(target)
            except RuntimeError: cases.append(name);return
            raise AssertionError(name+' 未拒绝错误的包')
        for name,relative in [('missing-adb','Contents/Resources/bin/adb'),('missing-usb-library','Contents/Frameworks/libusb-1.0.0.dylib')]:
            reset();(target/relative).unlink();refused(name)
        reset();(target/'Contents/Resources/AppIcon.icns').unlink();refused('missing-app-icon')
        reset();info=target/'Contents/Info.plist';data=plistlib.loads(info.read_bytes());data['CFBundleIconFile']='OtherIcon';info.write_bytes(plistlib.dumps(data));refused('unregistered-app-icon')
        for name,relative in [('thin-main-program','Contents/MacOS/StartupStudio'),('thin-adb','Contents/Resources/bin/adb'),('thin-usb-library','Contents/Frameworks/libusb-1.0.0.dylib')]:
            reset();binary=target/relative;thin=Path(temporary)/'thin'
            subprocess.run(['lipo',str(binary),'-thin','arm64','-output',str(thin)],check=True)
            shutil.copy2(thin,binary);refused(name)
        reset();info=target/'Contents/Info.plist';data=plistlib.loads(info.read_bytes());data['LSMinimumSystemVersion']='14.0';info.write_bytes(plistlib.dumps(data));refused('wrong-plist-minimum')
        reset();binary=target/'Contents/Resources/bin/adb';binary.unlink();binary.symlink_to(args.app.resolve()/'Contents/Resources/bin/adb');refused('external-binary-symlink')
    args.report.write_text(json.dumps(dict(passed=True,deviceAccessed=False,cases=cases),indent=2)+'\n')
    print(f'PASS: {len(cases)} macOS 通用包与拒绝条件检查；未访问相机')

if __name__=='__main__':main()
