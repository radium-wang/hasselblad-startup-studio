"""Exercise packaged Mac Python on both architectures, without --device."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from gen2_package import FILES

def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('app',type=Path);parser.add_argument('--report',type=Path,required=True);args=parser.parse_args()
    root=args.app.resolve()/'Contents/Resources/gen2';repo=Path(__file__).resolve().parents[1]
    hashes={}
    for name in FILES:
        data=(root/name).read_bytes();assert data==(repo/'gen2'/name).read_bytes(),'Stale helper source: '+name
        hashes[name]=hashlib.sha256(data).hexdigest()
    env={k:v for k,v in os.environ.items() if not k.upper().startswith('PYTHON')};env['PYTHONHOME']=str(root/'runtime')
    python=root/'runtime/bin/python3.13'
    code="""import sys,json,struct,zlib,ctypes,socket
sys.path.insert(0,sys.argv[1])
import codec,core,adb,winusb,transport
from g2profile import SIZE,HEADER
p=b'\\xff';fnv=((2166136261^255)*16777619)&0xffffffff
image=codec.encode(b'X2DLOGO3'+struct.pack('<6I',1,1,1,1,fnv,0)+p)
assert len(image)==SIZE and image[:53]==HEADER and image[53+383*1024+511]==235
assert zlib.decompress(zlib.compress(image))==image
assert 'usb.core' not in sys.modules
print(json.dumps(dict(version=list(sys.version_info[:3]),size=len(image),deviceAccessed=False)))
"""
    cases=[]
    with tempfile.TemporaryDirectory(prefix='gen2-runtime-') as temporary:
        for arch in ('arm64','x86_64'):
            command=['arch','-'+arch,str(python),'-X','utf8','-P','-s','-B']
            result=subprocess.run(command+['-c',code,str(root)],env=env,capture_output=True,timeout=45,check=True)
            value=json.loads(result.stdout);assert value['version']==[3,13,15] and not value['deviceAccessed'];cases.append(dict(architecture=arch,cpuEngine=value))
            result=subprocess.run(command+[str(root/'main.py'),'--operation','probe','--backups',temporary,'--libusb','unused','--adb','unused'],env=env,capture_output=True,timeout=15)
            assert result.returncode==2 and '缺少显式设备入口' in json.loads(result.stdout)['message']
            assert not list(Path(temporary).iterdir())
    args.report.write_text(json.dumps(dict(passed=True,deviceAccessed=False,cases=cases,helperSHA256=hashes),indent=2)+'\n')
    print('PASS: packaged Gen2 engine/import/UTF8/no-device gate, arm64 + x86_64 (Rosetta where applicable)')

if __name__=='__main__':main()
