"""Offline Gen2 contracts and real generated shell on private local files.

No USB objects are created. UID and SELinux are explicit stand-ins; this does
not simulate power-loss durability, Windows drivers, or a camera cold boot.
"""
import argparse
import base64
import ctypes
import gzip
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'gen2'))
import adb,codec,core,scripts,transport,winusb
from g2profile import HEADER,SIZE,STOCK,GUI,ORIGINAL_USB,StudioError

original=HEADER+bytes(SIZE-len(HEADER))
checks=[]
interruptions=[]

def sha(data):return hashlib.sha256(data).hexdigest()
def envelope(w,h,pixels,version=2):
    checksum=2166136261
    for value in pixels:checksum=((checksum^value)*16777619)&0xffffffff
    return b'X2DLOGO3'+struct.pack('<6I',version,w,h,len(pixels),checksum,0)+pixels

WRAPPER=r'''
import sys,os,base64,gzip,hashlib,subprocess
from pathlib import Path
name=Path(sys.argv[0]).name;args=sys.argv[1:]
if name=='getprop':
    values={'ro.product.device':'eagle2_hb722','sys.usb.config':'rndis,mass_storage,bulk,acm','sys.usb.state':'rndis,mass_storage,bulk,acm','init.svc.adbd':'stopped'}
    print(values.get(args[0],''))
elif name=='sha256sum':
    p=args[0];print(hashlib.sha256(Path(p).read_bytes()).hexdigest()+'  '+p)
elif name=='stat':
    s=os.stat(args[-1]);print(s.st_size if args[1]=='%s' else f'{s.st_mode & 0o777:o} 0 0 {s.st_size} {int(s.st_mtime)}')
elif name=='ls':print('u:object_r:system_data_file:s0 '+args[-1])
elif name in ('sync','chown'):pass
elif name=='toybox':
    name,*args=args
    if name=='gzip':
        if args[0]=='-c':sys.stdout.buffer.write(gzip.compress(Path(args[1]).read_bytes(),mtime=0))
        else:sys.stdout.buffer.write(gzip.decompress(sys.stdin.buffer.read()))
    elif name=='base64':
        if args and args[0]=='-d':sys.stdout.buffer.write(base64.b64decode(Path(args[-1]).read_bytes(),validate=False))
        else:sys.stdout.buffer.write(base64.encodebytes(sys.stdin.buffer.read()))
    elif name=='dd':
        options=dict(a.split('=',1) for a in args);size=int(options['bs']);start=int(options.get('skip','0'))*size
        sys.stdout.buffer.write(sys.stdin.buffer.read()[start:start+int(options['count'])*size])
    elif name=='wc':print(len(sys.stdin.buffer.read()))
    else:raise SystemExit(subprocess.call(['/usr/bin/'+name]+args))
else:raise SystemExit(90)
'''

class Fixture:
    healthy=True
    def __init__(self):
        self.temp=tempfile.TemporaryDirectory(prefix='hss2-test-')
        self.home=Path(self.temp.name);self.factory=self.home/'factory_data';self.factory.mkdir()
        self.target=self.factory/'logo.bin';self.target.write_bytes(original);self.target.chmod(0o600)
        os.utime(self.target,(1783935561,1783935561))
        self.settings=self.factory/'settings.ini';self.settings.write_bytes(b'local identity fixture')
        self.gui=self.home/'camera-gui';self.gui.write_bytes(b'local GUI fixture')
        self.root=self.factory/'.hss-startup2';self.backups=self.home/'Backups'
        self.device=sha(self.settings.read_bytes());self.metadata='600 0 0 1179701 1783935561'
        self.baseline=scripts.Baseline(self.device,self.metadata)
        self.tools=self.home/'tools';self.tools.mkdir();self.commands=[];self.large=0;self.fail=None
        (self.tools/'python').symlink_to(sys.executable)
        for name in ('getprop','sha256sum','stat','ls','toybox','chown','sync'):
            path=self.tools/name;path.write_text('#!'+str(self.tools/'python')+'\n'+WRAPPER);path.chmod(0o700)
        self.patches=[patch.object(core,'STOCK',sha(original)),patch.object(scripts,'STOCK',sha(original)),patch.object(core,'GUI',sha(self.gui.read_bytes())),patch.object(scripts,'GUI',sha(self.gui.read_bytes()))]
        for p in self.patches:p.start()
        self.controller=core.Controller(self,self.backups)
    def translate(self,source):
        return source.replace('/factory_data',str(self.factory)).replace('/system/bin/camera-gui',str(self.gui)).replace('export PATH=/system/bin:/system/xbin:/sbin','export PATH='+str(self.tools)+':/usr/bin:/bin')
    def run(self,source):
        return subprocess.run(['/bin/sh','-c','export PATH='+str(self.tools)+':/usr/bin:/bin\n'+self.translate(source)],capture_output=True,env=dict(os.environ,PYTHONDONTWRITEBYTECODE='1'))
    def call(self,command,timeout=15):
        transport.request(command+'; r=$?; printf "\\nHSS_STATUS=%s\\n" "$r"',1)
        self.commands.append(command)
        if command.startswith('sh '+scripts.ROOT+'/'):source=(self.root/command.split('/')[-1]).read_text()
        else:source=command
        result=self.run(source)
        if self.fail and self.fail(command):self.fail=None;self.healthy=False;raise StudioError('injected lost response')
        if result.returncode:raise StudioError('fixture command failed: '+command+'\n'+result.stderr.decode())
        return result.stdout.decode().strip().replace(str(self.factory),'/factory_data').replace(str(self.gui),'/system/bin/camera-gui')
    def write_large(self,data,path):
        self.large+=1;Path(self.translate(path)).write_bytes(data)
    def verify(self):
        assert self.target.read_bytes()==original and self.target.stat().st_mode&0o777==0o600
        assert int(self.target.stat().st_mtime)==1783935561 and not self.root.exists()
    def stage(self,image,record):
        self.root.mkdir(exist_ok=True);(self.root/'owner-id').write_text(self.device)
        (self.root/'journal.json').write_bytes(record);(self.root/'image.bin').write_bytes(image)
    def recover(self):
        self.healthy=True;self.controller=core.Controller(self,self.backups);self.controller.restore();self.verify()
    def close(self):
        for p in reversed(self.patches):p.stop()
        self.temp.cleanup()

class Checks(unittest.TestCase):
    def tearDown(self):checks.append(self.id().split('.')[-1])
    def reject(self,body):
        with self.assertRaises((StudioError,ValueError,FileNotFoundError)):body()
    def test_codec_vectors_and_center(self):
        colors=[(255,255,255),(255,255,0),(0,255,255),(0,255,0),(255,0,255),(255,0,0),(0,0,255),(0,0,0)]
        # Primary-vector values independently captured from the vImage trial.
        ys=[235,210,170,145,106,81,41,16];uvs=[(128,128),(16,146),(166,16),(54,34),(202,222),(90,240),(240,110),(128,128)]
        pixels=bytes(v for y in range(2) for r,g,b in colors for x in range(2) for v in (b,g,r,255))
        image=codec.encode(envelope(16,2,pixels));self.assertEqual(len(image),SIZE);self.assertEqual(image[:53],HEADER)
        ybase=53+383*1024+504;uvbase=53+1024*768+191*1024+504
        for i in range(8):
            self.assertLessEqual(abs(image[ybase+2*i]-ys[i]),1)
            # Top=383 is odd, so each chroma block blends with black. Check an even aligned image below.
        pixels=bytes(v for y in range(4) for r,g,b in colors for x in range(2) for v in (b,g,r,255))
        image=codec.encode(envelope(16,4,pixels));uvbase=53+1024*768+191*1024+504
        for i,(u,v) in enumerate(uvs):
            self.assertLessEqual(abs(image[uvbase+2*i]-u),1);self.assertLessEqual(abs(image[uvbase+2*i+1]-v),1)
        self.assertEqual(image[53],16);self.assertEqual(image[53+1024*768],128)
    def test_codec_odd_gray_and_invalid(self):
        image=codec.encode(envelope(1,1,b'\xff',1));self.assertEqual(image[53+383*1024+511],235)
        self.assertEqual(set(image[53+1024*768:]),{128})
        for raw in (b'',envelope(1,1,b'\x01',1)[:-1],envelope(1025,1,bytes(1025),1),envelope(1,1,b'\x01',1)[:-1]+b'\x02'):
            self.reject(lambda:codec.encode(raw))
    def test_protocol_frames(self):
        packet=transport.request('printf x',123);self.assertEqual(len(packet),257);self.assertEqual(packet[:5],bytes((10,0,8,5,252)))
        self.assertEqual(struct.unpack_from('<III',packet,5),(65,0,123))
        body=bytearray(252);struct.pack_into('<IIII',body,0,65,4,123,0);body[20:23]=b'ok\0';struct.pack_into('<I',body,12,transport.crc(body[16:]))
        reply=bytes((9,0,5,8,252))+body;self.assertEqual(transport.parse_record(reply,123),(4,b'ok'))
        for raw,cookie in ((reply[:-1],123),(reply,124),(reply[:40]+bytes((reply[40]^1,))+reply[41:],123)):
            self.reject(lambda:transport.parse_record(raw,cookie))
        self.reject(lambda:transport.request('x'*232,1))
    def test_native_firmware_gate(self):
        raw=bytearray(1024);payload=b'\x09\x00\x82S9,v1.3.18.1';raw[:5]=bytes((3,0,5,8,len(payload)));raw[5:5+len(payload)]=payload
        self.assertEqual(transport.parse_version(raw,9),'1.3.18.1')
        self.reject(lambda:transport.parse_version(raw,8));raw[18]^=1;self.reject(lambda:transport.parse_version(raw,9))
    def test_missing_status_poison(self):
        link=object.__new__(transport.Link);link.healthy=True;link.raw=lambda *_:'not a shell completion'
        self.reject(lambda:link.call('true'));self.assertFalse(link.healthy)
    def test_complete_padded_blocks_with_fragmented_reads(self):
        class Usb:
            def write(self,request):
                cookie=struct.unpack_from('<I',request,13)[0];self.parts=[]
                for function,text in ((4,b'hello\nHSS_STATUS=0\n'),(0,b'')):
                    body=bytearray(252);struct.pack_into('<IIII',body,0,65,function,cookie,0);body[20:20+len(text)]=text
                    struct.pack_into('<I',body,12,transport.crc(body[16:]));record=bytes((9,0,5,8,252))+body+bytes([165])*767
                    self.parts.extend((record[:257],record[257:]))
            def read(self,timeout,size):
                part=self.parts.pop(0);assert len(part)<=size;return part
        link=object.__new__(transport.Link);link.healthy=True;link.usb=Usb()
        self.assertEqual(link.call('printf hello'), 'hello');self.assertFalse(link.usb.parts);self.assertTrue(link.healthy)
    def test_fragmented_native_version(self):
        class Usb:
            def __init__(self,_):pass
            def write(self,request):
                sequence=request[5];raw=bytearray(1024);payload=bytes((sequence,0,130))+b'S9,v1.3.18.1';raw[:5]=bytes((3,0,5,8,len(payload)));raw[5:5+len(payload)]=payload;self.parts=[bytes(raw[:17]),bytes(raw[17:])]
            def read(self,timeout,size):return self.parts.pop(0)
            def close(self):pass
        with patch.object(transport,'MacUsb',Usb),patch.object(transport.sys,'platform','darwin'):
            link=transport.Link(None);self.assertEqual(link.version,'1.3.18.1');link.close()
    def test_block_timeout_keeps_partial_input(self):
        parts=[bytes(17),TimeoutError(110,'fixture timeout'),bytes(1007)]
        class Usb:
            def read(self,timeout,size):
                part=parts.pop(0)
                if isinstance(part,Exception):raise part
                return part
        link=object.__new__(transport.Link);link.usb=Usb();self.assertEqual(link.block(transport.time.monotonic()+2),bytes(1024))
    def test_disconnect_after_fragment_stops_session(self):
        class Usb:
            n=0
            def write(self,_):pass
            def read(self,timeout,size):
                self.n+=1
                if self.n==1:return bytes(257)
                raise StudioError('fixture disconnected')
        link=object.__new__(transport.Link);link.healthy=True;link.usb=Usb();self.reject(lambda:link.raw('true'));self.assertFalse(link.healthy)
    def test_winusb_timeout_with_bytes_is_not_discarded(self):
        def read(handle,endpoint,data,size,count,overlapped):
            ctypes.cast(count,ctypes.POINTER(ctypes.c_uint32)).contents.value=3;return 0
        usb=object.__new__(winusb.WinUsb);usb.handle=ctypes.c_void_p(1)
        usb.api=SimpleNamespace(win=SimpleNamespace(WinUsb_SetPipePolicy=lambda *_:1,WinUsb_ReadPipe=read))
        with patch.object(winusb.C,'get_last_error',lambda:121,create=True):self.assertEqual(usb.read(size=8),bytes(3))
    def test_windows_interface_selection(self):
        physical=r'USB\VID_2756&PID_000B\fixture';row=dict(physical=physical,id=r'USB\VID_2756&PID_000B&MI_04\fixture',path=r'\\?\usb#vid_2756&pid_000b&mi_04#fixture')
        self.assertEqual(winusb.select([row,dict(row)],{physical}),row['path'])
        for rows,devices in (([dict(row,id=row['id'].replace('04','03'))],{physical}),([row],{physical,physical+'2'}),([row],set())):
            self.reject(lambda:winusb.select(rows,devices))
        self.assertEqual(ctypes.sizeof(winusb.Interface),32);self.assertEqual(ctypes.sizeof(winusb.Device),32);self.assertEqual(ctypes.sizeof(winusb.Descriptor),9);self.assertEqual(ctypes.sizeof(winusb.Pipe),12)
    def test_record_strict_and_first_copy(self):
        f=Fixture()
        try:
            raw=scripts.journal(f.baseline,[sha(original)]);store=core.Store(f.backups,f.device);store.save(raw,original)
            first=(store.path/'baseline.json').read_bytes();store.save(scripts.journal(f.baseline,[sha(original),'a'*64]))
            self.assertEqual((store.path/'baseline.json').read_bytes(),first)
            for data,device in ((raw,'f'*64),(raw+b' ',f.device),(raw.replace(b'1.3.18.1',b'1.3.18.2'),f.device)):
                self.reject(lambda:core.decode_record(data,device))
            changed=scripts.Baseline(f.device,'600 0 0 1179701 1783935562')
            self.reject(lambda:store.save(scripts.journal(changed,[sha(original)])))
        finally:f.close()
    def test_controller_install_replace_restore_cross_host(self):
        f=Fixture()
        try:
            first=HEADER+bytes([127])*(SIZE-53);second=bytearray(first);second[1000]=1;second=bytes(second)
            self.assertTrue(f.controller.inspect()['result']['canInstall'])
            f.controller.install(first);baseline=(f.backups/'gen2'/f.device/'baseline.json').read_bytes()
            f.controller.install(second);self.assertEqual(f.target.read_bytes(),second)
            self.assertEqual((f.backups/'gen2'/f.device/'baseline.json').read_bytes(),baseline)
            self.assertEqual((f.root/'original-logo.bin').read_bytes(),original)
            # A different computer recovers baseline and original from the machine.
            other=core.Controller(f,f.home/'OtherComputer');self.assertTrue(other.inspect()['result']['canRestore'])
            other.restore();f.verify()
        finally:f.close()
    def test_controller_host_fallback(self):
        f=Fixture()
        try:
            f.controller.install(HEADER+bytes([128])*(SIZE-53));(f.root/'original-logo.bin').write_bytes(b'broken')
            f.controller.restore();f.verify()
        finally:f.close()
    def test_controller_unknown_image_preserved(self):
        f=Fixture()
        try:
            f.target.write_bytes(b'other tool image');before=f.target.read_bytes()
            self.reject(f.controller.inspect);self.assertEqual(f.target.read_bytes(),before);self.assertFalse(f.root.exists())
        finally:f.close()
    def test_controller_large_file_route(self):
        f=Fixture()
        try:
            payload=hashlib.shake_256(b'deterministic synthetic image').digest(SIZE-53)
            f.controller.install(HEADER+payload);self.assertEqual(f.large,1);f.controller.restore();f.verify()
        finally:f.close()
    def test_controller_interrupted_transfers(self):
        points=[lambda c:c.startswith('umask '),lambda c:c.startswith('printf ') and 'journal.json.next.b64' in c,
                lambda c:c.startswith('mv ') and 'journal.json.next' in c,lambda c:c.startswith('printf ') and 'image.bin.b64' in c,
                lambda c:c=='sh '+scripts.ROOT+'/install.sh']
        for point in points:
            f=Fixture()
            try:
                f.fail=point;self.reject(lambda:f.controller.install(HEADER+bytes([128])*(SIZE-53)))
                self.assertFalse(f.healthy);f.recover()
            finally:f.close()
    def test_generated_persistent_boundaries(self):
        # Same installed scripts as the App, actual cp/mv/chmod/touch/rm, local UID/label stand-ins.
        f=Fixture();image=HEADER+bytes([127])*(SIZE-53);record=scripts.journal(f.baseline,[sha(image)])
        sources=[('install',scripts.install(f.baseline,sha(image),sha(original),record)),('restore',scripts.restore(f.baseline,record))];f.close()
        for operation,source in sources:
            lines=source.splitlines();points=[i for i,line in enumerate(lines) if line.strip().startswith(('cp -p ','cat ','mv -f ','chmod ','chown ','TZ=UTC ','rm -f ','rmdir ')) or line.strip()=='sync']
            for number,at in enumerate(points):
                f=Fixture()
                try:
                    f.stage(image,record)
                    if operation=='restore':self.assertEqual(f.run(scripts.install(f.baseline,sha(image),sha(original),record)).returncode,0)
                    interrupted='\n'.join(lines[:at+1]+['exit 97']+lines[at+1:])+'\n';f.run(interrupted)
                    # Cold new host session is not available once final journal deletion has begun; host pending record is retained.
                    store=core.Store(f.backups,f.device);store.save(record,original);store.activate();f.recover()
                    interruptions.append(operation+':'+str(number+1))
                finally:f.close()
    def test_unknown_cleanup_entry_preserves_journal(self):
        f=Fixture()
        try:
            f.controller.install(HEADER+bytes([128])*(SIZE-53));(f.root/'foreign').write_bytes(b'preserve')
            record=(f.root/'journal.json').read_bytes();self.reject(f.controller.restore)
            self.assertEqual((f.root/'foreign').read_bytes(),b'preserve');self.assertEqual((f.root/'journal.json').read_bytes(),record)
        finally:f.close()
    def test_other_tool_usb_state_refused(self):
        f=Fixture();call=f.call
        try:
            f.call=lambda command,timeout=15:adb.TARGET if command=='getprop sys.usb.state' else call(command,timeout)
            self.reject(f.controller.inspect);self.assertFalse(f.root.exists())
        finally:f.close()
    def test_watchdog_is_armed_before_switch(self):
        source=adb.switch_script('/tmp/hss2-1234abcd').decode()
        self.assertLess(source.index('trap restore EXIT HUP INT TERM'),source.index('sleep 3'))
        self.assertIn('"$n" -lt 180',source);self.assertNotIn('setprop persist.',source);self.assertNotIn('setenforce',source)
        self.assertEqual(subprocess.run(['/bin/sh','-n'],input=source.encode()).returncode,0)
        self.reject(lambda:adb.switch_script('/tmp/foreign'))
    def test_default_cli_no_hardware(self):
        with tempfile.TemporaryDirectory() as temporary:
            args=[sys.executable,'-B',str(ROOT/'gen2/main.py'),'--operation','probe','--backups',temporary,'--libusb','missing','--adb','missing']
            result=subprocess.run(args,capture_output=True);self.assertEqual(result.returncode,2)
            self.assertIn('缺少显式设备入口',result.stdout.decode());self.assertEqual(list(Path(temporary).iterdir()),[])
    def test_cli_forces_utf8_for_windows_pipe(self):
        with tempfile.TemporaryDirectory() as temporary:
            args=[sys.executable,'-B',str(ROOT/'gen2/main.py'),'--operation','probe','--backups',temporary,'--libusb','missing','--adb','missing']
            result=subprocess.run(args,capture_output=True,env=dict(os.environ,PYTHONIOENCODING='ascii'))
            self.assertEqual(result.returncode,2);self.assertIn('缺少显式设备入口',json.loads(result.stdout.decode('utf-8'))['message'])
    def test_windows_parent_case_aliases(self):
        api=object.__new__(winusb.Api);physical=r'USB\VID_2756&PID_000B\fixture'
        api.rows=lambda _: [dict(physical=physical),dict(physical=physical.lower())]
        self.assertEqual(api.physical(),{physical.upper()})
    def test_managed_finish_waits_for_files_and_preserves_properties(self):
        calls=[];state={'usb':adb.TARGET,'checks':0};lose_stop=getattr(self,'lose_stop',False)
        class Native:
            healthy=True
            def close(self):calls.append('close')
            def call(self,command,timeout=15):
                calls.append(command)
                if command.startswith('sha256sum '):return 'a'*64+'  /factory_data/settings.ini'
                if command=='touch /tmp/hss2-1234abcd/stop':
                    state['usb']=ORIGINAL_USB
                    if lose_stop:raise StudioError('injected lost stop acknowledgement')
                    return ''
                if command in ('getprop sys.usb.state','getprop sys.usb.config'):return state['usb']
                if command=='getprop init.svc.adbd':return 'stopped'
                if command.startswith('test ! -e '):state['checks']+=1;return '0' if state['checks']>=2 else '1'
                if command in ('getprop persist.sys.usb.config','getprop ro.adb.secure'):return 'unchanged'
                raise AssertionError(command)
        m=object.__new__(adb.Managed);m.path='/tmp/hss2-1234abcd';m.created=True;m.launched=True;m.link=Native();m.libusb=None;m.fingerprint='a'*64;m.progress=lambda _:None
        m.before={'persist.sys.usb.config':'unchanged','ro.adb.secure':'unchanged'}
        class Server:
            def close(self):calls.append('server-close')
        m.server=Server()
        with patch.object(adb,'Link',lambda _:Native()),patch.object(adb.time,'sleep',lambda _:None):m.finish()
        self.assertEqual(state['checks'],2);self.assertEqual(calls.count('server-close'),1);self.assertIsNone(m.link)
        self.assertFalse(any(c.startswith('setprop ') for c in calls))
        self.assertEqual(calls.count('touch /tmp/hss2-1234abcd/stop'),1)
    def test_managed_lost_stop_ack_is_confirmed_without_rewrite(self):
        self.lose_stop=True;self.test_managed_finish_waits_for_files_and_preserves_properties()
    def test_managed_prelaunch_cleanup_checks_actual_mount(self):
        calls=[]
        class Native:
            healthy=True
            def close(self):calls.append('close')
            def call(self,command,timeout=15):
                transport.request(command+'; r=$?; printf "\\nHSS_STATUS=%s\\n" "$r"',1);calls.append(command)
                return 'YES' if command.startswith('if ') else ''
        m=object.__new__(adb.Managed);m.path='/tmp/hss2-1234abcd';m.created=True;m.launched=False;m.mounted=False;m.code_created=False;m.link=Native();m.server=None;m.progress=lambda _:None
        m.finish();self.assertIn('umount /tmp/hss2-1234abcd/code',calls);self.assertIn('rmdir /tmp/hss2-1234abcd',calls)
    def test_managed_collision_never_deleted(self):
        calls=[]
        class Native:
            healthy=True
            def close(self):calls.append('close')
        m=object.__new__(adb.Managed);m.path='/tmp/hss2-1234abcd';m.created=False;m.link=Native();m.server=None
        m.finish();self.assertEqual(calls,['close'])

def main():
    global original
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--original',type=Path);parser.add_argument('--report',type=Path,required=True);parser.add_argument('--case',action='append');args=parser.parse_args()
    if args.original:
        original=args.original.read_bytes();assert len(original)==SIZE and sha(original)==STOCK
    suite=unittest.TestSuite(Checks(name) for name in args.case) if args.case else unittest.defaultTestLoader.loadTestsFromTestCase(Checks)
    result=unittest.TextTestRunner(verbosity=2).run(suite)
    report=dict(passed=result.wasSuccessful(),checks=checks,checkCount=result.testsRun,interruptionCount=len(interruptions),interruptions=interruptions,deviceAccessed=False,officialStockInput=bool(args.original),limitations=['UID/SELinux are stand-ins','local shell and injected exits are not power-loss durability','no real USB, Windows or camera cold boot'])
    args.report.parent.mkdir(parents=True,exist_ok=True);args.report.write_text(json.dumps(report,indent=2)+'\n')
    raise SystemExit(0 if result.wasSuccessful() else 1)

if __name__=='__main__':main()
