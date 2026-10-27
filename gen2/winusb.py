"""Open only the second-generation CTRL MI_04 WinUSB interface on Windows x64.

No driver installation, gadget changes, alternate-setting changes or ADB opens.
"""
import ctypes as C
import re
import uuid
from g2profile import require,StudioError

GUID=C.c_ubyte*16
class Interface(C.Structure):_fields_=[('size',C.c_uint32),('guid',GUID),('flags',C.c_uint32),('reserved',C.c_void_p)]
class Device(C.Structure):_fields_=[('size',C.c_uint32),('guid',GUID),('instance',C.c_uint32),('reserved',C.c_void_p)]
class Descriptor(C.Structure):
    _pack_=1
    _fields_=[(n,C.c_ubyte) for n in ('length','type','number','alternate','endpoints','cls','sub','proto','index')]
class Pipe(C.Structure):_fields_=[('kind',C.c_int),('address',C.c_ubyte),('packet',C.c_ushort),('interval',C.c_ubyte)]


def physical_pid(value):
    match=re.fullmatch(r'USB\\VID_2756&PID_(0009|000A|000B)\\[^\\]+',value,re.I)
    return int(match[1],16) if match else 0


def select(rows,physical):
    require(len(physical)==1 and physical_pid(next(iter(physical)))==11,'请只连接一台 X2D II 相机。')
    found={}
    for row in rows:
        if physical_pid(row['physical'])!=11:continue
        if not re.fullmatch(r'USB\\VID_2756&PID_000B&MI_04\\[^\\]+',row['id'],re.I):continue
        if '#vid_2756&pid_000b&mi_04#' not in row['path'].lower():continue
        require(row['physical'].upper()==next(iter(physical)).upper(),'Windows 相机接口归属不符。')
        found.setdefault(row['id'].upper(),row['path'])
    require(len(found)==1,'未找到唯一二代工厂接口；请检查 CTRL MI_04 的 WinUSB 驱动。')
    return next(iter(found.values()))


class Api:
    def __init__(self):
        require(C.sizeof(C.c_void_p)==8,'二代 Windows 仅支持 64 位。')
        self.setup=C.WinDLL('setupapi',use_last_error=True);self.cm=C.WinDLL('cfgmgr32',use_last_error=True)
        self.kernel=C.WinDLL('kernel32',use_last_error=True);self.win=C.WinDLL('winusb',use_last_error=True)
        self.setup.SetupDiGetClassDevsW.argtypes=[C.POINTER(GUID),C.c_wchar_p,C.c_void_p,C.c_uint32];self.setup.SetupDiGetClassDevsW.restype=C.c_void_p
        self.setup.SetupDiEnumDeviceInterfaces.argtypes=[C.c_void_p,C.c_void_p,C.POINTER(GUID),C.c_uint32,C.POINTER(Interface)]
        self.setup.SetupDiGetDeviceInterfaceDetailW.argtypes=[C.c_void_p,C.POINTER(Interface),C.c_void_p,C.c_uint32,C.POINTER(C.c_uint32),C.POINTER(Device)]
        self.setup.SetupDiDestroyDeviceInfoList.argtypes=[C.c_void_p]
        self.cm.CM_Get_Device_IDW.argtypes=[C.c_uint32,C.c_wchar_p,C.c_uint32,C.c_uint32]
        self.cm.CM_Get_Parent.argtypes=[C.POINTER(C.c_uint32),C.c_uint32,C.c_uint32]
        self.cm.CM_Enumerate_Classes.argtypes=[C.c_uint32,C.POINTER(GUID),C.c_uint32]
        self.kernel.CreateFileW.argtypes=[C.c_wchar_p,C.c_uint32,C.c_uint32,C.c_void_p,C.c_uint32,C.c_uint32,C.c_void_p];self.kernel.CreateFileW.restype=C.c_void_p
        self.kernel.CloseHandle.argtypes=[C.c_void_p]
        self.win.WinUsb_Initialize.argtypes=[C.c_void_p,C.POINTER(C.c_void_p)]
        self.win.WinUsb_Free.argtypes=[C.c_void_p]
        self.win.WinUsb_QueryInterfaceSettings.argtypes=[C.c_void_p,C.c_ubyte,C.POINTER(Descriptor)]
        self.win.WinUsb_GetCurrentAlternateSetting.argtypes=[C.c_void_p,C.POINTER(C.c_ubyte)]
        self.win.WinUsb_QueryPipe.argtypes=[C.c_void_p,C.c_ubyte,C.c_ubyte,C.POINTER(Pipe)]
        self.win.WinUsb_SetPipePolicy.argtypes=[C.c_void_p,C.c_ubyte,C.c_uint32,C.c_uint32,C.c_void_p]
        for name in ('WinUsb_ReadPipe','WinUsb_WritePipe'):
            getattr(self.win,name).argtypes=[C.c_void_p,C.c_ubyte,C.c_void_p,C.c_uint32,C.POINTER(C.c_uint32),C.c_void_p]

    def device_id(self,instance):
        text=C.create_unicode_buffer(512)
        return text.value if self.cm.CM_Get_Device_IDW(instance,text,512,0)==0 else ''

    def parent(self,instance):
        for _ in range(16):
            value=self.device_id(instance)
            if physical_pid(value):return value
            parent=C.c_uint32()
            if self.cm.CM_Get_Parent(C.byref(parent),instance,0)!=0 or parent.value==instance:return ''
            instance=parent.value
        return ''

    def rows(self,guid):
        if isinstance(guid,str):guid=GUID.from_buffer_copy(uuid.UUID(guid).bytes_le)
        handle=self.setup.SetupDiGetClassDevsW(C.byref(guid),None,None,0x12)
        require(handle not in (None,C.c_void_p(-1).value),'Windows USB 接口枚举失败。')
        rows=[]
        try:
            for index in range(1024):
                interface=Interface();interface.size=C.sizeof(interface)
                if not self.setup.SetupDiEnumDeviceInterfaces(handle,None,C.byref(guid),index,C.byref(interface)):
                    require(C.get_last_error()==259,'Windows USB 接口枚举未完成。');return rows
                length=C.c_uint32();device=Device();device.size=C.sizeof(device)
                self.setup.SetupDiGetDeviceInterfaceDetailW(handle,C.byref(interface),None,0,C.byref(length),C.byref(device))
                if length.value<8:continue
                require(length.value<=65536,'Windows USB 接口路径异常。')
                buffer=C.create_string_buffer(length.value);C.c_uint32.from_buffer(buffer).value=8
                if self.setup.SetupDiGetDeviceInterfaceDetailW(handle,C.byref(interface),buffer,length,C.byref(length),C.byref(device)):
                    path=C.wstring_at(C.addressof(buffer)+4)
                    if '#vid_2756&pid_' in path.lower():rows.append(dict(path=path,id=self.device_id(device.instance),physical=self.parent(device.instance)))
            raise StudioError('Windows USB 接口数量超出边界。')
        finally:self.setup.SetupDiDestroyDeviceInfoList(handle)

    def physical(self):return {r['physical'].upper() for r in self.rows('A5DCBF10-6530-11D2-901F-00C04FB951ED') if physical_pid(r['physical'])}

    def factory(self,physical):
        preferred=self.rows('CC135687-5267-4313-B0C7-C344609D6EF0')
        if any('&MI_04\\' in r['id'].upper() for r in preferred):return select(preferred,physical)
        rows=[]
        for index in range(65536):
            guid=GUID();result=self.cm.CM_Enumerate_Classes(index,C.byref(guid),1)
            if result==0x25:return select(rows,physical)
            if result==0x1f:continue
            require(result==0,'Windows 接口类枚举失败。');rows.extend(self.rows(guid))
        raise StudioError('Windows 接口类数量超出边界。')


class WinUsb:
    @staticmethod
    def probe():
        devices=Api().physical();require(len(devices)<=1,'请只连接一台相机。')
        return len(devices)==1 and physical_pid(next(iter(devices)))==11

    def __init__(self):
        self.api=Api();self.file=None;self.handle=C.c_void_p()
        try:
            physical=self.api.physical();path=self.api.factory(physical)
            self.file=self.api.kernel.CreateFileW(path,0xc0000000,0,None,3,0x40000000,None)
            require(self.file not in (None,C.c_void_p(-1).value),'二代 USB 接口被占用或驱动未就绪。')
            require(self.api.win.WinUsb_Initialize(self.file,C.byref(self.handle)),'二代 WinUSB 初始化失败。')
            alternate=C.c_ubyte();desc=Descriptor()
            require(self.api.win.WinUsb_GetCurrentAlternateSetting(self.handle,C.byref(alternate)) and alternate.value==0,'二代 USB alternate 不符。')
            require(self.api.win.WinUsb_QueryInterfaceSettings(self.handle,0,C.byref(desc)) and (desc.number,desc.alternate,desc.cls,desc.sub,desc.proto,desc.endpoints)==(4,0,255,0,0,4),'二代工厂接口描述不符。')
            endpoints=[]
            for index in range(4):
                pipe=Pipe();require(self.api.win.WinUsb_QueryPipe(self.handle,0,index,C.byref(pipe)) and pipe.kind==2,'二代 USB 管道不符。');endpoints.append(pipe.address)
            require(sorted(endpoints)==[5,6,134,135],'二代 USB 端点不符。')
        except Exception:self.close();raise

    def transfer(self,endpoint,data,size,timeout):
        value=C.c_uint32(timeout);require(self.api.win.WinUsb_SetPipePolicy(self.handle,endpoint,3,4,C.byref(value)),'二代 USB 超时设置失败。')
        count=C.c_uint32();fn=self.api.win.WinUsb_ReadPipe if endpoint==135 else self.api.win.WinUsb_WritePipe
        if not fn(self.handle,endpoint,data,size,C.byref(count),None):
            error=C.get_last_error()
            if error in (121,1460):
                if endpoint==135 and count.value:return count.value
                raise TimeoutError(110,'USB timeout')
            raise StudioError('二代 USB 传输中断，请保持开机后重新检测。')
        return count.value

    def write(self,raw):
        data=C.create_string_buffer(raw,len(raw));require(self.transfer(6,data,len(raw),1500)==len(raw),'USB 请求发送不完整。')
    def read(self,timeout=1000,size=1024):
        require(1<=size<=1024,'USB 读取长度异常。')
        data=C.create_string_buffer(size);count=self.transfer(135,data,size,timeout);require(count<=size,'USB 响应长度异常。');return data.raw[:count]
    def close(self):
        if self.handle.value:self.api.win.WinUsb_Free(self.handle);self.handle=C.c_void_p()
        if self.file not in (None,C.c_void_p(-1).value):self.api.kernel.CloseHandle(self.file);self.file=None
