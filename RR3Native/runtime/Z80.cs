// Z80 state + helpers for the statically recompiled sound driver (GenZ80.cs)
using System;
using System.IO;

namespace RR3
{
    static partial class Z80
    {
        public static int A, F, B, C, D, E, H, L;
        static int A_, F_, B_, C_, D_, E_, H_, L_;
        public static int IX, IY, SP, I, RReg, IM;
        public static bool IFF1, IFF2, Halted;
        public static int PC;
        public static long Cyc;
        public static int Bank; // 9-bit bank register (68k address bits 15-23)
        static int bankShift;

        const int CF = 0x01, NF = 0x02, PF = 0x04, XF = 0x08, HF = 0x10, YF = 0x20, ZF = 0x40, SF = 0x80;

        static Func<int>[] blocks;
        static bool[] isCode = new bool[0x2000];
        public static bool CodeValid = true;

        static int HL { get { return (H << 8) | L; } set { H = (value >> 8) & 0xFF; L = value & 0xFF; } }
        static int BC { get { return (B << 8) | C; } set { B = (value >> 8) & 0xFF; C = value & 0xFF; } }
        static int DE { get { return (D << 8) | E; } set { D = (value >> 8) & 0xFF; E = value & 0xFF; } }
        static int IXH { get { return IX >> 8; } set { IX = (IX & 0xFF) | ((value & 0xFF) << 8); } }
        static int IXL { get { return IX & 0xFF; } set { IX = (IX & 0xFF00) | (value & 0xFF); } }
        static int IYH { get { return IY >> 8; } set { IY = (IY & 0xFF) | ((value & 0xFF) << 8); } }
        static int IYL { get { return IY & 0xFF; } set { IY = (IY & 0xFF00) | (value & 0xFF); } }

        static readonly byte[] szp = new byte[256];
        static Z80()
        {
            for (int i = 0; i < 256; i++)
            {
                int p = 0; for (int b = 0; b < 8; b++) p ^= (i >> b) & 1;
                szp[i] = (byte)((i & (SF | YF | XF)) | (i == 0 ? ZF : 0) | (p == 0 ? PF : 0));
            }
        }
        static int SZ(int v) { return (v & (SF | YF | XF)) | (v == 0 ? ZF : 0); }
        static void SetSZP(int v) { F = (F & CF) | szp[v & 0xFF]; }

        public static void Init()
        {
            blocks = new Func<int>[0x10000];
            RegisterBlocks(blocks);
            foreach (var a in CodeBytes) isCode[a] = true;
            foreach (var a in PatchBytes) isCode[a] = false; // bytes the driver rewrites by design
        }

        public static void Reset()
        {
            PC = 0; IFF1 = IFF2 = false; IM = 0; Halted = false; SP = 0xFFFF;
            A = F = 0xFF;
            needCheck = true;
        }

        // verify that the RAM image still matches the driver we recompiled
        public static void CheckImage()
        {
            var ram = Sound.Z80Ram;
            bool ok = true;
            foreach (var a in CodeBytes)
            {
                if (!isCode[a]) continue; // self-modified operand bytes
                if (ram[a] != DriverImage[a]) { ok = false; Log(string.Format("Z80 image mismatch at {0:X4}: {1:X2} vs {2:X2}", a, ram[a], DriverImage[a])); break; }
            }
            if (ok) Log("Z80 driver image verified");
            CodeValid = ok;
        }

        public static void Log(string s)
        {
            try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "z80log.txt"), s + "\n"); } catch { }
        }

        static bool needCheck;

        public static void Run(long until)
        {
            if (needCheck) { needCheck = false; CheckImage(); }
            if (!CodeValid) { Cyc = until; return; }
            var t = blocks;
            while (Cyc < until)
            {
                if (Halted) { Cyc = until; return; }
                var f = t[PC & 0xFFFF];
                if (f == null) { PC = Missing(PC); continue; }
                PC = f();
            }
        }

        public static void Interrupt()
        {
            if (!IFF1 || !CodeValid) return;
            Halted = false;
            IFF1 = IFF2 = false;
            Push(PC);
            PC = 0x38;
            Cyc += 13;
        }

        static int Missing(int pc)
        {
            Log(string.Format("Z80 missing block {0:X4}", pc));
            File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "missing_z80.txt"), pc.ToString("X4") + "\n");
            CodeValid = false;
            return pc;
        }

        // ---------------- memory ----------------
        static int Rd(int a)
        {
            a &= 0xFFFF;
            if (a < 0x4000) return Sound.Z80Ram[a & 0x1FFF];
            if (a < 0x6000) return Sound.YmReadStatus(Cyc);
            if (a >= 0x8000) return (int)Bus.R8((uint)((Bank << 15) | (a & 0x7FFF)));
            return 0xFF;
        }

        static void Wr(int a, int v)
        {
            a &= 0xFFFF; v &= 0xFF;
            if (a < 0x4000)
            {
                int o = a & 0x1FFF;
                if (isCode[o] && Sound.Z80Ram[o] != v) { Log(string.Format("Z80 self-modify {0:X4}={1:X2} at PC {2:X4}", o, v, PC)); }
                Sound.Z80Ram[o] = (byte)v;
                return;
            }
            if (a < 0x6000) { Sound.YmWrite(a & 3, v, Cyc); return; }
            if (a < 0x6100) { Bank = ((Bank >> 1) | ((v & 1) << 8)) & 0x1FF; return; }
            if (a == 0x7F11) { Sound.PsgWriteZ(v, Cyc); return; }
            if (a >= 0x8000) { Bus.W8((uint)((Bank << 15) | (a & 0x7FFF)), (uint)v); return; }
        }

        static int Rd16(int a) { return Rd(a) | (Rd(a + 1) << 8); }
        static void Wr16(int a, int v) { Wr(a, v & 0xFF); Wr(a + 1, (v >> 8) & 0xFF); }
        static void Push(int v) { SP = (SP - 2) & 0xFFFF; Wr16(SP, v); }
        static int Pop() { int v = Rd16(SP); SP = (SP + 2) & 0xFFFF; return v; }
        static int In(int p) { return 0xFF; }
        static void Out(int p, int v) { }

        // ---------------- ALU ----------------
        static void Add(int v)
        {
            int r = A + v;
            F = SZ(r & 0xFF) | ((r >> 8) & CF) | ((A ^ v ^ r) & HF) | ((((A ^ ~v) & (A ^ r)) >> 5) & PF);
            A = r & 0xFF;
        }
        static void Adc(int v)
        {
            int c = F & CF;
            int r = A + v + c;
            F = SZ(r & 0xFF) | ((r >> 8) & CF) | ((A ^ v ^ r) & HF) | ((((A ^ ~v) & (A ^ r)) >> 5) & PF);
            A = r & 0xFF;
        }
        static void Sub(int v)
        {
            int r = A - v;
            F = SZ(r & 0xFF) | ((r >> 8) & CF) | NF | ((A ^ v ^ r) & HF) | ((((A ^ v) & (A ^ r)) >> 5) & PF);
            A = r & 0xFF;
        }
        static void Sbc(int v)
        {
            int c = F & CF;
            int r = A - v - c;
            F = SZ(r & 0xFF) | ((r >> 8) & CF) | NF | ((A ^ v ^ r) & HF) | ((((A ^ v) & (A ^ r)) >> 5) & PF);
            A = r & 0xFF;
        }
        static void And(int v) { A &= v; F = szp[A] | HF; }
        static void Xor(int v) { A = (A ^ v) & 0xFF; F = szp[A]; }
        static void Or(int v) { A = (A | v) & 0xFF; F = szp[A]; }
        static void Cp(int v)
        {
            int r = A - v;
            F = (SZ(r & 0xFF) & ~(YF | XF)) | (v & (YF | XF)) | ((r >> 8) & CF) | NF | ((A ^ v ^ r) & HF) | ((((A ^ v) & (A ^ r)) >> 5) & PF);
        }
        static int Inc(int v)
        {
            int r = (v + 1) & 0xFF;
            F = (F & CF) | SZ(r) | (r == 0x80 ? PF : 0) | ((r & 0x0F) == 0 ? HF : 0);
            return r;
        }
        static int Dec(int v)
        {
            int r = (v - 1) & 0xFF;
            F = (F & CF) | SZ(r) | NF | (r == 0x7F ? PF : 0) | ((r & 0x0F) == 0x0F ? HF : 0);
            return r;
        }
        static int Add16(int a, int b)
        {
            int r = a + b;
            F = (F & (SF | ZF | PF)) | ((r >> 16) & CF) | (((a ^ b ^ r) >> 8) & HF) | ((r >> 8) & (YF | XF));
            return r & 0xFFFF;
        }
        static int Adc16(int a, int b)
        {
            int c = F & CF;
            int r = a + b + c;
            F = (((r >> 8) & (SF | YF | XF))) | ((r & 0xFFFF) == 0 ? ZF : 0) | ((r >> 16) & CF) | (((a ^ b ^ r) >> 8) & HF)
                | ((((a ^ ~b) & (a ^ r)) >> 13) & PF);
            return r & 0xFFFF;
        }
        static int Sbc16(int a, int b)
        {
            int c = F & CF;
            int r = a - b - c;
            F = (((r >> 8) & (SF | YF | XF))) | ((r & 0xFFFF) == 0 ? ZF : 0) | ((r >> 16) & CF) | NF | (((a ^ b ^ r) >> 8) & HF)
                | ((((a ^ b) & (a ^ r)) >> 13) & PF);
            return r & 0xFFFF;
        }
        static void Rlca() { A = ((A << 1) | (A >> 7)) & 0xFF; F = (F & (SF | ZF | PF)) | (A & (YF | XF | CF)); }
        static void Rrca() { F = (F & (SF | ZF | PF)) | (A & CF); A = ((A >> 1) | (A << 7)) & 0xFF; F |= A & (YF | XF); }
        static void Rla() { int r = ((A << 1) | (F & CF)) & 0xFF; F = (F & (SF | ZF | PF)) | ((A >> 7) & CF) | (r & (YF | XF)); A = r; }
        static void Rra() { int r = ((A >> 1) | ((F & CF) << 7)) & 0xFF; F = (F & (SF | ZF | PF)) | (A & CF) | (r & (YF | XF)); A = r; }
        static void Daa()
        {
            int a = A, c = 0, d = 0;
            bool n = (F & NF) != 0;
            if ((F & HF) != 0 || (a & 0x0F) > 9) d = 6;
            if ((F & CF) != 0 || a > 0x99) { d |= 0x60; c = CF; }
            int r = n ? a - d : a + d;
            r &= 0xFF;
            F = szp[r] | c | (F & NF) | ((a ^ r) & HF);
            A = r;
        }
        static int Rlc(int v) { int r = ((v << 1) | (v >> 7)) & 0xFF; F = szp[r] | (v >> 7); return r; }
        static int Rrc(int v) { int r = ((v >> 1) | (v << 7)) & 0xFF; F = szp[r] | (v & 1); return r; }
        static int Rl(int v) { int r = ((v << 1) | (F & CF)) & 0xFF; F = szp[r] | (v >> 7); return r; }
        static int Rr(int v) { int r = ((v >> 1) | ((F & CF) << 7)) & 0xFF; F = szp[r] | (v & 1); return r; }
        static int Sla(int v) { int r = (v << 1) & 0xFF; F = szp[r] | (v >> 7); return r; }
        static int Sra(int v) { int r = ((v >> 1) | (v & 0x80)) & 0xFF; F = szp[r] | (v & 1); return r; }
        static int Sll(int v) { int r = ((v << 1) | 1) & 0xFF; F = szp[r] | (v >> 7); return r; }
        static int Srl(int v) { int r = (v >> 1) & 0xFF; F = szp[r] | (v & 1); return r; }
        static void Bit(int b, int v)
        {
            int r = v & (1 << b);
            F = (F & CF) | HF | (r == 0 ? (ZF | PF) : 0) | (r & SF) | (v & (YF | XF));
        }
        static void Rrd()
        {
            int m = Rd(HL);
            Wr(HL, ((A << 4) | (m >> 4)) & 0xFF);
            A = (A & 0xF0) | (m & 0x0F);
            F = (F & CF) | szp[A];
        }
        static void Rld()
        {
            int m = Rd(HL);
            Wr(HL, ((m << 4) | (A & 0x0F)) & 0xFF);
            A = (A & 0xF0) | (m >> 4);
            F = (F & CF) | szp[A];
        }
        static void ExAF() { int t = A; A = A_; A_ = t; t = F; F = F_; F_ = t; }
        static void Exx()
        {
            int t;
            t = B; B = B_; B_ = t; t = C; C = C_; C_ = t;
            t = D; D = D_; D_ = t; t = E; E = E_; E_ = t;
            t = H; H = H_; H_ = t; t = L; L = L_; L_ = t;
        }
        static void Ldi()
        {
            int v = Rd(HL); Wr(DE, v);
            HL = (HL + 1) & 0xFFFF; DE = (DE + 1) & 0xFFFF; BC = (BC - 1) & 0xFFFF;
            F = (F & (SF | ZF | CF)) | (BC != 0 ? PF : 0);
        }
        static void Ldd()
        {
            int v = Rd(HL); Wr(DE, v);
            HL = (HL - 1) & 0xFFFF; DE = (DE - 1) & 0xFFFF; BC = (BC - 1) & 0xFFFF;
            F = (F & (SF | ZF | CF)) | (BC != 0 ? PF : 0);
        }
        static void Cpi()
        {
            int v = Rd(HL); int r = (A - v) & 0xFF;
            HL = (HL + 1) & 0xFFFF; BC = (BC - 1) & 0xFFFF;
            F = (F & CF) | NF | (SZ(r) & (SF | ZF)) | ((A ^ v ^ r) & HF) | (BC != 0 ? PF : 0);
        }
        static void Cpd()
        {
            int v = Rd(HL); int r = (A - v) & 0xFF;
            HL = (HL - 1) & 0xFFFF; BC = (BC - 1) & 0xFFFF;
            F = (F & CF) | NF | (SZ(r) & (SF | ZF)) | ((A ^ v ^ r) & HF) | (BC != 0 ? PF : 0);
        }
        static void Ini() { Wr(HL, In(C)); HL = (HL + 1) & 0xFFFF; B = Dec(B); }
        static void Ind() { Wr(HL, In(C)); HL = (HL - 1) & 0xFFFF; B = Dec(B); }
        static void Outi() { B = Dec(B); Out(C, Rd(HL)); HL = (HL + 1) & 0xFFFF; }
        static void Outd() { B = Dec(B); Out(C, Rd(HL)); HL = (HL - 1) & 0xFFFF; }
    }
}
