"""执行新 ARM64 读取器和原厂像素循环；全部离线，无 USB。"""
import argparse,hashlib,io,json,struct,sys
from pathlib import Path
from elftools.elf.elffile import ELFFile
from unicorn import Uc, UC_ARCH_ARM64, UC_MODE_ARM, UC_HOOK_CODE
from unicorn.arm64_const import *
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'tools'))
from firmware_layout import read_va,X2D_HASH

def fnv(b):
 h=2166136261
 for v in b:h=((h^v)*16777619)&0xffffffff
 return h

def resource(w,h,rgb=False):
 b=bytes(c for y in range(h) for x in range(w) for c in ((x*37%256,y*19%256,(x+y)*11%256,0) if rgb else ((x*37+y*19)%256,)))
 return struct.pack('<8s6I',b'X2DLOGO3',2 if rgb else 1,w,h,len(b),fnv(b),0)+b

def main():
 p=argparse.ArgumentParser();p.add_argument('stock',type=Path);p.add_argument('adapter',type=Path);a=p.parse_args()
 raw=a.stock.read_bytes();assert hashlib.sha256(raw).hexdigest()==X2D_HASH
 m=json.loads((a.adapter/'adapter.json').read_text());b=(a.adapter/'adapter.so').read_bytes();assert hashlib.sha256(b).hexdigest()==m['adapterSHA256']
 elf=ELFFile(io.BytesIO(b));stock=ELFFile(io.BytesIO(raw));cases=[]
 good=resource(321,239)
 def add(name,v=2,mode=b'3',image=good,alloc=True,fb=True,chunk=65536,expected=True):cases.append((name,v,mode,image,alloc,fb,chunk,expected))
 for v in (0,1,2,3,-1):add('factory-'+str(v),v)
 add('maximum-canvas',image=resource(1024,768))
 add('one-pixel',image=resource(1,1))
 add('partial-reads',chunk=7)
 for mode in (None,b'',b'2',b'33',b'3\n'):add('mode-'+repr(mode),mode=mode,expected=False)
 add('missing-resource',image=None,expected=False)
 add('read-error',image=b'io-error',expected=False)
 add('truncated-header',image=good[:20],expected=False)
 add('truncated-pixels',image=good[:-1],expected=False)
 add('extra-data',image=good+b'x',expected=False)
 for name,at,val in [('bad-magic',0,0),('bad-version',8,3),('zero-width',12,0),('oversize-width',12,1025),('oversize-height',16,769),('bad-size',20,7),('bad-checksum',24,0),('reserved',28,1)]:
  bad=bytearray(good);struct.pack_into('<I',bad,at,val);add(name,image=bytes(bad),expected=False)
 add('allocation-failure-earth',alloc=False,expected=False)
 add('allocation-failure-ordinary',v=0,alloc=False,expected=False)
 add('no-framebuffer',fb=False,expected=False)
 rgb_enabled='rgb_draw' in m['symbols']
 if rgb_enabled:
  for v in (0,2):add('rgb-factory-'+str(v),v=v,image=resource(250,250,True))
  add('rgb-maximum',image=resource(1024,768,True))
  add('rgb-one-pixel',image=resource(1,1,True))
  add('rgb-partial-reads',image=resource(31,17,True),chunk=7)
  add('rgb-truncated',image=resource(31,17,True)[:-1],expected=False)
  add('rgb-trailing',image=resource(31,17,True)+b'x',expected=False)
  add('rgb-allocation-failure',image=resource(31,17,True),alloc=False,expected=False)
  bad=bytearray(resource(31,17,True));bad[-4]^=1;add('rgb-checksum',image=bytes(bad),expected=False)
  bad=bytearray(resource(31,17,True));struct.pack_into('<I',bad,20,31*17);add('rgb-wrong-byte-count',image=bytes(bad),expected=False)
  add('rgb-three-byte-framebuffer',image=resource(31,17,True),expected=False)
  add('rgb-short-stride',image=resource(31,17,True),expected=False)
 fb,heap,stack=0x40000000,0x42000000,0x50000000
 results=[]
 for name,v,mode,image,alloc,has_fb,chunk,expected_custom in cases:
  u=Uc(UC_ARCH_ARM64,UC_MODE_ARM)
  for s in elf.iter_segments():
   if s['p_type']=='PT_LOAD':
    start=s['p_vaddr']&~4095;end=(s['p_vaddr']+s['p_memsz']+4095)&~4095;u.mem_map(start,end-start);u.mem_write(s['p_vaddr'],s.data())
  width,height,stride=1024,768,4096;total=stride*height
  u.mem_map(fb-4096,total+8192);u.mem_write(fb-4096,b'\xa5'*4096);u.mem_write(fb+total,b'\xa5'*4096)
  u.mem_map(heap-4096,3145728+8192);u.mem_write(heap-4096,b'\xa5'*4096);u.mem_write(heap+3145728,b'\xa5'*4096);u.mem_map(stack,65536);sp=stack+32768
  for r,val in [(UC_ARM64_REG_SP,sp),(UC_ARM64_REG_W0,v&0xffffffff),(UC_ARM64_REG_X23,width),(UC_ARM64_REG_X24,height),(UC_ARM64_REG_X25,fb if has_fb else 0),(UC_ARM64_REG_W26,3 if name=='rgb-three-byte-framebuffer' else 4),(UC_ARM64_REG_X30,0x12345678)]:u.reg_write(r,val)
  u.mem_write(sp+48,struct.pack('<Q',fb if has_fb else 0));u.mem_write(sp+64,struct.pack('<I',stride if name!='rgb-short-stride' else 4092))
  state={'open':{},'fds':{},'alloc':0,'free':0,'finish':False}
  def ret(n):u.reg_write(UC_ARM64_REG_X0,n&0xffffffffffffffff);u.reg_write(UC_ARM64_REG_PC,u.reg_read(UC_ARM64_REG_X30))
  def hook(uc,pc,size,data):
   if pc==0x7D228:
    path=bytes(u.mem_read(u.reg_read(UC_ARM64_REG_X0),100)).split(b'\0')[0].decode();assert u.reg_read(UC_ARM64_REG_W1)==0
    content=mode if path.endswith('mode3') else image;assert path in ['/system/lib64/weston/x2d-startup-mode3','/system/lib64/weston/x2d-startup-gray3.bin']
    if content is None:ret(-1)
    else:fd=3+len(state['open']);state['open'][fd]=content;state['fds'][fd]=0;ret(fd)
   elif pc==0x7D3B8:
    fd=u.reg_read(UC_ARM64_REG_W0);assert fd in state['fds'];data=state['open'][fd]
    if data==b'io-error':ret(-1);return
    at=state['fds'][fd];n=min(u.reg_read(UC_ARM64_REG_X2),chunk,len(data)-at);assert n>=0
    if n:u.mem_write(u.reg_read(UC_ARM64_REG_X1),data[at:at+n])
    state['fds'][fd]+=n;ret(n)
   elif pc==0x7BC88:
    fd=u.reg_read(UC_ARM64_REG_W0);assert fd in state['fds'];del state['fds'][fd];ret(0)
   elif pc==0x7CC18:
    n=u.reg_read(UC_ARM64_REG_X0);assert 1<=n<=3145728;state['alloc']+=1;ret(heap if alloc else 0)
   elif pc==0x7B498:
    assert u.reg_read(UC_ARM64_REG_X0)==heap and alloc;state['free']+=1;ret(0)
   elif pc==0x7B7F8:
    dest=u.reg_read(UC_ARM64_REG_X0);n=u.reg_read(UC_ARM64_REG_X2);assert fb<=dest<=dest+n<=fb+total
    if n:u.mem_write(dest,bytes([u.reg_read(UC_ARM64_REG_W1)&255])*n)
    ret(dest)
   elif pc in (0x946FC,0x94724) or (name in ('rgb-three-byte-framebuffer','rgb-short-stride') and pc==0x945A8):state['finish']=True;u.emu_stop()
  for pc in (0x7D228,0x7D3B8,0x7BC88,0x7CC18,0x7B498,0x7B7F8,0x946FC,0x94724,0x945A8):u.hook_add(UC_HOOK_CODE,hook,begin=pc,end=pc)
  try:u.emu_start(0x945A0,0,timeout=60000000,count=50000000)
  except Exception:
   print(name,hex(u.reg_read(UC_ARM64_REG_PC)),state,flush=True);raise
  assert state['finish'] and not state['fds'] and u.reg_read(UC_ARM64_REG_SP)==sp,name
  for addr in (fb-4096,fb+total,heap-4096,heap+3145728):assert bytes(u.mem_read(addr,4096))==b'\xa5'*4096,name
  if has_fb and name not in ('rgb-three-byte-framebuffer','rgb-short-stride'):
   if expected_custom:w,h=struct.unpack_from('<II',image,12);pixels=image[32:]
   else:
    w,h,pa,px=(570,426,0x17FE89,0x17FF5C) if v==2 else (162,128,0x17AD78,0x17AD89)
    ix=read_va(stock,px,w*h);pal=read_va(stock,pa,max(ix)+1);pixels=bytes(pal[i] for i in ix)
   expect=bytearray(total);left,top=(width-w)//2,(height-h)//2
   for y in range(h):
    row=pixels[y*w*4:(y+1)*w*4] if expected_custom and struct.unpack_from('<I',image,8)[0]==2 else bytes(c for v2 in pixels[y*w:(y+1)*w] for c in (v2,v2,v2,0));at=(top+y)*stride+left*4;expect[at:at+w*4]=row
   assert bytes(u.mem_read(fb,total))==expect,(name,'framebuffer mismatch')
  assert state['free']==(state['alloc'] if alloc else 0),name
  results.append(dict(name=name,passed=True));print('PASS',name,flush=True)
 (a.adapter/'loader-verification.json').write_text(json.dumps(dict(adapterSHA256=m['adapterSHA256'],cases=results,deviceAccessed=False,framebufferGuards=True,heapGuards=True),indent=2)+'\n')
 print('PASS',len(results),'actual ARM64 cases')
if __name__=='__main__':main()
