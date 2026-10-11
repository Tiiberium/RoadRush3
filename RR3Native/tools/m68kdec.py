# Motorola 68000 instruction decoder (for static recompilation of Mega Drive ROMs)
import struct

SZ = {'b': 1, 'w': 2, 'l': 4}
CC = ['t', 'f', 'hi', 'ls', 'cc', 'cs', 'ne', 'eq', 'vc', 'vs', 'pl', 'mi', 'ge', 'lt', 'gt', 'le']


class Illegal(Exception):
    pass


class EA:
    __slots__ = ('m', 'r', 'v', 'x', 'xl', 'pc')

    def __init__(self, m, r=0, v=0, x=None, xl=False):
        self.m = m      # D A ind pi pd d16 idx absw absl pcd pcidx imm ccr sr usp
        self.r = r      # register number
        self.v = v      # displacement / address / immediate
        self.x = x      # index register: ('D'|'A', n)
        self.xl = xl    # index is long

    def __repr__(self):
        m = self.m
        if m == 'D': return 'd%d' % self.r
        if m == 'A': return 'a%d' % self.r
        if m == 'ind': return '(a%d)' % self.r
        if m == 'pi': return '(a%d)+' % self.r
        if m == 'pd': return '-(a%d)' % self.r
        if m == 'd16': return '%d(a%d)' % (self.v, self.r)
        if m == 'idx': return '%d(a%d,%s%d.%s)' % (self.v, self.r, self.x[0].lower(), self.x[1], 'l' if self.xl else 'w')
        if m == 'absw': return '$%x.w' % self.v
        if m == 'absl': return '$%x.l' % self.v
        if m == 'pcd': return '$%x(pc)' % self.v
        if m == 'pcidx': return '$%x(pc,%s%d.%s)' % (self.v, self.x[0].lower(), self.x[1], 'l' if self.xl else 'w')
        if m == 'imm': return '#$%x' % self.v
        return m


class Ins:
    __slots__ = ('addr', 'name', 'sz', 'src', 'dst', 'len', 'flow', 'target', 'cc', 'extra', 'opc')

    def __init__(self, addr, name, sz=None, src=None, dst=None):
        self.addr = addr; self.name = name; self.sz = sz; self.src = src; self.dst = dst
        self.len = 0; self.flow = None; self.target = None; self.cc = None; self.extra = None; self.opc = 0

    def __repr__(self):
        n = self.name.replace('cc', CC[self.cc]) if self.cc is not None else self.name
        n = n + (('.' + self.sz) if self.sz else '')
        ops = []
        if self.extra is not None and self.name in ('movem',):
            ops.append('#$%04x' % self.extra[0])
        if self.src is not None: ops.append(repr(self.src))
        if self.dst is not None: ops.append(repr(self.dst))
        if self.target is not None and self.src is None and self.dst is None: ops.append('$%x' % self.target)
        elif self.target is not None: ops.append('$%x' % self.target)
        return '%06x  %-8s %s' % (self.addr, n, ','.join(ops))


def s8(v): return v - 0x100 if v & 0x80 else v
def s16(v): return v - 0x10000 if v & 0x8000 else v
def s32(v): return v - 0x100000000 if v & 0x80000000 else v


class Decoder:
    def __init__(self, rom):
        self.rom = rom

    def w(self, a):
        if a + 2 > len(self.rom) or a < 0:
            raise Illegal('out of rom %x' % a)
        return (self.rom[a] << 8) | self.rom[a + 1]

    def l(self, a):
        return (self.w(a) << 16) | self.w(a + 2)

    def decode(self, addr):
        if addr & 1:
            raise Illegal('odd')
        self.p = addr + 2
        op = self.w(addr)
        ins = self._dec(addr, op)
        ins.opc = op
        ins.len = self.p - addr
        if ins.flow is None:
            ins.flow = 'next'
        return ins

    def ext(self):
        v = self.w(self.p); self.p += 2; return v

    def extl(self):
        v = self.l(self.p); self.p += 4; return v

    def ea(self, mode, reg, sz, allow=None):
        if mode == 0: e = EA('D', reg)
        elif mode == 1: e = EA('A', reg)
        elif mode == 2: e = EA('ind', reg)
        elif mode == 3: e = EA('pi', reg)
        elif mode == 4: e = EA('pd', reg)
        elif mode == 5: e = EA('d16', reg, s16(self.ext()))
        elif mode == 6:
            x = self.ext()
            if x & 0x0700: raise Illegal('full ext')
            e = EA('idx', reg, s8(x & 0xff), ('A' if x & 0x8000 else 'D', (x >> 12) & 7), bool(x & 0x800))
        elif mode == 7:
            if reg == 0: e = EA('absw', 0, s16(self.ext()) & 0xffffffff)
            elif reg == 1: e = EA('absl', 0, self.extl())
            elif reg == 2:
                b = self.p; e = EA('pcd', 0, (b + s16(self.ext())) & 0xffffffff)
            elif reg == 3:
                b = self.p; x = self.ext()
                if x & 0x0700: raise Illegal('full ext')
                e = EA('pcidx', 0, (b + s8(x & 0xff)) & 0xffffffff, ('A' if x & 0x8000 else 'D', (x >> 12) & 7), bool(x & 0x800))
            elif reg == 4:
                if sz == 'l': e = EA('imm', 0, self.extl())
                elif sz == 'b': e = EA('imm', 0, self.ext() & 0xff)
                else: e = EA('imm', 0, self.ext())
            else:
                raise Illegal('bad ea')
        if allow is not None and e.m not in allow:
            raise Illegal('ea not allowed %s' % e.m)
        return e

    # addressing-mode groups
    DATA = ('D', 'ind', 'pi', 'pd', 'd16', 'idx', 'absw', 'absl', 'pcd', 'pcidx', 'imm')
    ALL = ('D', 'A', 'ind', 'pi', 'pd', 'd16', 'idx', 'absw', 'absl', 'pcd', 'pcidx', 'imm')
    DALT = ('D', 'ind', 'pi', 'pd', 'd16', 'idx', 'absw', 'absl')
    MALT = ('ind', 'pi', 'pd', 'd16', 'idx', 'absw', 'absl')
    ALT = ('D', 'A', 'ind', 'pi', 'pd', 'd16', 'idx', 'absw', 'absl')
    CTRL = ('ind', 'd16', 'idx', 'absw', 'absl', 'pcd', 'pcidx')

    def _dec(self, a, op):
        hi = op >> 12
        mode = (op >> 3) & 7; reg = op & 7
        r9 = (op >> 9) & 7; m6 = (op >> 6) & 7
        szb = (op >> 6) & 3
        I = lambda n, sz=None, s=None, d=None: Ins(a, n, sz, s, d)
        if hi == 0:
            if op & 0x100:
                if mode == 1:  # movep
                    d16 = s16(self.ext())
                    sz = 'l' if op & 0x40 else 'w'
                    if op & 0x80:
                        return I('movep_w2m', sz, EA('D', r9), EA('d16', reg, d16))
                    return I('movep_m2r', sz, EA('d16', reg, d16), EA('D', r9))
                n = ['btst', 'bchg', 'bclr', 'bset'][szb]
                d = self.ea(mode, reg, 'b', self.DATA if n == 'btst' else self.DALT)
                return I(n, 'l' if d.m == 'D' else 'b', EA('D', r9), d)
            if r9 == 4:  # static bit
                n = ['btst', 'bchg', 'bclr', 'bset'][szb]
                bit = self.ext() & 0xff
                d = self.ea(mode, reg, 'b', ('D', 'ind', 'pi', 'pd', 'd16', 'idx', 'absw', 'absl', 'pcd', 'pcidx') if n == 'btst' else self.DALT)
                return I(n, 'l' if d.m == 'D' else 'b', EA('imm', 0, bit), d)
            names = {0: 'ori', 1: 'andi', 2: 'subi', 3: 'addi', 5: 'eori', 6: 'cmpi'}
            if r9 not in names or szb == 3: raise Illegal('op0')
            n = names[r9]; sz = 'bwl'[szb]
            if op & 0x3f == 0x3c and n in ('ori', 'andi', 'eori'):
                if sz == 'b': return I(n, 'b', EA('imm', 0, self.ext() & 0xff), EA('ccr'))
                if sz == 'w':
                    i = I(n, 'w', EA('imm', 0, self.ext()), EA('sr')); return i
            s = self.ea(7, 4, sz)
            d = self.ea(mode, reg, sz, self.DALT if n != 'cmpi' else ('D', 'ind', 'pi', 'pd', 'd16', 'idx', 'absw', 'absl', 'pcd', 'pcidx'))
            return I(n, sz, s, d)
        if hi in (1, 2, 3):
            sz = {1: 'b', 3: 'w', 2: 'l'}[hi]
            s = self.ea(mode, reg, sz)
            if sz == 'b' and s.m == 'A': raise Illegal('move.b an')
            dm = m6
            if dm == 1:
                if sz == 'b': raise Illegal('movea.b')
                return I('movea', sz, s, EA('A', r9))
            d = self.ea(dm, r9, sz, self.DALT)
            return I('move', sz, s, d)
        if hi == 4:
            if op == 0x4e70: return I('reset')
            if op == 0x4e71: return I('nop')
            if op == 0x4e72:
                i = I('stop', None, EA('imm', 0, self.ext())); return i
            if op == 0x4e73:
                i = I('rte'); i.flow = 'rte'; return i
            if op == 0x4e75:
                i = I('rts'); i.flow = 'rts'; return i
            if op == 0x4e76:
                i = I('trapv'); return i
            if op == 0x4e77:
                i = I('rtr'); i.flow = 'rtr'; return i
            if op == 0x4afc:
                i = I('illegal'); i.flow = 'illegal'; return i
            if op & 0xfff0 == 0x4e40:
                i = I('trap', None, EA('imm', 0, op & 15)); i.flow = 'trap'; return i
            if op & 0xfff8 == 0x4e50:
                return I('link', None, EA('A', reg), EA('imm', 0, s16(self.ext()) & 0xffffffff))
            if op & 0xfff8 == 0x4e58:
                return I('unlk', None, EA('A', reg))
            if op & 0xfff8 == 0x4e60: return I('move_usp', 'l', EA('A', reg), EA('usp'))
            if op & 0xfff8 == 0x4e68: return I('move_usp', 'l', EA('usp'), EA('A', reg))
            if op & 0xffc0 == 0x4e80:
                d = self.ea(mode, reg, 'l', self.CTRL)
                i = I('jsr', None, d); i.flow = 'jsr'
                if d.m in ('absw', 'absl', 'pcd'): i.target = d.v
                return i
            if op & 0xffc0 == 0x4ec0:
                d = self.ea(mode, reg, 'l', self.CTRL)
                i = I('jmp', None, d); i.flow = 'jmp'
                if d.m in ('absw', 'absl', 'pcd'): i.target = d.v
                return i
            if op & 0x1c0 == 0x1c0:
                return I('lea', 'l', self.ea(mode, reg, 'l', self.CTRL), EA('A', r9))
            if op & 0x1c0 == 0x180:
                return I('chk', 'w', self.ea(mode, reg, 'w', self.DATA), EA('D', r9))
            if op & 0xffc0 == 0x40c0:
                return I('move_from_sr', 'w', EA('sr'), self.ea(mode, reg, 'w', self.DALT))
            if op & 0xffc0 == 0x44c0:
                return I('move_to_ccr', 'w', self.ea(mode, reg, 'w', self.DATA), EA('ccr'))
            if op & 0xffc0 == 0x46c0:
                return I('move_to_sr', 'w', self.ea(mode, reg, 'w', self.DATA), EA('sr'))
            if op & 0xffc0 == 0x4800:
                return I('nbcd', 'b', None, self.ea(mode, reg, 'b', self.DALT))
            if op & 0xfff8 == 0x4840: return I('swap', 'w', None, EA('D', reg))
            if op & 0xffc0 == 0x4840: return I('pea', 'l', self.ea(mode, reg, 'l', self.CTRL))
            if op & 0xfff8 == 0x4880: return I('ext', 'w', None, EA('D', reg))
            if op & 0xfff8 == 0x48c0: return I('ext', 'l', None, EA('D', reg))
            if op & 0xfb80 == 0x4880:
                mask = self.ext()
                sz = 'l' if op & 0x40 else 'w'
                if op & 0x400:
                    d = self.ea(mode, reg, sz, ('ind', 'pi', 'd16', 'idx', 'absw', 'absl', 'pcd', 'pcidx'))
                    i = I('movem', sz, d, None); i.extra = (mask, 'm2r')
                else:
                    d = self.ea(mode, reg, sz, ('ind', 'pd', 'd16', 'idx', 'absw', 'absl'))
                    i = I('movem', sz, None, d); i.extra = (mask, 'r2m')
                return i
            if op & 0xffc0 == 0x4ac0:
                return I('tas', 'b', None, self.ea(mode, reg, 'b', self.DALT))
            if op & 0xff00 in (0x4000, 0x4200, 0x4400, 0x4600, 0x4a00) and szb != 3:
                n = {0x4000: 'negx', 0x4200: 'clr', 0x4400: 'neg', 0x4600: 'not', 0x4a00: 'tst'}[op & 0xff00]
                sz = 'bwl'[szb]
                d = self.ea(mode, reg, sz, self.DALT if n != 'tst' else ('D', 'ind', 'pi', 'pd', 'd16', 'idx', 'absw', 'absl', 'pcd', 'pcidx', 'imm', 'A'))
                return I(n, sz, None, d)
            raise Illegal('op4 %04x' % op)
        if hi == 5:
            if szb == 3:
                c = (op >> 8) & 15
                if mode == 1:
                    disp = s16(self.ext())
                    i = I('dbcc', 'w', None, EA('D', reg)); i.cc = c
                    i.target = (a + 2 + disp) & 0xffffffff; i.flow = 'cond'
                    return i
                i = I('scc', 'b', None, self.ea(mode, reg, 'b', self.DALT)); i.cc = c
                return i
            q = r9 or 8
            sz = 'bwl'[szb]
            d = self.ea(mode, reg, sz, self.ALT)
            if d.m == 'A' and sz == 'b': raise Illegal('addq.b an')
            return I('subq' if op & 0x100 else 'addq', sz, EA('imm', 0, q), d)
        if hi == 6:
            c = (op >> 8) & 15
            d8 = op & 0xff
            if d8 == 0: disp = s16(self.ext())
            elif d8 == 0xff: raise Illegal('bcc.l')
            else: disp = s8(d8)
            t = (a + 2 + disp) & 0xffffffff
            if c == 0:
                i = I('bra'); i.target = t; i.flow = 'jmp'
            elif c == 1:
                i = I('bsr'); i.target = t; i.flow = 'jsr'
            else:
                i = I('bcc'); i.cc = c; i.target = t; i.flow = 'cond'
            return i
        if hi == 7:
            if op & 0x100: raise Illegal('moveq')
            return I('moveq', 'l', EA('imm', 0, s8(op & 0xff) & 0xffffffff), EA('D', r9))
        if hi in (8, 0xc):
            if m6 == 3 or m6 == 7:
                n = {8: ('divu', 'divs'), 0xc: ('mulu', 'muls')}[hi][m6 == 7]
                return I(n, 'w', self.ea(mode, reg, 'w', self.DATA), EA('D', r9))
            if op & 0x1f0 == 0x100:
                n = 'sbcd' if hi == 8 else 'abcd'
                if op & 8: return I(n, 'b', EA('pd', reg), EA('pd', r9))
                return I(n, 'b', EA('D', reg), EA('D', r9))
            if hi == 0xc and op & 0x130 == 0x100:
                om = op & 0xf8
                if om == 0x40: return I('exg', 'l', EA('D', r9), EA('D', reg))
                if om == 0x48: return I('exg', 'l', EA('A', r9), EA('A', reg))
                if om == 0x88: return I('exg', 'l', EA('D', r9), EA('A', reg))
                raise Illegal('exg')
            n = 'or' if hi == 8 else 'and'
            sz = 'bwl'[szb]
            if op & 0x100:
                return I(n, sz, EA('D', r9), self.ea(mode, reg, sz, self.MALT))
            return I(n, sz, self.ea(mode, reg, sz, self.DATA), EA('D', r9))
        if hi in (9, 0xd):
            n = 'sub' if hi == 9 else 'add'
            if szb == 3:
                sz = 'l' if op & 0x100 else 'w'
                return I(n + 'a', sz, self.ea(mode, reg, sz, self.ALL), EA('A', r9))
            sz = 'bwl'[szb]
            if op & 0x130 == 0x100:
                if op & 8: return I(n + 'x', sz, EA('pd', reg), EA('pd', r9))
                return I(n + 'x', sz, EA('D', reg), EA('D', r9))
            if op & 0x100:
                return I(n, sz, EA('D', r9), self.ea(mode, reg, sz, self.MALT))
            s = self.ea(mode, reg, sz, self.ALL)
            if s.m == 'A' and sz == 'b': raise Illegal('an.b')
            return I(n, sz, s, EA('D', r9))
        if hi == 0xb:
            if szb == 3:
                sz = 'l' if op & 0x100 else 'w'
                return I('cmpa', sz, self.ea(mode, reg, sz, self.ALL), EA('A', r9))
            sz = 'bwl'[szb]
            if op & 0x100:
                if mode == 1:
                    return I('cmpm', sz, EA('pi', reg), EA('pi', r9))
                return I('eor', sz, EA('D', r9), self.ea(mode, reg, sz, self.DALT))
            s = self.ea(mode, reg, sz, self.ALL)
            if s.m == 'A' and sz == 'b': raise Illegal('an.b')
            return I('cmp', sz, s, EA('D', r9))
        if hi == 0xe:
            if szb == 3:
                t = (op >> 9) & 7
                if t > 3: raise Illegal('bitfield')
                n = ['as', 'ls', 'rox', 'ro'][t] + ('l' if op & 0x100 else 'r')
                return I(n, 'w', EA('imm', 0, 1), self.ea(mode, reg, 'w', self.MALT))
            sz = 'bwl'[szb]
            t = (op >> 3) & 3
            n = ['as', 'ls', 'rox', 'ro'][t] + ('l' if op & 0x100 else 'r')
            if op & 0x20: s = EA('D', r9)
            else: s = EA('imm', 0, r9 or 8)
            return I(n, sz, s, EA('D', reg))
        if hi == 0xa:
            i = I('linea'); i.flow = 'illegal'; return i
        i = I('linef'); i.flow = 'illegal'; return i
