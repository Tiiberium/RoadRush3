# 68000 -> C# static recompiler code generator
from m68kdec import CC, Ins, EA

MASK = {'b': 0xFF, 'w': 0xFFFF, 'l': 0xFFFFFFFF}
SIGN = {'b': 0x80, 'w': 0x8000, 'l': 0x80000000}
BITS = {'b': 8, 'w': 16, 'l': 32}
NB = {'b': 1, 'w': 2, 'l': 4}
ALLF = frozenset('XNZVC')
NZVC = frozenset('NZVC')

# instructions that end a block even though control continues to next instruction
SOFT_END = {'divu', 'divs', 'chk', 'trapv', 'stop', 'move_to_sr', 'reset', 'move_usp'}


def is_soft_end(i):
    if i.name in SOFT_END: return True
    if i.name in ('andi', 'ori', 'eori') and i.dst is not None and i.dst.m == 'sr': return True
    return False


def h(v):
    return '0x%Xu' % (v & 0xFFFFFFFF)


def cc_expr(c):
    return ['true', 'false', '(!C && !Z)', '(C || Z)', '!C', 'C', '!Z', 'Z', '!V', 'V', '!N', 'N',
            '(N == V)', '(N != V)', '(!Z && N == V)', '(Z || N != V)'][c]


def cc_reads(c):
    return [set(), set(), {'C', 'Z'}, {'C', 'Z'}, {'C'}, {'C'}, {'Z'}, {'Z'}, {'V'}, {'V'}, {'N'}, {'N'},
            {'N', 'V'}, {'N', 'V'}, {'N', 'V', 'Z'}, {'N', 'V', 'Z'}][c]


def flag_rw(i):
    """return (read_set, write_set) for flag liveness"""
    n = i.name
    d = i.dst
    if n in ('move', 'moveq', 'and', 'or', 'eor', 'not', 'clr', 'tst', 'ext', 'swap', 'mulu', 'muls', 'tas',
             'divu', 'divs', 'rol', 'ror', 'btst_never'):
        if n in ('rol', 'ror') and i.src.m == 'D': return set(), set(NZVC)
        return set(), set(NZVC)
    if n in ('andi', 'ori', 'eori'):
        if d.m in ('ccr', 'sr'): return set(ALLF), set(ALLF)
        return set(), set(NZVC)
    if n in ('add', 'sub', 'addi', 'subi', 'neg'):
        return set(), set(ALLF)
    if n in ('addq', 'subq'):
        if d.m == 'A': return set(), set()
        return set(), set(ALLF)
    if n in ('addx', 'subx', 'negx'):
        return {'X', 'Z'}, set(ALLF)
    if n in ('cmp', 'cmpi', 'cmpa', 'cmpm'):
        return set(), set(NZVC)
    if n in ('asl', 'asr', 'lsl', 'lsr'):
        if i.src.m == 'D': return {'X'}, set(ALLF)
        return set(), set(ALLF)
    if n in ('roxl', 'roxr'):
        return {'X'}, set(ALLF)
    if n in ('btst', 'bchg', 'bclr', 'bset'):
        return set(), {'Z'}
    if n in ('bcc', 'scc', 'dbcc'):
        return cc_reads(i.cc), set()
    if n in ('move_to_ccr', 'move_to_sr', 'rte', 'rtr'):
        return set(), set(ALLF)
    if n in ('move_from_sr',):
        return set(ALLF), set()
    if n in ('abcd', 'sbcd', 'nbcd'):
        return {'X', 'Z'}, set(ALLF)
    if n in ('chk', 'trapv', 'trap', 'illegal', 'linea', 'linef', 'stop'):
        return set(ALLF), set(ALLF)
    return set(), set()


def cycles(i):
    def eac(e, sz):
        if e is None: return 0
        l = sz == 'l'
        return {'D': 0, 'A': 0, 'ind': 4, 'pi': 4, 'pd': 6, 'd16': 8, 'idx': 10, 'absw': 8, 'absl': 12,
                'pcd': 8, 'pcidx': 10, 'imm': 4}.get(e.m, 0) + (4 if l and e.m not in ('D', 'A') else 0)
    n = i.name; sz = i.sz or 'w'
    c = 4 + eac(i.src, sz) + eac(i.dst, sz)
    if n in ('mulu', 'muls'): c += 66
    elif n in ('divu', 'divs'): c += 136
    elif n in ('movem',):
        c = 12 + bin(i.extra[0]).count('1') * (8 if sz == 'l' else 4)
    elif n in ('jsr', 'bsr'): c = 18
    elif n in ('rts',): c = 16
    elif n in ('rte',): c = 20
    elif n in ('bra', 'bcc', 'jmp'): c = 10
    elif n == 'dbcc': c = 10
    elif n in ('asl', 'asr', 'lsl', 'lsr', 'rol', 'ror', 'roxl', 'roxr'):
        c = 6 + (2 * i.src.v if i.src.m == 'imm' and i.dst.m == 'D' else 4) + (2 if sz == 'l' else 0)
    elif n in ('lea',): c = 4 + eac(i.src, 'w') - 4 if i.src.m != 'ind' else 4
    elif n in ('moveq',): c = 4
    if sz == 'l' and n in ('add', 'sub', 'and', 'or', 'cmp', 'adda', 'suba', 'addq', 'subq') and i.dst.m in ('D', 'A'):
        c += 4
    return max(c, 4)


class Gen:
    def __init__(self, rom, romlen):
        self.rom = rom
        self.romlen = romlen
        self.L = []
        self.t = 0

    def tmp(self):
        self.t += 1
        return 't%d' % self.t

    def emit(self, s):
        self.L.append('        ' + s)

    # ---------- constant ROM reads ----------
    def romconst(self, a, sz):
        a &= 0xFFFFFF
        n = NB[sz]
        if a + n <= self.romlen and (sz == 'b' or not a & 1):
            return int.from_bytes(self.rom[a:a + n], 'big')
        return None

    # ---------- effective addresses ----------
    def xreg(self, e):
        k, n = e.x
        r = '%s%d' % (k, n)
        if e.xl: return r
        return '(uint)(short)%s' % r

    def addr_of(self, e, sz):
        """Return C# expression for memory address of e; may emit side effects (pi/pd). Returns (expr, const or None)."""
        m = e.m
        n = NB[sz] if sz else 4
        if m == 'ind': return 'A%d' % e.r, None
        if m == 'pi':
            inc = 2 if (n == 1 and e.r == 7) else n
            t = self.tmp(); self.emit('uint %s = A%d; A%d += %d;' % (t, e.r, e.r, inc)); return t, None
        if m == 'pd':
            dec = 2 if (n == 1 and e.r == 7) else n
            t = self.tmp(); self.emit('A%d -= %d; uint %s = A%d;' % (e.r, dec, t, e.r)); return t, None
        if m == 'd16':
            v = e.v
            return ('(A%d %s %d)' % (e.r, '+' if v >= 0 else '-', abs(v))), None
        if m == 'idx':
            v = e.v
            s = '(A%d + %s' % (e.r, self.xreg(e))
            if v: s += ' %s %d' % ('+' if v >= 0 else '-', abs(v))
            return s + ')', None
        if m in ('absw', 'absl', 'pcd'):
            return h(e.v), e.v
        if m == 'pcidx':
            return '(%s + %s)' % (h(e.v), self.xreg(e)), None
        raise ValueError('addr_of %s' % m)

    def loc(self, e, sz):
        """Resolve operand to a location tuple, emitting side effects once."""
        if e.m == 'D': return ('D', e.r)
        if e.m == 'A': return ('A', e.r)
        if e.m == 'imm': return ('imm', e.v)
        a, c = self.addr_of(e, sz)
        return ('mem', a, c, e.m in ('pcd', 'pcidx'))

    def rd(self, l, sz):
        k = l[0]
        if k == 'D':
            if sz == 'l': return 'D%d' % l[1]
            return '(D%d & %s)' % (l[1], h(MASK[sz]))
        if k == 'A':
            if sz == 'l': return 'A%d' % l[1]
            return '(A%d & %s)' % (l[1], h(MASK[sz]))
        if k == 'imm':
            return h(l[1] & MASK[sz])
        if k == 'mem':
            if l[2] is not None:
                c = self.romconst(l[2], sz)
                if c is not None: return h(c)
            return 'R%d(%s)' % (BITS[sz], l[1])
        raise ValueError(k)

    def rdv(self, l, sz):
        """read into a temp var"""
        e = self.rd(l, sz)
        if l[0] in ('imm',) or (l[0] == 'mem' and e.startswith('0x')): return e
        t = self.tmp(); self.emit('uint %s = %s;' % (t, e)); return t

    def wr(self, l, sz, v):
        k = l[0]
        if k == 'D':
            if sz == 'l': self.emit('D%d = %s;' % (l[1], v))
            else: self.emit('D%d = (D%d & %s) | %s;' % (l[1], l[1], h(~MASK[sz]), v))
        elif k == 'A':
            self.emit('A%d = %s;' % (l[1], v))
        elif k == 'mem':
            self.emit('W%d(%s, %s);' % (BITS[sz], l[1], v))
        else:
            raise ValueError('write to %s' % k)

    # ---------- flags ----------
    def nz(self, r, sz, need):
        if 'N' in need: self.emit('N = (%s & %s) != 0;' % (r, h(SIGN[sz])))
        if 'Z' in need: self.emit('Z = %s == 0;' % r)

    def logic_flags(self, r, sz, need):
        self.nz(r, sz, need)
        if 'V' in need: self.emit('V = false;')
        if 'C' in need: self.emit('C = false;')

    def add_flags(self, a, b, r, sz, need, x=True, zsticky=False):
        sg = h(SIGN[sz])
        if 'N' in need: self.emit('N = (%s & %s) != 0;' % (r, sg))
        if 'Z' in need:
            if zsticky: self.emit('if (%s != 0) Z = false;' % r)
            else: self.emit('Z = %s == 0;' % r)
        if 'V' in need: self.emit('V = ((%s ^ %s) & (%s ^ %s) & %s) != 0;' % (a, r, b, r, sg))
        if 'C' in need or ('X' in need and x):
            self.emit('C = (((%s & %s) | (~%s & (%s | %s))) & %s) != 0;' % (a, b, r, a, b, sg))
        if x and 'X' in need: self.emit('X = C;')

    def sub_flags(self, s, d, r, sz, need, x=True, zsticky=False):
        sg = h(SIGN[sz])
        if 'N' in need: self.emit('N = (%s & %s) != 0;' % (r, sg))
        if 'Z' in need:
            if zsticky: self.emit('if (%s != 0) Z = false;' % r)
            else: self.emit('Z = %s == 0;' % r)
        if 'V' in need: self.emit('V = ((%s ^ %s) & (%s ^ %s) & %s) != 0;' % (s, d, r, d, sg))
        if 'C' in need or ('X' in need and x):
            self.emit('C = (((%s & ~%s) | (%s & ~%s) | (%s & %s)) & %s) != 0;' % (s, d, r, d, s, r, sg))
        if x and 'X' in need: self.emit('X = C;')

    def m(self, expr, sz):
        if sz == 'l': return expr
        return '((%s) & %s)' % (expr, h(MASK[sz]))

    # ---------- instruction ----------
    def gen(self, i, need):
        """Generate code for instruction i. need = set of flags that must be computed.
        Returns None for fallthrough, or a C# expression string for the 'return' (block end)."""
        n = i.name; sz = i.sz; s = i.src; d = i.dst
        nxt = (i.addr + i.len) & 0xFFFFFFFF
        E = self.emit
        if n == 'move':
            ls = self.loc(s, sz); v = self.rdv(ls, sz)
            ld = self.loc(d, sz); self.wr(ld, sz, v)
            self.logic_flags(v, sz, need); return
        if n == 'moveq':
            E('D%d = %s;' % (d.r, h(s.v))); self.logic_flags(h(s.v), 'l', need); return
        if n == 'movea':
            ls = self.loc(s, sz); v = self.rd(ls, sz)
            if sz == 'w': v = '(uint)(short)%s' % v
            E('A%d = %s;' % (d.r, v)); return
        if n == 'lea':
            a, c = self.addr_of(s, 'l'); E('A%d = %s;' % (d.r, a)); return
        if n == 'pea':
            a, c = self.addr_of(s, 'l'); t = self.tmp(); E('uint %s = %s;' % (t, a)); E('A7 -= 4; W32(A7, %s);' % t); return
        if n in ('add', 'addi', 'addq', 'sub', 'subi', 'subq'):
            add = n.startswith('add')
            if d.m == 'A':  # addq/subq to An: full 32-bit, no flags
                E('A%d %s= %s;' % (d.r, '+' if add else '-', h(s.v))); return
            ls = self.loc(s, sz); a = self.rdv(ls, sz)
            ld = self.loc(d, sz); b = self.rdv(ld, sz)
            r = self.tmp()
            E('uint %s = %s;' % (r, self.m('%s %s %s' % (b, '+' if add else '-', a), sz)))
            self.wr(ld, sz, r)
            if add: self.add_flags(a, b, r, sz, need)
            else: self.sub_flags(a, b, r, sz, need)
            return
        if n in ('adda', 'suba'):
            ls = self.loc(s, sz); v = self.rd(ls, sz)
            if sz == 'w': v = '(uint)(short)%s' % v
            E('A%d %s= %s;' % (d.r, '+' if n == 'adda' else '-', v)); return
        if n in ('cmp', 'cmpi', 'cmpm', 'cmpa'):
            ls = self.loc(s, sz); a = self.rdv(ls, sz)
            ld = self.loc(d, sz)
            if n == 'cmpa':
                if sz == 'w':
                    t = self.tmp(); E('uint %s = (uint)(short)%s;' % (t, a)); a = t
                b = self.rdv(ld, 'l'); sz = 'l'
            else:
                b = self.rdv(ld, sz)
            if not need: return
            r = self.tmp(); E('uint %s = %s;' % (r, self.m('%s - %s' % (b, a), sz)))
            self.sub_flags(a, b, r, sz, need, x=False); return
        if n in ('addx', 'subx'):
            ls = self.loc(s, sz); a = self.rdv(ls, sz)
            ld = self.loc(d, sz); b = self.rdv(ld, sz)
            r = self.tmp()
            op = '+' if n == 'addx' else '-'
            E('uint %s = %s;' % (r, self.m('%s %s %s %s (X ? 1u : 0u)' % (b, op, a, op), sz)))
            self.wr(ld, sz, r)
            if n == 'addx': self.add_flags(a, b, r, sz, need | {'X'}, zsticky=True)
            else: self.sub_flags(a, b, r, sz, need | {'X'}, zsticky=True)
            return
        if n in ('neg', 'negx'):
            ld = self.loc(d, sz); b = self.rdv(ld, sz); r = self.tmp()
            if n == 'neg':
                E('uint %s = %s;' % (r, self.m('0u - %s' % b, sz)))
                self.wr(ld, sz, r); self.sub_flags(b, '0u', r, sz, need)
            else:
                E('uint %s = %s;' % (r, self.m('0u - %s - (X ? 1u : 0u)' % b, sz)))
                self.wr(ld, sz, r); self.sub_flags(b, '0u', r, sz, need | {'X'}, zsticky=True)
            return
        if n in ('and', 'or', 'eor', 'andi', 'ori', 'eori'):
            op = {'a': '&', 'o': '|', 'e': '^'}[n[0]]
            if d.m == 'ccr':
                E('SetCCR(GetCCR() %s %s);' % (op, h(s.v & 0xFF))); return
            if d.m == 'sr':
                E('SetSR(GetSR() %s %s);' % (op, h(s.v & 0xFFFF))); return nxt_ret(nxt)
            ls = self.loc(s, sz); a = self.rdv(ls, sz)
            ld = self.loc(d, sz); b = self.rdv(ld, sz)
            r = self.tmp(); E('uint %s = %s %s %s;' % (r, b, op, a))
            self.wr(ld, sz, r); self.logic_flags(r, sz, need); return
        if n == 'not':
            ld = self.loc(d, sz); b = self.rdv(ld, sz); r = self.tmp()
            E('uint %s = %s;' % (r, self.m('~%s' % b, sz)))
            self.wr(ld, sz, r); self.logic_flags(r, sz, need); return
        if n == 'clr':
            ld = self.loc(d, sz); self.wr(ld, sz, '0u')
            if 'N' in need: E('N = false;')
            if 'Z' in need: E('Z = true;')
            if 'V' in need: E('V = false;')
            if 'C' in need: E('C = false;')
            return
        if n == 'tst':
            ld = self.loc(d, sz); v = self.rdv(ld, sz); self.logic_flags(v, sz, need); return
        if n == 'swap':
            E('D%d = (D%d >> 16) | (D%d << 16);' % (d.r, d.r, d.r)); self.logic_flags('D%d' % d.r, 'l', need); return
        if n == 'ext':
            if sz == 'w':
                E('D%d = (D%d & 0xFFFF0000u) | ((uint)(short)(sbyte)D%d & 0xFFFFu);' % (d.r, d.r, d.r))
                self.logic_flags('(D%d & 0xFFFFu)' % d.r, 'w', need)
            else:
                E('D%d = (uint)(short)D%d;' % (d.r, d.r)); self.logic_flags('D%d' % d.r, 'l', need)
            return
        if n == 'exg':
            t = self.tmp()
            a = '%s%d' % (s.m, s.r); b = '%s%d' % (d.m, d.r)
            E('uint %s = %s; %s = %s; %s = %s;' % (t, a, a, b, b, t)); return
        if n in ('mulu', 'muls'):
            ls = self.loc(s, 'w'); a = self.rdv(ls, 'w')
            if n == 'mulu': E('D%d = (D%d & 0xFFFFu) * %s;' % (d.r, d.r, a))
            else: E('D%d = (uint)((int)(short)D%d * (int)(short)%s);' % (d.r, d.r, a))
            self.logic_flags('D%d' % d.r, 'l', need); return
        if n in ('divu', 'divs'):
            ls = self.loc(s, 'w'); a = self.rdv(ls, 'w')
            E('if (%s == 0) return Exception(5, %s);' % (a, h(nxt)))
            E('%s(%d, %s);' % ('DivU' if n == 'divu' else 'DivS', d.r, a))
            return h(nxt)
        if n in ('asl', 'asr', 'lsl', 'lsr', 'rol', 'ror', 'roxl', 'roxr'):
            ld = self.loc(d, sz); v = self.rdv(ld, sz)
            if s.m == 'imm': cnt = str(s.v)
            else: cnt = '(int)(D%d & 63u)' % s.r
            r = self.tmp()
            if s.m == 'imm' and not need and n in ('lsl', 'asl', 'lsr', 'rol', 'ror'):
                c = s.v; bits = BITS[sz]
                if n in ('lsl', 'asl'): ex = self.m('%s << %d' % (v, c), sz)
                elif n == 'lsr': ex = '(%s >> %d)' % (v, c)
                elif n == 'rol': ex = self.m('(%s << %d) | (%s >> %d)' % (v, c, v, bits - c), sz)
                else: ex = self.m('(%s >> %d) | (%s << %d)' % (v, c, v, bits - c), sz)
                E('uint %s = %s;' % (r, ex))
            elif s.m == 'imm' and not need and n == 'asr':
                c = s.v
                st = {'b': '(sbyte)', 'w': '(short)', 'l': ''}[sz]
                E('uint %s = %s;' % (r, self.m('(uint)((int)%s%s >> %d)' % (st, v, c), sz)))
            else:
                E('uint %s = Sh_%s(%s, %s, %d);' % (r, n, v, cnt, BITS[sz]))
            self.wr(ld, sz, r); return
        if n in ('btst', 'bchg', 'bclr', 'bset'):
            if s.m == 'imm': bit = str(s.v & (31 if sz == 'l' else 7))
            else: bit = '(int)(D%d & %du)' % (s.r, 31 if sz == 'l' else 7)
            ld = self.loc(d, sz); v = self.rdv(ld, sz)
            E('Z = ((%s >> %s) & 1u) == 0;' % (v, bit))
            if n == 'bchg': self.wr(ld, sz, '(%s ^ (1u << %s))' % (v, bit))
            elif n == 'bclr': self.wr(ld, sz, '(%s & ~(1u << %s))' % (v, bit))
            elif n == 'bset': self.wr(ld, sz, '(%s | (1u << %s))' % (v, bit))
            return
        if n == 'scc':
            ld = self.loc(d, 'b'); self.wr(ld, 'b', '(%s ? 0xFFu : 0u)' % cc_expr(i.cc)); return
        if n == 'tas':
            ld = self.loc(d, 'b'); v = self.rdv(ld, 'b'); self.logic_flags(v, 'b', need)
            self.wr(ld, 'b', '(%s | 0x80u)' % v); return
        if n == 'chk':
            ls = self.loc(s, 'w'); a = self.rdv(ls, 'w')
            E('if ((short)D%d < 0) { N = true; return Exception(6, %s); }' % (d.r, h(nxt)))
            E('if ((short)D%d > (short)%s) { N = false; return Exception(6, %s); }' % (d.r, a, h(nxt)))
            return h(nxt)
        if n == 'link':
            E('A7 -= 4; W32(A7, A%d); A%d = A7; A7 += %s;' % (s.r, s.r, h(d.v))); return
        if n == 'unlk':
            E('A7 = A%d; A%d = R32(A7); A7 += 4;' % (s.r, s.r)); return
        if n == 'movem':
            mask, dirn = i.extra
            nb = NB[sz]
            regs = ['D%d' % k for k in range(8)] + ['A%d' % k for k in range(8)]
            if dirn == 'r2m':
                if d.m == 'pd':
                    t = self.tmp(); E('uint %s = A%d;' % (t, d.r))
                    for b in range(16):
                        if mask & (1 << b):
                            rg = regs[15 - b]
                            E('%s -= %d; W%d(%s, %s);' % (t, nb, BITS[sz], t, rg if sz == 'l' else '(%s & 0xFFFFu)' % rg))
                    E('A%d = %s;' % (d.r, t)); return
                a, c = self.addr_of(d, sz)
                t = self.tmp(); E('uint %s = %s;' % (t, a))
                for b in range(16):
                    if mask & (1 << b):
                        rg = regs[b]
                        E('W%d(%s, %s); %s += %d;' % (BITS[sz], t, rg if sz == 'l' else '(%s & 0xFFFFu)' % rg, t, nb))
                return
            else:
                if s.m == 'pi': a = 'A%d' % s.r
                else: a, c = self.addr_of(s, sz)
                t = self.tmp(); E('uint %s = %s;' % (t, a))
                for b in range(16):
                    if mask & (1 << b):
                        rg = regs[b]
                        if sz == 'l': E('%s = R32(%s); %s += 4;' % (rg, t, t))
                        else: E('%s = (uint)(short)R16(%s); %s += 2;' % (rg, t, t))
                if s.m == 'pi': E('A%d = %s;' % (s.r, t))
                return
        if n == 'movep_m2r':
            a = '(A%d %s %d)' % (s.r, '+' if s.v >= 0 else '-', abs(s.v))
            t = self.tmp(); E('uint %s = %s;' % (t, a))
            if sz == 'w':
                E('D%d = (D%d & 0xFFFF0000u) | (R8(%s) << 8) | R8(%s + 2);' % (d.r, d.r, t, t))
            else:
                E('D%d = (R8(%s) << 24) | (R8(%s + 2) << 16) | (R8(%s + 4) << 8) | R8(%s + 6);' % (d.r, t, t, t, t))
            return
        if n == 'movep_w2m':
            a = '(A%d %s %d)' % (d.r, '+' if d.v >= 0 else '-', abs(d.v))
            t = self.tmp(); E('uint %s = %s;' % (t, a))
            r = 'D%d' % s.r
            if sz == 'w':
                E('W8(%s, (%s >> 8) & 0xFFu); W8(%s + 2, %s & 0xFFu);' % (t, r, t, r))
            else:
                E('W8(%s, %s >> 24); W8(%s + 2, (%s >> 16) & 0xFFu); W8(%s + 4, (%s >> 8) & 0xFFu); W8(%s + 6, %s & 0xFFu);' % (t, r, t, r, t, r, t, r))
            return
        if n in ('abcd', 'sbcd'):
            ls = self.loc(s, 'b'); a = self.rdv(ls, 'b')
            ld = self.loc(d, 'b'); b = self.rdv(ld, 'b')
            r = self.tmp(); E('uint %s = %s(%s, %s);' % (r, 'Abcd' if n == 'abcd' else 'Sbcd', a, b))
            self.wr(ld, 'b', r); return
        if n == 'nbcd':
            ld = self.loc(d, 'b'); b = self.rdv(ld, 'b')
            r = self.tmp(); E('uint %s = Sbcd(%s, 0u);' % (r, b)); self.wr(ld, 'b', r); return
        if n == 'move_from_sr':
            ld = self.loc(d, 'w'); self.wr(ld, 'w', 'GetSR()'); return
        if n == 'move_to_ccr':
            ls = self.loc(s, 'w'); v = self.rd(ls, 'w'); E('SetCCR(%s & 0xFFu);' % v); return
        if n == 'move_to_sr':
            ls = self.loc(s, 'w'); v = self.rd(ls, 'w'); E('SetSR(%s);' % v); return h(nxt)
        if n == 'move_usp':
            if d.m == 'usp': E('USP = A%d;' % s.r)
            else: E('A%d = USP;' % d.r)
            return h(nxt)
        if n == 'nop': return
        if n == 'reset': return h(nxt)
        if n == 'stop':
            E('SetSR(%s); Stop();' % h(s.v)); return h(nxt)
        if n == 'trapv':
            E('if (V) return Exception(7, %s);' % h(nxt)); return h(nxt)
        # ---- control flow ----
        if n == 'bra': return h(i.target)
        if n == 'bsr':
            E('A7 -= 4; W32(A7, %s);' % h(nxt)); return h(i.target)
        if n == 'bcc':
            return '%s ? %s : %s' % (cc_expr(i.cc), h(i.target), h(nxt))
        if n == 'dbcc':
            if i.cc != 0:
                E('if (%s) return %s;' % (cc_expr(i.cc), h(nxt)))
            t = self.tmp()
            E('uint %s = (D%d - 1u) & 0xFFFFu; D%d = (D%d & 0xFFFF0000u) | %s;' % (t, d.r, d.r, d.r, t))
            return '%s != 0xFFFFu ? %s : %s' % (t, h(i.target), h(nxt))
        if n == 'jmp':
            if i.target is not None: return h(i.target)
            a, c = self.addr_of(s, 'l'); t = self.tmp(); E('uint %s = %s;' % (t, a)); return t
        if n == 'jsr':
            if i.target is not None:
                E('A7 -= 4; W32(A7, %s);' % h(nxt)); return h(i.target)
            a, c = self.addr_of(s, 'l'); t = self.tmp(); E('uint %s = %s;' % (t, a))
            E('A7 -= 4; W32(A7, %s);' % h(nxt)); return t
        if n == 'rts':
            t = self.tmp(); E('uint %s = R32(A7); A7 += 4;' % t); return t
        if n == 'rtr':
            t = self.tmp(); E('SetCCR(R16(A7) & 0xFFu); uint %s = R32(A7 + 2); A7 += 6;' % t); return t
        if n == 'rte':
            t = self.tmp(); E('uint sr_ = R16(A7); uint %s = R32(A7 + 2); A7 += 6; SetSR(sr_);' % t); return t
        if n == 'trap':
            return 'Exception(%d, %s)' % (32 + s.v, h(nxt))
        if n == 'illegal':
            return 'Exception(4, %s)' % h(i.addr)
        if n == 'linea':
            return 'Exception(10, %s)' % h(i.addr)
        if n == 'linef':
            return 'Exception(11, %s)' % h(i.addr)
        raise ValueError('unhandled %s' % n)


def nxt_ret(a):
    return h(a)


def gen_block(g, start, ins_map, entries):
    """Generate one C# method for block at start. Returns (code_lines, list of instrs)"""
    seq = []
    a = start
    while True:
        i = ins_map.get(a)
        if i is None:
            seq.append(None); break
        seq.append(i)
        if i.flow != 'next' or is_soft_end(i): break
        a += i.len
        if a in entries:
            break
    # liveness
    need = []
    live = set(ALLF)
    for i in reversed(seq):
        if i is None:
            need.append(set()); continue
        r, w = flag_rw(i)
        need.append(live & w)
        live = (live - w) | r
    need.reverse()
    g.L = []; g.t = 0
    cyc = sum(cycles(i) for i in seq if i is not None)
    g.emit('Cyc += %d;' % cyc)
    ret = None
    for i, nd in zip(seq, need):
        if i is None:
            ret = 'Missing(%s)' % h(a); break
        g.L.append('        // %s' % repr(i).replace('\\', '/'))
        r = g.gen(i, nd)
        if r is not None:
            ret = r; break
    if ret is None:
        last = seq[-1]
        ret = h(last.addr + last.len)
    g.emit('return %s;' % ret)
    return g.L, seq
