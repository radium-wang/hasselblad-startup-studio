"""Exact Gen2 USB/native version and command65 contracts, bounded and fail closed."""
import secrets
import struct
import sys
import time
from pathlib import Path
from g2profile import require,StudioError


def crc(data):
    value=0
    for byte in data:
        value^=byte<<8
        for _ in range(8):value=((value<<1)^0x1021)&65535 if value&32768 else (value<<1)&65535
    return value


def request(command,cookie):
    raw=command.encode('ascii')
    require(len(raw)<=231 and b'\0' not in raw and 0<cookie<2**32,'内部相机命令超出边界。')
    body=bytearray(252);struct.pack_into('<III',body,0,65,0,cookie);struct.pack_into('<I',body,16,10)
    body[20:20+len(raw)]=raw;struct.pack_into('<I',body,12,crc(body[16:]))
    return b'\x0a\x00\x08\x05\xfc'+body


def parse_record(raw,cookie):
    require(len(raw) in (257,1024) and raw[:5]==b'\x09\x00\x05\x08\xfc','相机响应格式不符，已停止。')
    body=raw[5:257];cmd,fun,received,check=struct.unpack_from('<IIII',body)
    require(cmd==65 and received==cookie and check==crc(body[16:]),'相机响应校验不符，已停止。')
    require(fun in (0,4) and struct.unpack_from('<I',body,16)[0]==0,'相机命令未正常完成。')
    return fun,body[20:].split(b'\0',1)[0] if fun==4 else b''


def parse_version(raw,sequence):
    require(len(raw)==1024 and raw[:4]==b'\x03\0\x05\x08','二代版本响应格式不符。')
    end=5+raw[4]
    require(raw[5:8]==bytes((sequence,0,0x82)) and raw[8:end]==b'S9,v1.3.18.1','二代相机固件暂不支持，仅支持 1.3.18.1。')
    return '1.3.18.1'


def timeout_error(exc):return getattr(exc,'errno',None) in (60,110) or getattr(exc,'backend_error_code',None)==-7


class MacUsb:
    @staticmethod
    def devices(libusb):
        import usb.core,usb.backend.libusb1
        backend=usb.backend.libusb1.get_backend(find_library=lambda _:str(libusb))
        require(backend is not None,'内置 USB 组件无法加载。')
        return [d for d in usb.core.find(find_all=True,idVendor=0x2756,backend=backend) or [] if d.idProduct in (9,10,11)]

    @classmethod
    def probe(cls,libusb):
        devices=cls.devices(libusb)
        require(len(devices)<=1,'请只连接一台相机。')
        return len(devices)==1 and devices[0].idProduct==11

    def __init__(self,libusb):
        import usb.util
        self.util=usb.util;self.device=None;self.claimed=False
        devices=self.devices(libusb);require(len(devices)==1 and devices[0].idProduct==11,'未检测到单台 X2D II，请保持相机开机。')
        self.device=devices[0]
        try:
            cfg=self.device.get_active_configuration()
            choices=[i for i in cfg if (i.bInterfaceNumber,i.bAlternateSetting,i.bInterfaceClass,i.bInterfaceSubClass,i.bInterfaceProtocol)==(4,0,255,0,0)]
            require(len(choices)==1,'二代工厂接口不匹配。');i=choices[0]
            require(usb.util.get_string(self.device,i.iInterface)=='CTRL Interface' and sorted(e.bEndpointAddress for e in i)==[5,6,134,135] and all(e.bmAttributes&3==2 for e in i),'二代 USB 端点不匹配。')
            self.out=next(e for e in i if e.bEndpointAddress==6);self.input=next(e for e in i if e.bEndpointAddress==135)
            usb.util.claim_interface(self.device,4);self.claimed=True
        except Exception:self.close();raise

    def write(self,data):require(self.out.write(data,timeout=1500)==len(data),'USB 请求发送不完整。')
    def read(self,timeout=1000,size=1024):return bytes(self.input.read(size,timeout=timeout))
    def close(self):
        if self.device is not None:
            if self.claimed:self.util.release_interface(self.device,4)
            self.util.dispose_resources(self.device);self.device=None


class Link:
    def __init__(self,libusb):
        if sys.platform=='win32':
            from winusb import WinUsb
            self.usb=WinUsb()
        else:self.usb=MacUsb(libusb)
        self.healthy=True
        try:
            seq=secrets.randbelow(255)+1
            self.usb.write(bytes((4,0,8,5,5,seq,0,2,27,0)))
            self.version=parse_version(self.block(time.monotonic()+3),seq)
        except Exception:self.close();raise

    @staticmethod
    def probe(libusb):
        if sys.platform=='win32':
            from winusb import WinUsb
            return WinUsb.probe()
        return MacUsb.probe(libusb)

    def raw(self,command,timeout=15):
        require(self.healthy,'USB 响应状态未确认，请重新连接。')
        cookie=secrets.randbelow(2**32-1)+1;result=bytearray();records=0
        try:
            self.usb.write(request(command,cookie));end=time.monotonic()+timeout
            while time.monotonic()<end:
                fun,text=parse_record(self.block(end),cookie);records+=1;result.extend(text)
                require(len(result)<=16384 and records<=128,'相机文本响应超出边界。')
                if fun==0:return result.decode('ascii')
            raise StudioError('相机响应超时；请保持开机并重新检测。')
        except Exception:self.healthy=False;raise

    def block(self,deadline):
        # USBCtrlIn pads each Gen2 queued message to 1024 bytes. Consume that
        # complete block even when a driver returns its logical 257-byte prefix.
        buffer=bytearray()
        while time.monotonic()<deadline:
            try:chunk=self.usb.read(timeout=max(1,min(1000,int((deadline-time.monotonic())*1000))),size=1024-len(buffer))
            except Exception as exc:
                if timeout_error(exc):continue
                raise
            require(len(chunk)<=1024-len(buffer),'USB 响应超出边界。');buffer.extend(chunk)
            if len(buffer)==1024:return bytes(buffer)
        raise StudioError('相机响应超时；请保持开机并重新检测。')

    def call(self,command,timeout=15):
        suffix='; r=$?; printf "\\nHSS_STATUS=%s\\n" "$r"'
        text=self.raw(command+suffix,timeout)
        import re
        match=re.fullmatch(r'(.*)\nHSS_STATUS=([0-9]{1,3})\n?',text,re.S)
        if match is None:
            self.healthy=False
            raise StudioError('相机命令状态未确认。')
        require(int(match[2])==0,'相机命令执行失败，请保留恢复备份并重新检测。')
        return match[1].strip()

    def close(self):self.usb.close()
