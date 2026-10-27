"""Shared Gen2 helper. Hardware is opened only with explicit --device."""
import argparse
import json
import os
from pathlib import Path
import sys
import traceback

sys.stdout.reconfigure(encoding='utf-8')
sys.stderr.reconfigure(encoding='utf-8')
sys.path.insert(0,str(Path(__file__).resolve().parent))
from g2profile import StudioError,require
from transport import Link
from core import Controller
from codec import encode
from adb import Managed


def emit(kind,**value):print(json.dumps(dict(type=kind,**value),ensure_ascii=False),flush=True)


class Lock:
    def __init__(self,backups):
        directory=Path(backups)/'gen2';directory.mkdir(parents=True,exist_ok=True,mode=0o700)
        self.file=(directory/'session.lock').open('a+b');self.file.seek(0)
        if self.file.read(1)==b'':self.file.write(b'\0');self.file.flush()
        self.file.seek(0)
        try:
            if sys.platform=='win32':
                import msvcrt
                msvcrt.locking(self.file.fileno(),msvcrt.LK_NBLCK,1)
            else:
                import fcntl
                fcntl.flock(self.file,fcntl.LOCK_EX|fcntl.LOCK_NB)
        except OSError as exc:self.file.close();raise StudioError('另一个换图操作正在进行，请稍后重试。') from exc
    def close(self):self.file.close()


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--device',action='store_true')
    parser.add_argument('--operation',choices=('probe','inspect','install','restore'),required=True)
    parser.add_argument('--backups',type=Path,required=True);parser.add_argument('--libusb',type=Path,required=True)
    parser.add_argument('--adb',type=Path,required=True);parser.add_argument('--input',type=Path)
    args=parser.parse_args();require(args.device,'默认不访问设备；缺少显式设备入口。')
    lock=None;link=None;controller=None;result=None;failure=None
    try:
        lock=Lock(args.backups)
        if args.operation=='probe':result=dict(gen2=Link.probe(args.libusb))
        else:
            progress=lambda message:emit('progress',message=message)
            progress('正在检查二代机型、固件和恢复基线')
            image=None
            if args.operation=='install':
                require(args.input is not None and args.input.stat().st_size<=3145760,'请选择有效的自定义图片。')
                progress('正在准备二代彩色画面');image=encode(args.input.read_bytes())
            link=Managed(args.libusb,args.adb,progress);controller=Controller(link,args.backups,progress)
            if args.operation=='install':result=controller.install(image)
            elif args.operation=='restore':result=controller.restore()
            else:result=controller.inspect()['result']
    except Exception as exc:
        failure=str(exc) if isinstance(exc,StudioError) else '二代操作未完成，请保持开机并保留恢复备份。'
        traceback.print_exc(file=sys.stderr)
        if args.operation=='install' and controller is not None and controller.mutated and link.healthy:
            try:
                emit('progress',message='安装未完成，正在尝试恢复原厂')
                controller.restore();failure+='\n已自动恢复原厂，请重新检测。'
            except Exception as restore_error:
                traceback.print_exc(file=sys.stderr);failure+='\n自动恢复未确认，请重新连接后使用恢复按钮。'
    finally:
        if link is not None:
            try:link.finish()
            except Exception as exc:
                traceback.print_exc(file=sys.stderr)
                failure=(failure+'\n' if failure else '画面操作已完成；')+str(exc)
        if lock is not None:lock.close()
    if failure:emit('error',message=failure);return 2
    emit('result',data=result);return 0


if __name__=='__main__':
    try:raise SystemExit(main())
    except StudioError as exc:emit('error',message=str(exc));raise SystemExit(2)
