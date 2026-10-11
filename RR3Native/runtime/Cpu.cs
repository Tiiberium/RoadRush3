// 68000 CPU state + helpers used by the statically recompiled code (Gen68k.cs)
using System;
using System.IO;

namespace RR3
{
    static partial class M68K
    {
        public static uint D0, D1, D2, D3, D4, D5, D6, D7;
        public static uint A0, A1, A2, A3, A4, A5, A6, A7;
        public static bool X, N, Z, V, C;
        public static bool S = true, T;
        public static int IMask = 7;
        public static uint USP, SSPsaved;
        public static uint PC;
        public static long Cyc;
        public static bool Stopped;
        public static int IrqLevel; // highest pending interrupt level

        static Func<uint>[] blocks;
        public static Action<uint> MissingHook;

        public static void Init()
        {
            blocks = new Func<uint>[0x200000 >> 1];
            RegisterBlocks(blocks);
        }

        public static void Reset()
        {
            S = true; T = false; IMask = 7;
            A7 = Bus.R32(0);
            PC = Bus.R32(4);
            Stopped = false;
        }

        // ---------- memory ----------
        static uint R8(uint a) { return Bus.R8(a); }
        static uint R16(uint a) { return Bus.R16(a); }
        static uint R32(uint a) { return Bus.R32(a); }
        static void W8(uint a, uint v) { Bus.W8(a, v); }
        static void W16(uint a, uint v) { Bus.W16(a, v); }
        static void W32(uint a, uint v) { Bus.W32(a, v); }

        // ---------- status register ----------
        public static uint GetCCR()
        {
            return (X ? 16u : 0) | (N ? 8u : 0) | (Z ? 4u : 0) | (V ? 2u : 0) | (C ? 1u : 0);
        }
        public static void SetCCR(uint v)
        {
            X = (v & 16) != 0; N = (v & 8) != 0; Z = (v & 4) != 0; V = (v & 2) != 0; C = (v & 1) != 0;
        }
        public static uint GetSR()
        {
            return (T ? 0x8000u : 0) | (S ? 0x2000u : 0) | ((uint)IMask << 8) | GetCCR();
        }
        public static void SetSR(uint v)
        {
            SetCCR(v);
            T = (v & 0x8000) != 0;
            IMask = (int)((v >> 8) & 7);
            bool ns = (v & 0x2000) != 0;
            if (ns != S)
            {
                if (ns) { USP = A7; A7 = SSPsaved; }
                else { SSPsaved = A7; A7 = USP; }
                S = ns;
            }
        }

        public static uint Exception(int vec, uint retpc)
        {
            uint sr = GetSR();
            if (!S) { USP = A7; A7 = SSPsaved; S = true; }
            T = false;
            A7 -= 4; W32(A7, retpc);
            A7 -= 2; W16(A7, sr);
            Cyc += 34;
            return R32((uint)vec * 4);
        }

        static void Stop() { Stopped = true; }

        static uint Missing(uint pc)
        {
            if (MissingHook != null) MissingHook(pc);
            throw new InvalidOperationException(string.Format("Unrecompiled code at {0:X6}", pc));
        }

        // ---------- execution ----------
        // Runs recompiled blocks until Cyc >= until.
        public static void Run(long until)
        {
            var tbl = blocks;
            while (Cyc < until)
            {
                if (IrqLevel > IMask)
                {
                    int lvl = IrqLevel;
                    Stopped = false;
                    uint sr = GetSR();
                    if (!S) { USP = A7; A7 = SSPsaved; S = true; }
                    T = false;
                    A7 -= 4; Bus.W32(A7, PC);
                    A7 -= 2; Bus.W16(A7, sr);
                    IMask = lvl;
                    Cyc += 44;
                    Vdp.AckIrq(lvl);
                    PC = Bus.R32((uint)(24 + lvl) * 4);
                }
                if (Stopped) { Cyc = until; return; }
                uint pc = PC & 0xFFFFFF;
                Func<uint> f = pc < 0x200000 ? tbl[pc >> 1] : null;
                if (f == null)
                {
                    if ((pc & 1) != 0) throw new InvalidOperationException(string.Format("Odd PC {0:X6}", pc));
                    PC = Missing(pc);
                    continue;
                }
                PC = f();
            }
        }

        // ---------- arithmetic helpers ----------
        static void DivU(int r, uint src)
        {
            uint d = GetD(r);
            uint q = d / src, rem = d % src;
            C = false;
            if (q > 0xFFFF) { V = true; N = true; Z = false; return; }
            SetD(r, (rem << 16) | q);
            N = (q & 0x8000) != 0; Z = q == 0; V = false;
        }
        static void DivS(int r, uint src)
        {
            int d = (int)GetD(r);
            int s = (short)src;
            C = false;
            if (d == int.MinValue && s == -1) { V = true; N = true; Z = false; return; }
            int q = d / s, rem = d % s;
            if (q < -32768 || q > 32767) { V = true; N = true; Z = false; return; }
            SetD(r, ((uint)(rem & 0xFFFF) << 16) | ((uint)q & 0xFFFF));
            N = (q & 0x8000) != 0; Z = (q & 0xFFFF) == 0; V = false;
        }
        static uint GetD(int r)
        {
            switch (r) { case 0: return D0; case 1: return D1; case 2: return D2; case 3: return D3; case 4: return D4; case 5: return D5; case 6: return D6; default: return D7; }
        }
        static void SetD(int r, uint v)
        {
            switch (r) { case 0: D0 = v; break; case 1: D1 = v; break; case 2: D2 = v; break; case 3: D3 = v; break; case 4: D4 = v; break; case 5: D5 = v; break; case 6: D6 = v; break; default: D7 = v; break; }
        }

        static uint Msk(int bits) { return bits == 32 ? 0xFFFFFFFFu : ((1u << bits) - 1); }
        static uint Sgn(int bits) { return 1u << (bits - 1); }
        static void NZ(uint r, int bits) { N = (r & Sgn(bits)) != 0; Z = (r & Msk(bits)) == 0; }

        static uint Sh_asl(uint v, int c, int bits)
        {
            uint m = Msk(bits);
            v &= m;
            if (c == 0) { C = false; V = false; NZ(v, bits); return v; }
            uint r;
            if (c >= bits)
            {
                r = 0;
                C = X = c == bits ? (v & 1) != 0 : false;
                V = v != 0;
            }
            else
            {
                r = (v << c) & m;
                C = X = ((v >> (bits - c)) & 1) != 0;
                uint top = (m << (bits - c - 1)) & m;
                uint t = v & top;
                V = t != 0 && t != top;
            }
            NZ(r, bits); return r;
        }
        static uint Sh_asr(uint v, int c, int bits)
        {
            uint m = Msk(bits);
            v &= m;
            V = false;
            if (c == 0) { C = false; NZ(v, bits); return v; }
            bool neg = (v & Sgn(bits)) != 0;
            uint r;
            if (c >= bits) { r = neg ? m : 0; C = X = neg; }
            else
            {
                C = X = ((v >> (c - 1)) & 1) != 0;
                r = v >> c;
                if (neg) r |= (m << (bits - c)) & m;
            }
            NZ(r, bits); return r;
        }
        static uint Sh_lsl(uint v, int c, int bits)
        {
            uint m = Msk(bits);
            v &= m;
            V = false;
            if (c == 0) { C = false; NZ(v, bits); return v; }
            uint r;
            if (c > bits) { r = 0; C = X = false; }
            else if (c == bits) { r = 0; C = X = (v & 1) != 0; }
            else { r = (v << c) & m; C = X = ((v >> (bits - c)) & 1) != 0; }
            NZ(r, bits); return r;
        }
        static uint Sh_lsr(uint v, int c, int bits)
        {
            uint m = Msk(bits);
            v &= m;
            V = false;
            if (c == 0) { C = false; NZ(v, bits); return v; }
            uint r;
            if (c > bits) { r = 0; C = X = false; }
            else if (c == bits) { r = 0; C = X = (v & Sgn(bits)) != 0; }
            else { r = v >> c; C = X = ((v >> (c - 1)) & 1) != 0; }
            NZ(r, bits); return r;
        }
        static uint Sh_rol(uint v, int c, int bits)
        {
            uint m = Msk(bits);
            v &= m;
            V = false;
            if (c == 0) { C = false; NZ(v, bits); return v; }
            int k = c & (bits - 1);
            uint r = k == 0 ? v : (((v << k) | (v >> (bits - k))) & m);
            C = (r & 1) != 0;
            NZ(r, bits); return r;
        }
        static uint Sh_ror(uint v, int c, int bits)
        {
            uint m = Msk(bits);
            v &= m;
            V = false;
            if (c == 0) { C = false; NZ(v, bits); return v; }
            int k = c & (bits - 1);
            uint r = k == 0 ? v : (((v >> k) | (v << (bits - k))) & m);
            C = (r & Sgn(bits)) != 0;
            NZ(r, bits); return r;
        }
        static uint Sh_roxl(uint v, int c, int bits)
        {
            uint m = Msk(bits);
            v &= m;
            V = false;
            c %= (bits + 1);
            for (int i = 0; i < c; i++)
            {
                bool o = (v & Sgn(bits)) != 0;
                v = ((v << 1) | (X ? 1u : 0u)) & m;
                X = o;
            }
            C = X;
            NZ(v, bits); return v;
        }
        static uint Sh_roxr(uint v, int c, int bits)
        {
            uint m = Msk(bits);
            v &= m;
            V = false;
            c %= (bits + 1);
            for (int i = 0; i < c; i++)
            {
                bool o = (v & 1) != 0;
                v = (v >> 1) | (X ? Sgn(bits) : 0u);
                X = o;
            }
            C = X;
            NZ(v, bits); return v;
        }

        static uint Abcd(uint src, uint dst)
        {
            uint x = X ? 1u : 0u;
            uint res = (src & 0x0F) + (dst & 0x0F) + x;
            if (res > 9) res += 6;
            res += (src & 0xF0) + (dst & 0xF0);
            C = X = res > 0x99;
            if (C) res -= 0xA0;
            res &= 0xFF;
            N = (res & 0x80) != 0;
            if (res != 0) Z = false;
            V = false;
            return res;
        }
        static uint Sbcd(uint src, uint dst)
        {
            uint x = X ? 1u : 0u;
            int res = (int)(dst & 0x0F) - (int)(src & 0x0F) - (int)x;
            int hi = (int)(dst & 0xF0) - (int)(src & 0xF0);
            if (res < 0) { res += 10; hi -= 0x10; }
            res = (res & 0x0F) + hi;
            C = X = res < 0;
            if (C) res += 0xA0;
            uint r = (uint)res & 0xFF;
            N = (r & 0x80) != 0;
            if (r != 0) Z = false;
            V = false;
            return r;
        }
    }
}
