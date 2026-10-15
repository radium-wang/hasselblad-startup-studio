"""只离线构建共用4.2.0的独立模式3适配层；默认历史灰度，--rgb 增加彩色。"""
import argparse, hashlib, io, json, struct, subprocess
from pathlib import Path
from elftools.elf.elffile import ELFFile
ROOT=Path(__file__).resolve().parents[1]
from firmware_layout import X2D_HASH, branch, CAVE_OFFSET, CAVE_VA, CAVE_LIMIT

def main():
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('original',type=Path);p.add_argument('output',type=Path);p.add_argument('--rgb',action='store_true');p.add_argument('--cc',default='clang');a=p.parse_args()
 original=a.original.read_bytes();assert hashlib.sha256(original).hexdigest()==X2D_HASH
 elf=ELFFile(io.BytesIO(original));a.output.mkdir(parents=True,exist_ok=True)
 asm=(ROOT/('native/loader-rgb.S' if a.rgb else 'native/loader.S')).read_text(encoding='utf-8')+'.byte '+','.join(map(str,range(256)))+'\n'
 (a.output/'loader.S').write_text(asm)
 compiler=[a.cc,'cc'] if Path(a.cc).stem=='zig' else [a.cc]
 subprocess.run([*compiler,'--target=aarch64-linux-android','-g0','-c','loader.S','-o','loader.o'],cwd=a.output,check=True)
 obj=ELFFile(io.BytesIO((a.output/'loader.o').read_bytes()))
 assert not any(s['sh_type']=='SHT_RELA' and s['sh_size'] for s in obj.iter_sections())
 syms={s.name:s['st_value'] for s in obj.get_section_by_name('.symtab').iter_symbols()};blob=bytearray(obj.get_section_by_name('.text').data())
 targets={'open_fn':0x7D228,'read_fn':0x7D3B8,'close_fn':0x7BC88,'malloc_fn':0x7CC18,'free_fn':0x7B498,'return_h':0x946D8,'return_earth':0x945A8,'return_draw':0x945C0,'return_finish':0x94724}
 if a.rgb:targets['memset_fn']=0x7B7F8
 for label,target in targets.items():
  at=syms[label];blob[at:at+4]=branch(CAVE_VA+at,target)
 rx=next(s for s in elf.iter_segments() if s['p_type']=='PT_LOAD');assert rx['p_flags']==5 and rx['p_filesz']==rx['p_memsz']==0x1D9684
 assert not any(original[rx['p_filesz']:CAVE_LIMIT]);assert CAVE_OFFSET+len(blob)<=CAVE_LIMIT
 patched=bytearray(original);ix=next(i for i,s in enumerate(elf.iter_segments()) if s['p_type']=='PT_LOAD');ph=elf['e_phoff']+ix*elf['e_phentsize'];parts=[(ph+32,struct.pack('<QQ',CAVE_OFFSET+len(blob),CAVE_OFFSET+len(blob))),
 (0x945A4-0x17000,branch(0x945A4,CAVE_VA+syms['entry'])),(0x946D4-0x17000,branch(0x946D4,CAVE_VA+syms['cleanup'])),(CAVE_OFFSET,blob)]
 for at,data in parts:patched[at:at+len(data)]=data
 assert len(patched)==len(original)
 for start,end in [(0x945A0,0x945A4),(0x945A8,0x946D4),(0x946D8,0x94724),(0x17AD78,0x1BB3DC)]:assert patched[start-0x17000:end-0x17000]==original[start-0x17000:end-0x17000]
 (a.output/'adapter.so').write_bytes(patched);(a.output/'patch.bin').write_bytes(b''.join(data for _,data in parts))
 result=dict(model='X2D 100C / CFV 100C',models=['X2D 100C','CFV 100C'],firmware='4.2.0',mode=3,originalSHA256=X2D_HASH,adapterSHA256=hashlib.sha256(patched).hexdigest(),
 originalSize=len(original),patches=[dict(offset=at,size=len(data)) for at,data in parts],symbols=syms,caveVA=CAVE_VA,
 factoryIdentityWrite=False,environmentWrite=False,deviceAccessed=False)
 (a.output/'adapter.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result,indent=2))
if __name__=='__main__':main()
