# Code discovery for the Mega Drive ROM: recursive descent + jump-table heuristics
import struct, sys, os
from m68kdec import Decoder, Illegal

HERE = os.path.dirname(os.path.abspath(__file__))


def load_rom(path):
    return open(path, 'rb').read()


class Analysis:
    def __init__(self, rom):
        self.rom = rom
        self.dec = Decoder(rom)
        self.ins = {}          # addr -> Ins
        self.entries = set()   # block entry addresses
        self.funcs = set()     # call targets
        self.indirect = []     # (ins) indirect jumps/calls
        self.bad = {}          # addr -> reason

    def in_rom(self, a):
        return 0x200 <= a < len(self.rom) and not a & 1

    def explore(self, seeds):
        work = [s for s in seeds if self.in_rom(s)]
        for s in work: self.entries.add(s)
        while work:
            a = work.pop()
            while True:
                if a in self.ins:
                    break
                if not self.in_rom(a):
                    self.bad[a] = 'outside'; break
                try:
                    i = self.dec.decode(a)
                except Illegal as e:
                    self.bad[a] = str(e); break
                self.ins[a] = i
                nxt = a + i.len
                f = i.flow
                if i.target is not None and f in ('jmp', 'jsr', 'cond'):
                    t = i.target
                    if self.in_rom(t):
                        if t not in self.entries:
                            self.entries.add(t); work.append(t)
                        if f == 'jsr': self.funcs.add(t)
                    else:
                        self.bad[t] = 'target outside from %x' % a
                if f in ('jmp', 'jsr') and i.target is None:
                    self.indirect.append(i)
                if f in ('next',) and i.name in ('divu', 'divs', 'chk', 'trapv', 'stop'):
                    # end block here: resumable point
                    if nxt not in self.entries:
                        self.entries.add(nxt)
                if f in ('cond', 'jsr', 'trap'):
                    if nxt not in self.entries:
                        self.entries.add(nxt); work.append(nxt)
                    break
                if f in ('jmp', 'rts', 'rte', 'rtr', 'illegal'):
                    break
                a = nxt


def vectors(rom):
    return [struct.unpack('>I', rom[i:i + 4])[0] for i in range(0, 0x100, 4)]


if __name__ == '__main__':
    rom = load_rom(sys.argv[1])
    an = Analysis(rom)
    v = vectors(rom)
    an.explore(v[1:])
    print('instructions', len(an.ins), 'entries', len(an.entries), 'funcs', len(an.funcs))
    print('indirect', len(an.indirect))
    for i in an.indirect[:80]:
        print('  ', i)
    print('bad', len(an.bad))
    for k in list(an.bad)[:30]: print('  %x %s' % (k, an.bad[k]))
    hi = max(an.ins)
    print('max code addr %x' % hi)


def code_ptrs_in_ins(i):
    out = []
    for e in (i.src, i.dst):
        if e is None: continue
        if e.m == 'imm' and i.sz == 'l': out.append(e.v)
        if e.m in ('absl', 'absw', 'pcd') and i.name in ('lea', 'pea', 'jsr', 'jmp', 'move', 'movea'):
            out.append(e.v)
    return out


def validate(an, a, maxn=200):
    """Linear decode from a; accept if it reaches a terminator/known code without illegal ops."""
    starts = an.ins
    n = 0
    seen = []
    while n < maxn:
        if a in starts: return True
        if not an.in_rom(a): return False
        if a in an.inside: return False
        try:
            i = an.dec.decode(a)
        except Illegal:
            return False
        if i.flow == 'illegal': return False
        if i.target is not None and not an.in_rom(i.target): return False
        if i.flow in ('rts', 'rte', 'jmp', 'rtr'): return True
        n += 1
        a += i.len
    return False


def mark_inside(an):
    an.inside = set()
    for a, i in an.ins.items():
        for k in range(a + 2, a + i.len, 2): an.inside.add(k)


def jump_tables(an):
    """Find tables of bra/jmp instructions used by computed jumps:
       jmp d(An,Xn) / jmp d(pc,Xn) / jsr (An) / jmp (An) preceded by lea const,An."""
    seeds = []
    for i in list(an.indirect):
        e = i.src
        if e is None or e.m not in ('idx', 'pcidx', 'ind'):
            continue
        base = e.v if e.m == 'pcidx' else None
        if base is None:
            # walk back linearly up to 12 instructions looking for lea const,An
            a = i.addr
            for back in range(2, 40, 2):
                p = an.ins.get(i.addr - back)
                if p and p.name == 'lea' and p.dst.r == e.r and p.src.m in ('absl', 'absw', 'pcd') and p.addr + p.len <= i.addr:
                    base = p.src.v + (e.v if e.m == 'idx' else 0); break
                if p and p.flow != 'next': break
        if base is None: continue
        a = base
        n = 0
        while an.in_rom(a) and n < 256:
            try: t = an.dec.decode(a)
            except Illegal: break
            if t.name in ('bra', 'jmp') and t.target is not None:
                seeds.append(a); seeds.append(t.target); a += t.len; n += 1
            else: break
    return seeds


def full_discovery(an, seeds, ptr_scan=True, extra=()):
    an.explore(list(seeds) + list(extra))
    rounds = 0
    while True:
        rounds += 1
        mark_inside(an)
        cand = set()
        for i in an.ins.values():
            for p in code_ptrs_in_ins(i):
                if an.in_rom(p) and p not in an.ins: cand.add(p)
        cand.update(s for s in jump_tables(an) if s not in an.ins)
        if ptr_scan:
            rom = an.rom
            for off in range(0, len(rom) - 3, 2):
                if off in an.ins or off in an.inside: continue
                v = (rom[off] << 24) | (rom[off+1] << 16) | (rom[off+2] << 8) | rom[off+3]
                if 0x200 <= v < len(rom) and not v & 1 and v not in an.ins:
                    cand.add(v)
            # code following an unconditional terminator is likely another function
            for a, i in list(an.ins.items()):
                if i.flow in ('rts', 'rte', 'jmp', 'rtr'):
                    n = a + i.len
                    if n not in an.ins and n not in an.inside: cand.add(n)
        good = [c for c in cand if validate(an, c)]
        before = len(an.ins)
        an.explore(good)
        print('round', rounds, 'cand', len(cand), 'good', len(good), 'ins', len(an.ins))
        if len(an.ins) == before or rounds > 30: break
    mark_inside(an)
