"""Stage only Gen2 source and public dependency runtimes, never device backups."""
import hashlib
from pathlib import Path
import shutil
import subprocess
import zipfile

FILES=('g2profile.py','codec.py','transport.py','winusb.py','core.py','scripts.py','adb.py','main.py')


def sources(repo,resources):
    target=resources/'gen2';target.mkdir(parents=True,exist_ok=True)
    for name in FILES:shutil.copy2(repo/'gen2'/name,target/name)
    shutil.copytree(repo/'assets/python-licenses',target/'Licenses')
    (target/'Runtime-changes.txt').write_text('Python runtime packaging changes: relocated macOS executable/dylib paths; omitted installer GUI, tests, bytecode caches and an unused duplicate libusb. Windows: official pinned embedded x64 archive with a local python313._pth. No interpreter source modifications. Original Python and incorporated-software notices are retained in this directory.\n',encoding='utf-8')
    return target


def mac(repo,resources,runtime,python_license,pyusb,pyusb_license):
    target=sources(repo,resources)
    assert (runtime/'bin/python3.13').is_file() and (runtime/'lib/python3.13/encodings').is_dir(),'Relocated Python 3.13 runtime required'
    shutil.copytree(runtime,target/'runtime',ignore=shutil.ignore_patterns('__pycache__','*.pyc'))
    (target/'runtime/lib/libusb-1.0.0.dylib').unlink(missing_ok=True)
    # Relocate the dylib's own install name too, not only its consumers.
    subprocess.run(['install_name_tool','-id','@loader_path/Python',str(target/'runtime/lib/Python')],check=True)
    for library in (target/'runtime').rglob('*.dylib'):
        subprocess.run(['install_name_tool','-id','@loader_path/'+library.name,str(library)],check=True)
    shutil.copytree(pyusb,target/'usb',ignore=shutil.ignore_patterns('__pycache__','*.pyc'))
    shutil.copy2(python_license,target/'Python-license.rtf');shutil.copy2(pyusb_license,target/'PyUSB-license.txt')


def windows(repo,resources,archive):
    assert hashlib.sha256(archive.read_bytes()).hexdigest()=='d1f04d990aee1253d8569e8e5104e30fa9f5fa830899f14843448872d936a2cf','Pinned Python 3.13.15 Windows x64 embed required'
    target=sources(repo,resources);runtime=target/'runtime';runtime.mkdir()
    with zipfile.ZipFile(archive) as package:
        for entry in package.infolist():
            path=Path(entry.filename);assert not path.is_absolute() and '..' not in path.parts
        package.extractall(runtime)
    (runtime/'python313._pth').write_text('python313.zip\n.\n..\n',encoding='ascii')
