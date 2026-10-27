"""Host envelope -> centered BT.601 video-range NV12, without image libraries."""
import struct
from g2profile import HEADER,SIZE,require


def signed_round(n,d):return (n+d//2)//d if n>=0 else -((-n+d//2)//d)


def encode(data):
    require(len(data)>=32 and data[:8]==b'X2DLOGO3','导入的图片数据格式不符。')
    version,w,h,size,checksum,reserved=struct.unpack_from('<6I',data,8)
    require(version in (1,2) and 1<=w<=1024 and 1<=h<=768 and reserved==0,'图片尺寸或格式不符。')
    channels=4 if version==2 else 1
    pixels=data[32:]
    require(size==w*h*channels==len(pixels),'图片数据不完整。')
    fnv=2166136261
    for value in pixels:fnv=((fnv^value)*16777619)&0xffffffff
    require(fnv==checksum,'图片数据校验失败。')
    rgb=bytearray(1024*768*3);left=(1024-w)//2;top=(768-h)//2
    for y in range(h):
        for x in range(w):
            src=(y*w+x)*channels;dst=((top+y)*1024+left+x)*3
            if channels==1:rgb[dst:dst+3]=bytes([pixels[src]])*3
            else:rgb[dst:dst+3]=bytes((pixels[src+2],pixels[src+1],pixels[src]))
    out=bytearray(SIZE);out[:len(HEADER)]=HEADER;offset=len(HEADER)
    for i in range(1024*768):
        r,g,b=rgb[i*3:i*3+3]
        out[offset+i]=16+(219*(299*r+587*g+114*b)+127500)//255000
    uv=offset+1024*768
    for y in range(0,768,2):
        for x in range(0,1024,2):
            at=(y*1024+x)*3
            sums=[rgb[at+c]+rgb[at+3+c]+rgb[at+3072+c]+rgb[at+3075+c] for c in range(3)]
            r,g,b=sums
            u=128+signed_round(224*(-168736*r-331264*g+500000*b),1020000000)
            v=128+signed_round(224*(500000*r-418688*g-81312*b),1020000000)
            dst=uv+(y//2)*1024+x
            out[dst]=max(16,min(240,u));out[dst+1]=max(16,min(240,v))
    return bytes(out)
