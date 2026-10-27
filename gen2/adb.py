"""Lazy, bounded Gen2 large-file channel. Host-owned ADB and private RAM only."""
import base64
import gzip
import hashlib
import os
from pathlib import Path
import re
import secrets
import socket
import subprocess
import sys
import tempfile
import time
from g2profile import ORIGINAL_USB,require,StudioError
from transport import Link

TARGET=ORIGINAL_USB+',adb'
RAM_LABEL='u:object_r:system_file:s0'


def switch_script(path):
    require(re.fullmatch('/tmp/hss2-[0-9a-f]{8}',path) is not None,'临时传输路径无效。')
    return f'''#!/system/bin/sh
export PATH=/system/bin:/system/xbin:/sbin
cd {path} || exit 20
wait_state() {{ n=0; while [ "$(getprop sys.usb.state)" != "$1" ]; do n=$((n+1)); [ "$n" -lt 15 ] || return 1; sleep 1; done; }}
cleanup() {{
 rm -f code/data
 n=0; while grep -F ' {path}/code ' /proc/mounts >/dev/null; do
  umount {path}/code && break
  n=$((n+1)); [ "$n" -lt 5 ] || {{ echo cleanup_failed > phase; return; }}; sleep 1
 done
 rmdir code || return
 rm -f owner-id switch.sh switch.sh.b64 switch.log phase stop
 cd /tmp; rmdir {path}
}}
restore() {{
 trap - EXIT HUP INT TERM
 setprop sys.usb.config none; wait_state none
 setprop sys.usb.config {ORIGINAL_USB}
 if wait_state {ORIGINAL_USB}; then echo restored > phase; cleanup; else echo restore_failed > phase; fi
}}
test "$(getprop sys.usb.config)" = {ORIGINAL_USB} || exit 21
test "$(getprop sys.usb.state)" = {ORIGINAL_USB} || exit 22
test "$(getprop init.svc.adbd)" = stopped || exit 23
trap restore EXIT HUP INT TERM
echo armed > phase
sleep 3
setprop sys.usb.config none; wait_state none || exit 24
setprop sys.usb.config {TARGET}; wait_state {TARGET} || exit 25
echo enabled > phase
n=0
while [ "$n" -lt 180 ] && [ ! -e stop ]; do sleep 1; n=$((n+1)); done
'''.encode()


class PrivateAdb:
    def __init__(self,executable):
        self.directory=tempfile.TemporaryDirectory(prefix='startup-adb-');self.path=Path(self.directory.name)
        self.log=(self.path/'server.log').open('wb');self.task=None;self.executable=str(executable);self.serial=None
        self.env=os.environ.copy()
        for key in ('ADB_SERVER_SOCKET','ANDROID_ADB_SERVER_PORT','ANDROID_SERIAL','ADB_TRACE','PYTHONHOME','PYTHONPATH'):self.env.pop(key,None)
        self.env.update(ANDROID_USER_HOME=str(self.path/'android-home'),ADB_MDNS_AUTO_CONNECT='0')
        with socket.socket() as reservation:reservation.bind(('127.0.0.1',0));self.port=reservation.getsockname()[1]
        try:
            self.task=subprocess.Popen([self.executable,'-L','tcp:localhost:'+str(self.port),'nodaemon','server'],env=self.env,stdin=subprocess.DEVNULL,stdout=self.log,stderr=subprocess.STDOUT)
            end=time.monotonic()+10
            while time.monotonic()<end:
                require(self.task.poll() is None,'内置文件传输服务未能启动。')
                try:
                    with socket.create_connection(('127.0.0.1',self.port),timeout=.2) as client:
                        client.sendall(b'000chost:version');reply=bytearray()
                        while len(reply)<12:
                            chunk=client.recv(12-len(reply))
                            if not chunk:break
                            reply.extend(chunk)
                        if re.fullmatch(rb'OKAY0004[0-9a-fA-F]{4}',reply):return
                except OSError:pass
                time.sleep(.1)
            raise StudioError('内置文件传输服务启动超时。')
        except Exception:self.close();raise

    def run(self,args,selected=True,cwd=None,timeout=20):
        command=[self.executable,'-H','127.0.0.1','-P',str(self.port)]
        if selected:
            require(self.serial is not None,'文件传输设备尚未核验。');command+=['-s',self.serial]
        try:result=subprocess.run(command+args,env=self.env,cwd=cwd,stdin=subprocess.DEVNULL,capture_output=True,timeout=timeout)
        except subprocess.TimeoutExpired as exc:raise StudioError('文件传输超时，请保持相机开机后重新检测。') from exc
        require(len(result.stdout)<=4194304 and len(result.stderr)<=262144,'文件传输响应过大。')
        require(result.returncode==0,'文件传输未完成，请检查相机连接和 ADB 接口驱动。')
        return result.stdout.decode('ascii').strip()
    def close(self):
        try:
            if self.task is not None and self.task.poll() is None:
                self.task.terminate()
                try:self.task.wait(timeout=5)
                except subprocess.TimeoutExpired:self.task.kill();self.task.wait(timeout=5)
        finally:
            if not self.log.closed:self.log.close()
            self.directory.cleanup()


class Managed:
    def __init__(self,libusb,adb,progress):
        self.libusb=libusb;self.adb_path=Path(adb);self.progress=progress;self.link=Link(libusb)
        self.server=None;self.path=None;self.launched=False;self.mounted=False;self.created=False;self.code_created=False;self.fingerprint=None;self.before={}
    @property
    def healthy(self):return self.link is not None and self.link.healthy
    @property
    def expected_usb_state(self):return TARGET if self.server is not None else ORIGINAL_USB
    def call(self,command,timeout=15):
        require(self.link is not None,'相机正在重新连接，请稍候。');return self.link.call(command,timeout)
    def small_upload(self,data,path):
        encoded=base64.b64encode(data).decode()
        for at in range(0,len(encoded),72):self.call("printf %s '"+encoded[at:at+72]+"' "+('>' if at==0 else '>>')+path+'.b64')
        self.call('toybox base64 -d '+path+'.b64 > '+path)
        require(self.call('sha256sum '+path).split()[0]==hashlib.sha256(data).hexdigest(),'临时恢复脚本传输校验失败。')
    def prepare(self):
        if self.server is not None:return
        require(self.adb_path.is_file(),'内置文件传输组件缺失，请完整解压 App。')
        self.progress('正在准备临时文件传输通道')
        identity=self.call('id');require('uid=0(root)' in identity and 'context=u:r:hbl_camera_service:s0' in identity,'二代诊断权限与已验证状态不同。')
        self.fingerprint=self.call('sha256sum /factory_data/settings.ini').split()[0]
        for key in ('sys.usb.config','sys.usb.state','init.svc.adbd','persist.sys.usb.config','ro.adb.secure'):self.before[key]=self.call('getprop '+key)
        require(self.before['sys.usb.config']==self.before['sys.usb.state']==ORIGINAL_USB and self.before['init.svc.adbd']=='stopped','当前 USB 状态不适合准备传输，请先关闭其他相机工具。')
        self.path='/tmp/hss2-'+secrets.token_hex(4);p=self.path
        print('temporary transfer namespace: '+p,file=sys.stderr)
        self.call('umask 077;test ! -e '+p+' && test ! -L '+p+' && mkdir '+p)
        self.created=True
        self.call('printf %s '+self.fingerprint+' > '+p+'/owner-id')
        self.call('mkdir '+p+'/code');self.code_created=True
        self.call('chmod 700 '+p+'/code')
        self.call('mount -t tmpfs -o size=8m,mode=0700,nosuid,nodev,rootcontext='+RAM_LABEL+' tmpfs '+p+'/code');self.mounted=True
        mount=self.call("grep -F ' "+p+"/code ' /proc/mounts")
        flags=mount.split()[3].split(',')
        require(mount.split()[:3]==['tmpfs',p+'/code','tmpfs'] and {'rw','nosuid','nodev','rootcontext='+RAM_LABEL,'size=8192k'}<=set(flags),'临时传输内存挂载未通过检查。')
        require(self.call('ls -Zd '+p+'/code')==RAM_LABEL+' '+p+'/code','临时传输内存标签不符。')
        self.small_upload(switch_script(p),p+'/switch.sh');self.call('sh -n '+p+'/switch.sh')
        self.launched=True
        pid=self.call('/system/bin/nohup /system/bin/sh '+p+'/switch.sh </dev/null >'+p+'/switch.log 2>&1 & echo $!')
        require(re.fullmatch('[1-9][0-9]{0,6}',pid) is not None,'临时 USB 自动恢复任务未确认。')
        self.link.close();self.link=None
        if sys.platform=='win32':
            from winusb import Api
            end=time.monotonic()+25
            while time.monotonic()<end:
                if any(re.fullmatch(r'USB\\VID_2756&PID_000B&MI_06\\[^\\]+',r['id'],re.I) for r in Api().rows('F72FE0D4-CBCB-407D-8814-9ED673D0DD6B')):break
                time.sleep(.5)
            else:raise StudioError('Windows 二代 ADB 接口未就绪，请检查 MI_06 驱动。')
        else:time.sleep(4)
        self.server=PrivateAdb(self.adb_path);end=time.monotonic()+25
        while time.monotonic()<end:
            rows=[line.split() for line in self.server.run(['devices'],False).splitlines()[1:] if line.strip()]
            require(len(rows)<=1,'检测到多个文件传输设备，请只保留相机。')
            if len(rows)==1 and len(rows[0])==2 and rows[0][1]=='device':
                require(re.fullmatch('[A-Za-z0-9._-]+',rows[0][0]) is not None,'ADB 设备标识无效。');self.server.serial=rows[0][0];break
            time.sleep(.5)
        require(self.server.serial is not None,'等待相机文件传输通道超时。')
        require(self.server.run(['shell','getprop ro.product.device'])=='eagle2_hb722','文件传输目标不是二代相机。')
        self.link=Link(self.libusb)
        require(self.call('sha256sum /factory_data/settings.ini').split()[0]==self.fingerprint and self.call('getprop sys.usb.state')==TARGET,'重新连接后的相机身份或 USB 状态不符。')
        self.progress('文件传输通道已就绪')
    def write_large(self,data,target):
        require(target in ('/factory_data/.hss-startup2/image.bin','/factory_data/.hss-startup2/host-original.bin'),'大文件目标路径无效。')
        self.prepare();require(len(data)<=2097152,'传输文件过大。')
        with tempfile.TemporaryDirectory(prefix='startup-upload-') as directory:
            Path(directory,'data').write_bytes(data)
            self.server.run(['push','data',self.path+'/code/data'],cwd=directory)
        require(self.call('sha256sum '+self.path+'/code/data').split()[0]==hashlib.sha256(data).hexdigest(),'文件传输读回校验失败。')
        self.call('cp '+self.path+'/code/data '+target);self.call('rm -f '+self.path+'/code/data')
    def finish(self):
        error=None
        try:
            if self.path is not None and self.created:
                self.progress('正在清理临时传输并核对 USB 状态')
                if self.launched:
                    try:
                        if self.link is None or not self.link.healthy:
                            if self.link:self.link.close()
                            self.link=Link(self.libusb)
                        require(self.call('sha256sum /factory_data/settings.ini').split()[0]==self.fingerprint,'USB 收尾时相机身份不符。')
                        if self.call('getprop sys.usb.state')==TARGET:self.call('touch '+self.path+'/stop')
                    except Exception as stop_error:
                        # USB can disappear before the stop command's final reply.
                        # Never resend an uncertain write; confirm the outcome below.
                        print('USB stop acknowledgement unavailable: '+str(stop_error),file=sys.stderr)
                    if self.link:self.link.close();self.link=None
                    if self.server:self.server.close();self.server=None
                    end=time.monotonic()+25;confirmed=False
                    while time.monotonic()<end:
                        try:
                            self.link=Link(self.libusb)
                            require(self.call('sha256sum /factory_data/settings.ini').split()[0]==self.fingerprint,'USB 收尾重新连接到不同相机。')
                            if self.call('getprop sys.usb.state')==self.call('getprop sys.usb.config')==ORIGINAL_USB and self.call('getprop init.svc.adbd')=='stopped':
                                if self.call('test ! -e '+self.path+' && test ! -L '+self.path+';echo $?')=='0':
                                    confirmed=True;break
                            self.link.close();self.link=None
                        except Exception:
                            if self.link:self.link.close();self.link=None
                        time.sleep(.5)
                    require(confirmed,'USB 原状态或临时文件清理未确认，请完整关机再开机后重新检测。')
                    for key in ('persist.sys.usb.config','ro.adb.secure'):require(self.call('getprop '+key)==self.before[key],'USB 原始属性发生变化。')
                else:
                    # Check actual state: a successful mkdir/mount can lose its USB completion.
                    exists=self.call('if [ -e '+self.path+'/code ] || [ -L '+self.path+'/code ];then printf YES;else printf NO;fi')
                    if exists=='YES':
                        self.call('test -d '+self.path+'/code && test ! -L '+self.path+'/code')
                        mounted=self.call("if grep -F ' "+self.path+"/code ' /proc/mounts >/dev/null;then printf YES;else printf NO;fi")
                        self.call('rm -f '+self.path+'/code/data')
                        if mounted=='YES':self.call('umount '+self.path+'/code')
                        self.call('rmdir '+self.path+'/code')
                    self.call('rm -f '+self.path+'/owner-id '+self.path+'/switch.sh '+self.path+'/switch.sh.b64')
                    self.call('rmdir '+self.path)
            elif self.path is not None and not self.healthy:
                raise StudioError('临时目录创建结果未确认，请完整关机再开机后重新检测。')
        except Exception as exc:error=exc
        finally:
            if self.server:self.server.close();self.server=None
            if self.link:self.link.close();self.link=None
        if error:raise StudioError('USB 或临时文件收尾未确认：'+str(error)) from error
