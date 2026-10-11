# Z80 decoder + C# code generator (static recompilation of the sound driver)

R8 = ['B', 'C', 'D', 'E', 'H', 'L', '(HL)', 'A']
RP = ['BC', 'DE', 'HL', 'SP']
RP2 = ['BC', 'DE', 'HL', 'AF']
CCN = ['nz', 'z', 'nc', 'c', 'po', 'pe', 'p', 'm']
CCX = ['(F & ZF) == 0', '(F & ZF) != 0', '(F & CF) == 0', '(F & CF) != 0',
       '(F & PF) == 0', '(F & PF) != 0', '(F & SF) == 0', '(F & SF) != 0']
ALU = ['Add', 'Adc', 'Sub', 'Sbc', 'And', 'Xor', 'Or', 'Cp']
ALUN = ['add a,', 'adc a,', 'sub ', 'sbc a,', 'and ', 'xor ', 'or ', 'cp ']
ROT = ['Rlc', 'Rrc', 'Rl', 'Rr', 'Sla', 'Sra', 'Sll', 'Srl']


def sx8(v): return v - 256 if v & 0x80 else v


class ZIns:
    def __init__(self, addr):
        self.addr = addr; self.len = 0; self.text = ''; self.code = []; self.flow = 'next'
        self.target = None; self.cyc = 4; self.cyc_taken = 0; self.cond = None
        self.reads_mem_code = False


class Z80Gen:
    def __init__(self, mem):
        self.mem = mem  # bytearray 64K image (RAM at 0..1FFF)

    def b(self, a): return self.mem[a & 0xFFFF]

    def w(self, a): return self.b(a) | (self.b(a + 1) << 8)

    # register accessors with index prefix
    def r8get(self, r, ix, d):
        if r == 6:
            if ix: return 'Rd((%s + %d) & 0xFFFF)' % (ix, d)
            return 'Rd(HL)'
        n = R8[r]
        if ix and n in ('H', 'L'): return '%s%s' % (ix, n)
        return n

    def r8set(self, r, ix, d, v):
        if r == 6:
            if ix: return 'Wr((%s + %d) & 0xFFFF, %s);' % (ix, d, v)
            return 'Wr(HL, %s);' % v
        n = R8[r]
        if ix and n in ('H', 'L'): return '%s%s = %s;' % (ix, n, v)
        return '%s = %s;' % (n, v)

    def rpname(self, p, ix, tbl=RP):
        n = tbl[p]
        if n == 'HL' and ix: return ix
        return n

    def decode(self, a):
        z = ZIns(a)
        p0 = a
        op = self.b(a); a += 1
        ix = None
        pre = 0
        if op in (0xDD, 0xFD):
            ix = 'IX' if op == 0xDD else 'IY'
            pre = 4
            op = self.b(a); a += 1
            if op in (0xDD, 0xFD, 0xED):
                # prefix acts as NOP
                z.len = 1; z.text = 'nop (prefix)'; z.cyc = 4; return z
        if op == 0xCB:
            if ix:
                d = sx8(self.b(a)); a += 1
                op2 = self.b(a); a += 1
                self.gen_cb(z, op2, ix, d)
                z.cyc = 20 if (op2 >> 6) == 1 else 23
            else:
                op2 = self.b(a); a += 1
                self.gen_cb(z, op2, None, 0)
                z.cyc = (12 if (op2 >> 6) == 1 else 15) if (op2 & 7) == 6 else 8
            z.len = a - p0
            return z
        if op == 0xED and not ix:
            op2 = self.b(a); a += 1
            a = self.gen_ed(z, op2, a)
            z.len = a - p0
            return z
        a = self.gen_main(z, op, a, ix, pre)
        z.len = a - p0
        return z

    def gen_cb(self, z, op, ix, d):
        x = op >> 6; y = (op >> 3) & 7; r = op & 7
        C = z.code
        if ix:
            addr = '((%s + %d) & 0xFFFF)' % (ix, d)
            src = 'Rd(%s)' % addr
            where = '(%s%+d)' % (ix.lower(), d)
        else:
            src = self.r8get(r, None, 0); where = R8[r].lower()
        if x == 0:
            z.text = '%s %s' % (ROT[y].lower(), where)
            C.append('{ int v = %s(%s);' % (ROT[y], src))
            if ix:
                C.append('Wr(%s, v);' % addr)
                if r != 6: C.append(self.r8set(r, None, 0, 'v'))
            else:
                C.append(self.r8set(r, None, 0, 'v'))
            C.append('}')
        elif x == 1:
            z.text = 'bit %d,%s' % (y, where)
            C.append('Bit(%d, %s);' % (y, src))
        else:
            z.text = '%s %d,%s' % ('res' if x == 2 else 'set', y, where)
            ex = '(%s & 0x%X)' % (src, (~(1 << y)) & 0xFF) if x == 2 else '(%s | 0x%X)' % (src, 1 << y)
            if ix:
                C.append('{ int v = %s; Wr(%s, v);' % (ex, addr))
                if r != 6: C.append(self.r8set(r, None, 0, 'v'))
                C.append('}')
            else:
                C.append(self.r8set(r, None, 0, ex))

    def gen_ed(self, z, op, a):
        x = op >> 6; y = (op >> 3) & 7; zz = op & 7; p = y >> 1; q = y & 1
        C = z.code
        if x == 1:
            if zz == 0:
                z.text = 'in %s,(c)' % (R8[y] if y != 6 else 'f'); z.cyc = 12
                C.append('{ int v = In(C); SetSZP(v);')
                if y != 6: C.append('%s = v;' % R8[y])
                C.append('}')
            elif zz == 1:
                z.text = 'out (c),%s' % (R8[y] if y != 6 else '0'); z.cyc = 12
                C.append('Out(C, %s);' % (R8[y] if y != 6 else '0'))
            elif zz == 2:
                z.text = '%s hl,%s' % ('sbc' if q == 0 else 'adc', RP[p]); z.cyc = 15
                C.append('HL = %s(HL, %s);' % ('Sbc16' if q == 0 else 'Adc16', RP[p]))
            elif zz == 3:
                nn = self.w(a); a += 2; z.cyc = 20
                if q == 0:
                    z.text = 'ld ($%04x),%s' % (nn, RP[p]); C.append('Wr16(0x%X, %s);' % (nn, RP[p]))
                else:
                    z.text = 'ld %s,($%04x)' % (RP[p], nn); C.append('%s = Rd16(0x%X);' % (RP[p], nn))
            elif zz == 4:
                z.text = 'neg'; z.cyc = 8; C.append('{ int v = A; A = 0; Sub(v); }')
            elif zz == 5:
                z.text = 'reti' if y == 1 else 'retn'; z.cyc = 14
                C.append('IFF1 = IFF2;')
                z.flow = 'ret'
            elif zz == 6:
                z.text = 'im %d' % [0, 0, 1, 2, 0, 0, 1, 2][y]; z.cyc = 8
                C.append('IM = %d;' % [0, 0, 1, 2, 0, 0, 1, 2][y])
            else:
                z.cyc = 9
                if y == 0: z.text = 'ld i,a'; C.append('I = A;')
                elif y == 1: z.text = 'ld r,a'; C.append('RReg = A;')
                elif y == 2: z.text = 'ld a,i'; C.append('A = I; F = (F & CF) | SZ(A) | (IFF2 ? PF : 0);')
                elif y == 3: z.text = 'ld a,r'; C.append('A = (RReg + (int)(Cyc & 0x7F)) & 0xFF; F = (F & CF) | SZ(A) | (IFF2 ? PF : 0);')
                elif y == 4: z.text = 'rrd'; z.cyc = 18; C.append('Rrd();')
                elif y == 5: z.text = 'rld'; z.cyc = 18; C.append('Rld();')
                else: z.text = 'nop*'; z.cyc = 8
        elif x == 2 and zz <= 3 and y >= 4:
            nm = [['ldi', 'cpi', 'ini', 'outi'], ['ldd', 'cpd', 'ind', 'outd'],
                  ['ldir', 'cpir', 'inir', 'otir'], ['lddr', 'cpdr', 'indr', 'otdr']][y - 4][zz]
            z.text = nm; z.cyc = 16
            fn = nm.capitalize()
            if y >= 6:
                # repeating: executes one iteration then loops back to itself
                base = fn[:-1] if nm.endswith('r') and nm not in ('otir', 'otdr') else fn
                base = {'Ldir': 'Ldi', 'Lddr': 'Ldd', 'Cpir': 'Cpi', 'Cpdr': 'Cpd', 'Inir': 'Ini', 'Indr': 'Ind',
                        'Otir': 'Outi', 'Otdr': 'Outd'}[fn]
                C.append('%s();' % base)
                if base.startswith('Cp'):
                    cond = '(F & PF) != 0 && (F & ZF) == 0'
                elif base.startswith(('In', 'Out')):
                    cond = 'B != 0'
                else:
                    cond = '(F & PF) != 0'
                z.flow = 'cond'; z.cond = cond; z.target = z.addr; z.cyc_taken = 5
            else:
                C.append('%s();' % fn)
        else:
            z.text = 'nop (ed %02x)' % op; z.cyc = 8
        return a

    def gen_main(self, z, op, a, ix, pre):
        x = op >> 6; y = (op >> 3) & 7; zz = op & 7; p = y >> 1; q = y & 1
        C = z.code
        HLn = ix if ix else 'HL'
        def disp():
            nonlocal a
            d = sx8(self.b(a)); a += 1; return d
        if x == 0:
            if zz == 0:
                if y == 0: z.text = 'nop'; z.cyc = 4
                elif y == 1: z.text = "ex af,af'"; C.append('ExAF();'); z.cyc = 4
                elif y == 2:
                    d = disp(); z.text = 'djnz $%04x' % ((a + d) & 0xFFFF)
                    C.append('B = (B - 1) & 0xFF;')
                    z.flow = 'cond'; z.cond = 'B != 0'; z.target = (a + d) & 0xFFFF; z.cyc = 8; z.cyc_taken = 5
                elif y == 3:
                    d = disp(); z.target = (a + d) & 0xFFFF; z.text = 'jr $%04x' % z.target
                    z.flow = 'jp'; z.cyc = 12
                else:
                    d = disp(); z.target = (a + d) & 0xFFFF; z.text = 'jr %s,$%04x' % (CCN[y - 4], z.target)
                    z.flow = 'cond'; z.cond = CCX[y - 4]; z.cyc = 7; z.cyc_taken = 5
            elif zz == 1:
                if q == 0:
                    nn = self.w(a); a += 2
                    z.text = 'ld %s,$%04x' % (self.rpname(p, ix).lower(), nn)
                    C.append('%s = 0x%X;' % (self.rpname(p, ix), nn)); z.cyc = 10 + pre
                else:
                    z.text = 'add %s,%s' % (HLn.lower(), self.rpname(p, ix).lower())
                    C.append('%s = Add16(%s, %s);' % (HLn, HLn, self.rpname(p, ix))); z.cyc = 11 + pre
            elif zz == 2:
                if q == 0:
                    if p == 0: z.text = 'ld (bc),a'; C.append('Wr(BC, A);'); z.cyc = 7
                    elif p == 1: z.text = 'ld (de),a'; C.append('Wr(DE, A);'); z.cyc = 7
                    elif p == 2:
                        nn = self.w(a); a += 2; z.text = 'ld ($%04x),%s' % (nn, HLn.lower())
                        C.append('Wr16(0x%X, %s);' % (nn, HLn)); z.cyc = 16 + pre
                    else:
                        nn = self.w(a); a += 2; z.text = 'ld ($%04x),a' % nn
                        C.append('Wr(0x%X, A);' % nn); z.cyc = 13
                else:
                    if p == 0: z.text = 'ld a,(bc)'; C.append('A = Rd(BC);'); z.cyc = 7
                    elif p == 1: z.text = 'ld a,(de)'; C.append('A = Rd(DE);'); z.cyc = 7
                    elif p == 2:
                        nn = self.w(a); a += 2; z.text = 'ld %s,($%04x)' % (HLn.lower(), nn)
                        C.append('%s = Rd16(0x%X);' % (HLn, nn)); z.cyc = 16 + pre
                    else:
                        nn = self.w(a); a += 2; z.text = 'ld a,($%04x)' % nn
                        C.append('A = Rd(0x%X);' % nn); z.cyc = 13
            elif zz == 3:
                n = self.rpname(p, ix)
                z.text = '%s %s' % ('inc' if q == 0 else 'dec', n.lower())
                C.append('%s = (%s %s 1) & 0xFFFF;' % (n, n, '+' if q == 0 else '-')); z.cyc = 6 + pre
            elif zz in (4, 5):
                fn = 'Inc' if zz == 4 else 'Dec'
                d = disp() if (ix and y == 6) else 0
                if y == 6 and ix:
                    ad = '((%s + %d) & 0xFFFF)' % (ix, d)
                    C.append('Wr(%s, %s(Rd(%s)));' % (ad, fn, ad)); z.cyc = 23
                    z.text = '%s (%s%+d)' % (fn.lower(), ix.lower(), d)
                else:
                    C.append(self.r8set(y, ix, d, '%s(%s)' % (fn, self.r8get(y, ix, d))))
                    z.text = '%s %s' % (fn.lower(), R8[y].lower()); z.cyc = 11 if y == 6 else 4 + pre
            elif zz == 6:
                d = disp() if (ix and y == 6) else 0
                n = self.b(a); a += 1
                C.append(self.r8set(y, ix, d, '0x%X' % n))
                z.text = 'ld %s,$%02x' % (R8[y].lower(), n); z.cyc = 19 if (ix and y == 6) else (10 if y == 6 else 7 + pre)
            else:
                z.text = ['rlca', 'rrca', 'rla', 'rra', 'daa', 'cpl', 'scf', 'ccf'][y]
                C.append(['Rlca();', 'Rrca();', 'Rla();', 'Rra();', 'Daa();',
                          'A ^= 0xFF; F = (F & (SF | ZF | PF | CF)) | HF | NF | (A & (YF | XF));',
                          'F = (F & (SF | ZF | PF)) | CF | (A & (YF | XF));',
                          'F = ((F & (SF | ZF | PF | CF)) | ((F & CF) != 0 ? HF : 0) | (A & (YF | XF))) ^ CF;'][y])
                z.cyc = 4
        elif x == 1:
            if zz == 6 and y == 6:
                z.text = 'halt'; z.flow = 'halt'; z.cyc = 4
            else:
                d = disp() if (ix and (y == 6 or zz == 6)) else 0
                # with (ix+d), the other operand uses plain H/L
                if ix and (y == 6 or zz == 6):
                    src = self.r8get(zz, ix if zz == 6 else None, d)
                    C.append(self.r8set(y, ix if y == 6 else None, d, src))
                    z.cyc = 19
                else:
                    C.append(self.r8set(y, ix, d, self.r8get(zz, ix, d)))
                    z.cyc = 7 if (y == 6 or zz == 6) else 4 + pre
                z.text = 'ld %s,%s' % (R8[y].lower(), R8[zz].lower())
        elif x == 2:
            d = disp() if (ix and zz == 6) else 0
            C.append('%s(%s);' % (ALU[y], self.r8get(zz, ix, d)))
            z.text = ALUN[y] + R8[zz].lower(); z.cyc = 19 if (ix and zz == 6) else (7 if zz == 6 else 4 + pre)
        else:
            if zz == 0:
                z.text = 'ret %s' % CCN[y]; z.flow = 'retcond'; z.cond = CCX[y]; z.cyc = 5; z.cyc_taken = 6
            elif zz == 1:
                if q == 0:
                    n = self.rpname(p, ix, RP2); z.text = 'pop %s' % n.lower()
                    if n == 'AF': C.append('{ int v = Pop(); A = v >> 8; F = v & 0xFF; }')
                    else: C.append('%s = Pop();' % n)
                    z.cyc = 10 + pre
                else:
                    if p == 0: z.text = 'ret'; z.flow = 'ret'; z.cyc = 10
                    elif p == 1: z.text = 'exx'; C.append('Exx();'); z.cyc = 4
                    elif p == 2: z.text = 'jp (%s)' % HLn.lower(); z.flow = 'jpind'; z.cond = HLn; z.cyc = 4 + pre
                    else: z.text = 'ld sp,%s' % HLn.lower(); C.append('SP = %s;' % HLn); z.cyc = 6 + pre
            elif zz == 2:
                nn = self.w(a); a += 2
                z.text = 'jp %s,$%04x' % (CCN[y], nn); z.flow = 'cond'; z.cond = CCX[y]; z.target = nn; z.cyc = 10
            elif zz == 3:
                if y == 0:
                    nn = self.w(a); a += 2; z.text = 'jp $%04x' % nn; z.flow = 'jp'; z.target = nn; z.cyc = 10
                elif y == 2:
                    n = self.b(a); a += 1; z.text = 'out ($%02x),a' % n; C.append('Out(0x%X, A);' % n); z.cyc = 11
                elif y == 3:
                    n = self.b(a); a += 1; z.text = 'in a,($%02x)' % n; C.append('A = In(0x%X);' % n); z.cyc = 11
                elif y == 4:
                    z.text = 'ex (sp),%s' % HLn.lower()
                    C.append('{ int v = Rd16(SP); Wr16(SP, %s); %s = v; }' % (HLn, HLn)); z.cyc = 19 + pre
                elif y == 5:
                    z.text = 'ex de,hl'; C.append('{ int v = DE; DE = HL; HL = v; }'); z.cyc = 4
                elif y == 6:
                    z.text = 'di'; C.append('IFF1 = IFF2 = false;'); z.cyc = 4
                elif y == 7:
                    z.text = 'ei'; C.append('IFF1 = IFF2 = true;'); z.cyc = 4; z.flow = 'ei'
            elif zz == 4:
                nn = self.w(a); a += 2
                z.text = 'call %s,$%04x' % (CCN[y], nn); z.flow = 'callcond'; z.cond = CCX[y]; z.target = nn
                z.cyc = 10; z.cyc_taken = 7
            elif zz == 5:
                if q == 0:
                    n = self.rpname(p, ix, RP2); z.text = 'push %s' % n.lower()
                    C.append('Push(%s);' % ('(A << 8) | F' if n == 'AF' else n)); z.cyc = 11 + pre
                else:
                    nn = self.w(a); a += 2
                    z.text = 'call $%04x' % nn; z.flow = 'call'; z.target = nn; z.cyc = 17
            elif zz == 6:
                n = self.b(a); a += 1
                C.append('%s(0x%X);' % (ALU[y], n)); z.text = ALUN[y] + '$%02x' % n; z.cyc = 7
            else:
                z.text = 'rst $%02x' % (y * 8); z.flow = 'call'; z.target = y * 8; z.cyc = 11
        return a


def ends_block(z):
    return z.flow != 'next'
