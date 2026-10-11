import sys
from m68kdec import Decoder, Illegal
rom=open('../rr3.bin','rb').read(); d=Decoder(rom)
a=int(sys.argv[1],16); n=int(sys.argv[2]) if len(sys.argv)>2 else 30
for _ in range(n):
    try: i=d.decode(a)
    except Illegal as e: print('%06x ILLEGAL %s'%(a,e)); a+=2; continue
    print(i); a+=i.len
