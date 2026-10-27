"""Shared Mac/Windows Gen2 state machine and immutable first backup."""
import base64
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import zlib
from g2profile import STOCK,SIZE,GUI,ORIGINAL_USB,require,StudioError
from scripts import Baseline,ROOT,LOGO,LABEL,journal,install,restore,finish_cleanup,validate_logo


def digest(data):return hashlib.sha256(data).hexdigest()


def atomic(path,data,replace=True):
    path=Path(path);path.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
    require(replace or not path.exists(),'首次恢复备份已存在，禁止覆盖。')
    temporary=path.with_name(path.name+'.next')
    fd=os.open(temporary,os.O_CREAT|os.O_EXCL|os.O_WRONLY,0o600)
    try:
        with os.fdopen(fd,'wb') as stream:stream.write(data);stream.flush();os.fsync(stream.fileno())
        if replace:os.replace(temporary,path)
        else:
            # Exclusive creation prevents another instance replacing the first copy.
            with path.open('xb') as stream:stream.write(data);stream.flush();os.fsync(stream.fileno())
            path.chmod(0o600);temporary.unlink()
    finally:
        if temporary.exists():temporary.unlink()


def decode_record(raw,device):
    require(len(raw)<=16384,'二代恢复记录超出边界。')
    try:
        value=json.loads(raw);baseline=Baseline(value['device'],value['originalMetadata'],value['originalLabel']);baseline.validate()
        require(baseline.device==device and raw==journal(baseline,value['images']),'二代恢复记录或机身归属不符。')
        return value,baseline
    except (KeyError,TypeError,ValueError,json.JSONDecodeError) as exc:raise StudioError('二代恢复记录损坏，请保留原备份。') from exc


def same_baseline(a,b):return all(a[k]==b[k] for k in ('schema','backend','firmware','device','originalHash','originalMetadata','originalLabel'))


class Store:
    def __init__(self,parent,device):
        require(re.fullmatch('[0-9a-f]{64}',device) is not None,'机身指纹无效。')
        self.path=Path(parent)/'gen2'/device;self.device=device
    def pending(self):
        if not (self.path/'active').exists():return None
        raw=(self.path/'transaction.json').read_bytes();decode_record(raw,self.device);return raw
    def original(self):
        data=(self.path/'original-logo.bin').read_bytes();require(digest(data)==STOCK,'电脑首次原图备份损坏。');return data
    def save(self,record,original=None):
        value,_=decode_record(record,self.device);first=self.path/'baseline.json'
        if first.exists():
            old,_=decode_record(first.read_bytes(),self.device);require(same_baseline(old,value),'已有首次原图基线不同，禁止覆盖。')
        else:atomic(first,record,False)
        original_path=self.path/'original-logo.bin'
        if original_path.exists():self.original()
        else:
            require(original is not None and digest(original)==STOCK,'缺少这台相机完整的首次原图备份。');atomic(original_path,original,False)
        atomic(self.path/'transaction.json',record)
    def activate(self):atomic(self.path/'active',b'pending')
    def complete(self):(self.path/'active').unlink(missing_ok=True)


class Controller:
    def __init__(self,link,backups,progress=lambda _:None):self.link=link;self.backups=backups;self.progress=progress;self.store=None;self.mutated=False
    def call(self,command,timeout=15):return self.link.call(command,timeout)
    def absent(self,path):
        result=self.call('if [ -e '+path+' ] || [ -L '+path+' ];then printf PRESENT;else printf ABSENT;fi')
        require(result in ('PRESENT','ABSENT'),'文件存在状态未确认。');return result=='ABSENT'
    def regular(self,path):return self.call('if [ -f '+path+' ] && [ ! -L '+path+' ];then printf YES;else printf NO;fi')=='YES'
    def hash(self,path):
        words=self.call('sha256sum '+path).split();require(len(words)==2 and re.fullmatch('[0-9a-f]{64}',words[0]) is not None and words[1]==path,'相机文件哈希响应不完整。');return words[0]
    def metadata(self,path):return self.call("stat -c '%a %u %g %s %Y' "+path)
    def label(self,path):
        text=self.call('ls -Z '+path).split();require(len(text)==2 and text[1]==path,'相机安全标签响应不完整。');return text[0]
    def read(self,path,limit=SIZE):
        require(self.regular(path),'相机文件不是常规文件。')
        expected=self.hash(path);size=int(self.call('stat -c %s '+path));require(0<=size<=limit,'相机文件超出长度边界。')
        compressed_size=int(self.call('toybox gzip -c '+path+' | toybox wc -c'));require(1<=compressed_size<=limit+8192,'压缩文件长度异常。')
        data=bytearray()
        for block,offset in enumerate(range(0,compressed_size,8192)):
            text=self.call('toybox gzip -c '+path+' | toybox dd bs=8192 skip='+str(block)+' count=1 2>/dev/null | toybox base64')
            part=base64.b64decode(''.join(text.split()),validate=True);require(len(part)==min(8192,compressed_size-offset),'相机文件分块读取不完整。');data.extend(part)
        decoder=zlib.decompressobj(31);raw=decoder.decompress(data,limit+1)
        require(decoder.eof and not decoder.unused_data and not decoder.unconsumed_tail and len(raw)==size and digest(raw)==expected and self.hash(path)==expected,'原图备份读取校验失败。')
        return raw
    def upload(self,name,data):
        require(name in ('journal.json.next','image.bin','host-original.bin','install.sh','restore.sh','finish.sh'),'暂存文件名不符。')
        path=ROOT+'/'+name;require(self.absent(path) and self.absent(path+'.b64'),'上次传输尚未清理，请先恢复。')
        compressed=gzip.compress(data,mtime=0)
        if len(compressed)>8192:
            self.link.write_large(data,path)
        else:
            encoded=base64.b64encode(compressed).decode()
            for at in range(0,len(encoded),72):self.call("printf %s '"+encoded[at:at+72]+"' "+('>' if at==0 else '>>')+path+'.b64')
            self.call('toybox base64 -d '+path+'.b64 | toybox gzip -dc > '+path)
            self.call('rm -f '+path+'.b64')
        require(self.hash(path)==digest(data),'上传文件校验失败。')
    def verify_original(self,baseline):
        require(self.hash(LOGO)==STOCK and self.metadata(LOGO)==baseline.metadata and self.label(LOGO)==LABEL,'原厂图或文件属性未完全恢复。')
    def inspect(self):
        require(self.call('getprop ro.product.device')=='eagle2_hb722' and self.hash('/system/bin/camera-gui')==GUI,'二代机型或固件不匹配。')
        expected=getattr(self.link,'expected_usb_state',ORIGINAL_USB)
        require(self.call('getprop sys.usb.config')==self.call('getprop sys.usb.state')==expected,'相机 USB 正被其他操作占用，请等待原状态恢复后重新检测。')
        require(self.call('getprop init.svc.adbd')==('stopped' if expected==ORIGINAL_USB else 'running'),'相机文件传输服务状态与本次操作不符。')
        device=self.hash('/factory_data/settings.ini');self.store=Store(self.backups,device)
        require(self.absent('/factory_data/.hss-startup2-trial'),'发现未完成的二代试验，请先使用试验工具恢复。')
        require(self.regular(LOGO),'开机图不是原始常规文件。');current=self.hash(LOGO)
        exists=not self.absent(ROOT);record=None;raw=None;backup_good=False;journal_present=False
        if exists:
            self.call('test -d '+ROOT+' && test ! -L '+ROOT)
            if not self.absent(ROOT+'/owner-id'):require(self.regular(ROOT+'/owner-id') and self.call('cat '+ROOT+'/owner-id')==device,'机内目录不属于这台相机。')
            journal_present=not self.absent(ROOT+'/journal.json')
            if journal_present:require(self.regular(ROOT+'/owner-id'),'机内恢复记录缺少归属标记。')
            raw=self.read(ROOT+'/journal.json',16384) if journal_present else self.store.pending()
            require(raw is not None,'发现未归属的二代修改文件，请使用原工具恢复。')
            record,baseline=decode_record(raw,device)
            require(current in (STOCK,*record['images']),'当前开机图由其他工具修改，已停止操作。')
            backup_good=self.regular(ROOT+'/original-logo.bin') and self.hash(ROOT+'/original-logo.bin')==STOCK
            if backup_good:require(self.metadata(ROOT+'/original-logo.bin')==baseline.metadata and self.label(ROOT+'/original-logo.bin')==LABEL,'机内首次备份属性不符。')
            original=None
            if not (self.store.path/'original-logo.bin').exists():
                if backup_good:original=self.read(ROOT+'/original-logo.bin')
                elif current==STOCK:original=self.read(LOGO)
                else:raise StudioError('机内和电脑均缺少可校验的首次原图，已停止。')
            self.store.save(raw,original)
        else:
            require(current==STOCK,'开机图不是原厂或本 App 管理的图，请先用原工具恢复。')
            baseline=Baseline(device,self.metadata(LOGO),self.label(LOGO));baseline.validate()
            raw=journal(baseline,[STOCK]);record,_=decode_record(raw,device)
            original=None if (self.store.path/'original-logo.bin').exists() else self.read(LOGO)
            self.store.save(raw,original);self.verify_original(baseline);self.store.complete()
        partial=exists and any(not self.absent(ROOT+'/'+n) for n in ('image.bin.b64','install.sh.b64','journal.json.next','journal.json.next.b64','restore.sh.b64'))
        can_install=not exists or (journal_present and current!=STOCK and backup_good and not partial and self.metadata(LOGO)==baseline.metadata and self.label(LOGO)==LABEL)
        return dict(record=record,raw=raw,baseline=baseline,exists=exists,current=current,backup_good=backup_good,journal_present=journal_present,
                    result=dict(deviceTitle='X2D II 100C',edition='标准版',current='原厂开机画面' if not exists else '自定义开机画面' if can_install else '需要完成恢复',canInstall=can_install,canRestore=exists))
    def install(self,image):
        validate_logo(image)
        state=self.inspect();require(state['result']['canInstall'],'请先恢复未完成的二代安装。')
        image_hash=digest(image)
        if image_hash==state['current']:return state['result']
        images=sorted(set(state['record']['images']+[image_hash]));require(len(images)<=32,'换图记录已满，请先恢复原厂再安装。')
        record=journal(state['baseline'],images);self.store.save(record);self.store.activate();self.mutated=True
        self.progress('正在保护首次原厂备份')
        if not state['exists']:self.call('umask 077;mkdir '+ROOT+' && printf %s '+state['baseline'].device+' > '+ROOT+'/owner-id')
        else:
            require(self.hash(ROOT+'/journal.json')==digest(state['raw']),'恢复记录已变化，请重新检测。')
            self.call('rm -f '+ROOT+'/image.bin '+ROOT+'/install.sh')
        self.upload('journal.json.next',record)
        self.call('mv '+ROOT+'/journal.json.next '+ROOT+'/journal.json;sync')
        self.progress('正在上传自定义画面');self.upload('image.bin',image)
        script=install(state['baseline'],image_hash,state['current'],record).encode();self.upload('install.sh',script)
        self.progress('正在安装并核对画面')
        require(self.call('sh '+ROOT+'/install.sh',30)=='HSS2_INSTALLED','相机未确认安装完成，请保留恢复备份。')
        final=self.inspect();require(final['current']==image_hash and final['backup_good'],'安装读回或机内原图备份未通过检查。')
        return final['result']
    def restore(self):
        state=self.inspect()
        if not state['exists']:return state['result']
        self.store.activate();self.mutated=True;self.progress('正在恢复这台相机的原厂画面')
        if state['journal_present']:
            if state['current']!=STOCK and not state['backup_good']:
                self.call('rm -f '+ROOT+'/host-original.bin '+ROOT+'/host-original.bin.b64');self.upload('host-original.bin',self.store.original())
            name='restore.sh';script=restore(state['baseline'],state['raw']).encode()
        else:
            self.verify_original(state['baseline']);name='finish.sh';script=finish_cleanup(state['baseline']).encode()
        self.call('rm -f '+ROOT+'/'+name+' '+ROOT+'/'+name+'.b64');self.upload(name,script)
        require(self.call('sh '+ROOT+'/'+name,30)=='HSS2_RESTORED','原厂恢复结果未确认。')
        self.verify_original(state['baseline']);require(self.absent(ROOT),'原图已恢复，但机内安装文件清理未完成。')
        self.store.complete();return self.inspect()['result']
