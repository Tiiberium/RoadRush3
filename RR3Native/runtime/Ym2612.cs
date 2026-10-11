// YM2612 (OPN2) FM synthesizer, modelled after the MAME fm.c OPN core
using System;

namespace RR3
{
    class Ym2612
    {
        const int SIN_LEN = 1024, TL_RES_LEN = 256, TL_TAB_LEN = 13 * 2 * TL_RES_LEN;
        const int ENV_QUIET = TL_TAB_LEN >> 3;
        const int MAX_ATT = 1023;
        static int[] tlTab = new int[TL_TAB_LEN];
        static int[] sinTab = new int[SIN_LEN];
        static readonly byte[] egInc = {
            0,1,0,1,0,1,0,1, 0,1,0,1,1,1,0,1, 0,1,1,1,0,1,1,1, 0,1,1,1,1,1,1,1,
            1,1,1,1,1,1,1,1, 1,1,1,2,1,1,1,2, 1,2,1,2,1,2,1,2, 1,2,2,2,1,2,2,2,
            2,2,2,2,2,2,2,2, 2,2,2,4,2,2,2,4, 2,4,2,4,2,4,2,4, 2,4,4,4,2,4,4,4,
            4,4,4,4,4,4,4,4, 4,4,4,8,4,4,4,8, 4,8,4,8,4,8,4,8, 4,8,8,8,4,8,8,8,
            8,8,8,8,8,8,8,8, 16,16,16,16,16,16,16,16, 0,0,0,0,0,0,0,0 };
        static byte[] egSel = new byte[64], egShift = new byte[64];
        static readonly byte[][] dtTab = {
            new byte[]{0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0},
            new byte[]{0,0,0,0,1,1,1,1,1,1,1,1,2,2,2,2,2,3,3,3,4,4,4,5,5,6,6,7,8,8,8,8},
            new byte[]{1,1,1,1,2,2,2,2,2,3,3,3,4,4,4,5,5,6,6,7,8,8,9,10,11,12,13,14,16,16,16,16},
            new byte[]{2,2,2,2,2,3,3,3,4,4,4,5,5,6,6,7,8,8,9,10,11,12,13,14,16,17,19,20,22,22,22,22} };
        static readonly int[] lfoSamples = { 108, 77, 71, 67, 62, 44, 8, 5 };
        static readonly int[] amsShift = { 8, 3, 1, 0 };
        static readonly double[] pmsDepth = { 0, 3.4, 6.7, 10, 14, 20, 40, 80 }; // cents

        static Ym2612()
        {
            for (int x = 0; x < TL_RES_LEN; x++)
            {
                double m = (1 << 16) / Math.Pow(2, (x + 1) * (1.0 / 32.0) / 8.0);
                int n = (int)Math.Floor(m);
                n >>= 4;
                if ((n & 1) != 0) n = (n >> 1) + 1; else n >>= 1;
                n <<= 2;
                tlTab[x * 2] = n; tlTab[x * 2 + 1] = -n;
                for (int i = 1; i < 13; i++)
                {
                    tlTab[x * 2 + i * 2 * TL_RES_LEN] = tlTab[x * 2] >> i;
                    tlTab[x * 2 + 1 + i * 2 * TL_RES_LEN] = -(tlTab[x * 2] >> i);
                }
            }
            for (int i = 0; i < SIN_LEN; i++)
            {
                double m = Math.Sin(((i * 2) + 1) * Math.PI / SIN_LEN);
                double o = 8 * Math.Log(1.0 / Math.Abs(m)) / Math.Log(2.0);
                o = o / (1.0 / 32.0);
                int n = (int)(2.0 * o);
                if ((n & 1) != 0) n = (n >> 1) + 1; else n >>= 1;
                sinTab[i] = n * 2 + (m >= 0.0 ? 0 : 1);
            }
            for (int r = 0; r < 64; r++)
            {
                if (r < 48)
                {
                    egSel[r] = (byte)(r < 2 ? 18 : (r & 3));
                    egShift[r] = (byte)(11 - (r >> 2));
                }
                else if (r < 60) { egSel[r] = (byte)(4 + (r - 48)); egShift[r] = 0; }
                else { egSel[r] = 16; egShift[r] = 0; }
            }
            // MAME (Nemesis' tests): rates 0,1 infinite; 2-5 select 0; 6,7 select 2
            egSel[2] = 0; egSel[3] = 0; egSel[4] = 0; egSel[5] = 0; egSel[6] = 2; egSel[7] = 2;
        }

        class Op
        {
            public int dt, mul, tl, ks, ar, d1r, d2r, sl, rr, am, ssg;
            public uint phase; public int incr;
            public int att = MAX_ATT; public int state; // 0 off/release,1 attack,2 decay,3 sustain,4 release
            public bool key;
            public int ksr;
        }

        class Ch
        {
            public Op[] op = { new Op(), new Op(), new Op(), new Op() }; // register order: S1,S3,S2,S4
            public int fnum, block, kc, fnHi;
            public int alg, fb, ams, pms; public bool left = true, right = true;
            public int op1a, op1b, memValue;
        }

        Ch[] ch = new Ch[6];
        int[] reg = new int[0x200];
        int addr0, addr1;
        int ch3mode;
        int[] ch3fn = new int[3], ch3blk = new int[3], ch3kc = new int[3];
        int ch3fnHi;
        public int Dac = 0x80; public bool DacEnable;
        public static long DacWrites;
        int lfoEn, lfoFreq, lfoCnt, lfoTimer;
        int egCnt = 1, egTimer;
        // timers
        int ta, tb, taCnt, tbCnt, timerCtrl;
        public int Status;

        public Ym2612() { for (int i = 0; i < 6; i++) ch[i] = new Ch(); Reset(); }

        public void Reset()
        {
            for (int i = 0; i < 6; i++)
            {
                ch[i] = new Ch();
            }
            Status = 0; timerCtrl = 0; taCnt = tbCnt = 0; DacEnable = false; Dac = 0x80;
            for (int r = 0xB4; r <= 0xB6; r++) { Write(0, r); Write(1, 0xC0); Write(2, r); Write(3, 0xC0); }
        }

        public void Write(int port, int v)
        {
            v &= 0xFF;
            switch (port)
            {
                case 0: addr0 = v; break;
                case 2: addr1 = v; break;
                case 1: WriteReg(addr0, v, 0); break;
                case 3: if (addr1 >= 0x30) WriteReg(addr1, v, 1); break;
            }
        }

        void WriteReg(int r, int v, int part)
        {
            reg[r + part * 0x100] = v;
            if (part == 0 && r < 0x30)
            {
                switch (r)
                {
                    case 0x22: lfoEn = (v >> 3) & 1; lfoFreq = v & 7; if (lfoEn == 0) lfoCnt = 0; break;
                    case 0x24: ta = (ta & 3) | (v << 2); break;
                    case 0x25: ta = (ta & 0x3FC) | (v & 3); break;
                    case 0x26: tb = v; break;
                    case 0x27:
                        if ((v & 1) != 0 && (timerCtrl & 1) == 0) taCnt = 1024 - ta;
                        if ((v & 2) != 0 && (timerCtrl & 2) == 0) tbCnt = (256 - tb) << 4;
                        if ((v & 0x10) != 0) Status &= ~1;
                        if ((v & 0x20) != 0) Status &= ~2;
                        timerCtrl = v & 0x0F;
                        ch3mode = (v >> 6) & 3;
                        break;
                    case 0x28:
                        {
                            int c = v & 3; if (c == 3) return;
                            if ((v & 4) != 0) c += 3;
                            var C = ch[c];
                            KeyOp(C, C.op[0], (v & 0x10) != 0);
                            KeyOp(C, C.op[2], (v & 0x20) != 0);
                            KeyOp(C, C.op[1], (v & 0x40) != 0);
                            KeyOp(C, C.op[3], (v & 0x80) != 0);
                            break;
                        }
                    case 0x2A: Dac = v; DacWrites++; break;
                    case 0x2B: DacEnable = (v & 0x80) != 0; break;
                }
                return;
            }
            int cn = r & 3; if (cn == 3) return;
            var ChX = ch[cn + part * 3];
            if (r < 0xA0)
            {
                var o = ChX.op[(r >> 2) & 3];
                switch (r & 0xF0)
                {
                    case 0x30: o.dt = (v >> 4) & 7; o.mul = v & 15; break;
                    case 0x40: o.tl = v & 0x7F; break;
                    case 0x50: o.ks = v >> 6; o.ar = v & 31; break;
                    case 0x60: o.am = v >> 7; o.d1r = v & 31; break;
                    case 0x70: o.d2r = v & 31; break;
                    case 0x80: o.sl = v >> 4; o.rr = v & 15; break;
                    case 0x90: o.ssg = v & 15; break;
                }
                return;
            }
            switch (r & 0xFC)
            {
                case 0xA0: ChX.fnum = ((ChX.fnHi & 7) << 8) | v; ChX.block = (ChX.fnHi >> 3) & 7; ChX.kc = KeyCode(ChX.fnum, ChX.block); break;
                case 0xA4: ChX.fnHi = v & 0x3F; break;
                case 0xA8:
                    if (part == 0) { ch3fn[cn] = ((ch3fnHi & 7) << 8) | v; ch3blk[cn] = (ch3fnHi >> 3) & 7; ch3kc[cn] = KeyCode(ch3fn[cn], ch3blk[cn]); }
                    break;
                case 0xAC: if (part == 0) ch3fnHi = v & 0x3F; break;
                case 0xB0: ChX.fb = (v >> 3) & 7; ChX.alg = v & 7; break;
                case 0xB4: ChX.left = (v & 0x80) != 0; ChX.right = (v & 0x40) != 0; ChX.ams = (v >> 4) & 3; ChX.pms = v & 7; break;
            }
        }

        static readonly int[] fkTable = { 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 3, 3, 3, 3, 3, 3 };
        static int KeyCode(int fnum, int block) { return (block << 2) | fkTable[(fnum >> 7) & 15]; }

        void KeyOp(Ch c, Op o, bool on)
        {
            if (on && !o.key)
            {
                o.key = true;
                o.phase = 0;
                o.state = 1;
                int rate = o.ar == 0 ? 0 : Math.Min(63, o.ar * 2 + (KcFor(c, o) >> (3 - o.ks)));
                if (rate >= 62) { o.att = 0; o.state = 2; }
            }
            else if (!on && o.key)
            {
                o.key = false;
                if (o.state != 0) o.state = 4;
            }
        }

        int KcFor(Ch c, Op o)
        {
            if (c == ch[2] && ch3mode != 0)
            {
                int idx = Array.IndexOf(c.op, o);
                if (idx == 0) return ch3kc[1];
                if (idx == 2) return ch3kc[2];
                if (idx == 1) return ch3kc[0];
            }
            return c.kc;
        }

        int Incr(Ch c, Op o, int idx, double pmFactor)
        {
            int fnum = c.fnum, block = c.block, kc = c.kc;
            if (c == ch[2] && ch3mode != 0 && idx != 3)
            {
                int k = idx == 0 ? 1 : idx == 2 ? 2 : 0;
                fnum = ch3fn[k]; block = ch3blk[k]; kc = ch3kc[k];
            }
            if (pmFactor != 1.0) fnum = (int)(fnum * pmFactor);
            int inc = ((fnum << block) >> 1);
            int dt = dtTab[o.dt & 3][kc];
            if ((o.dt & 4) != 0) inc -= dt; else inc += dt;
            inc &= 0x1FFFF;
            if (o.mul == 0) inc >>= 1; else inc *= o.mul;
            o.ksr = kc >> (3 - o.ks);
            return inc & 0xFFFFF;
        }

        void UpdateEnv(Op o)
        {
            int rate;
            switch (o.state)
            {
                case 1:
                    rate = o.ar == 0 ? 0 : Math.Min(63, o.ar * 2 + o.ksr);
                    if (rate >= 62) { o.att = 0; o.state = 2; return; }
                    if ((egCnt & ((1 << egShift[rate]) - 1)) == 0)
                    {
                        int inc = egInc[egSel[rate] * 8 + ((egCnt >> egShift[rate]) & 7)];
                        o.att += ((~o.att) * inc) >> 4;
                        if (o.att <= 0) { o.att = 0; o.state = 2; }
                    }
                    break;
                case 2:
                    {
                        int sl = o.sl == 15 ? 0x3E0 : o.sl << 5;
                        if (o.att >= sl) { o.state = 3; break; }
                        rate = o.d1r == 0 ? 0 : Math.Min(63, o.d1r * 2 + o.ksr);
                        Step(o, rate);
                        if (o.att >= sl) o.state = 3;
                        break;
                    }
                case 3:
                    rate = o.d2r == 0 ? 0 : Math.Min(63, o.d2r * 2 + o.ksr);
                    Step(o, rate);
                    break;
                case 4:
                    rate = Math.Min(63, o.rr * 4 + 2 + o.ksr);
                    Step(o, rate);
                    if (o.att >= MAX_ATT) { o.att = MAX_ATT; o.state = 0; }
                    break;
            }
        }

        void Step(Op o, int rate)
        {
            if (rate < 2) return;
            if ((egCnt & ((1 << egShift[rate]) - 1)) == 0)
            {
                o.att += egInc[egSel[rate] * 8 + ((egCnt >> egShift[rate]) & 7)];
                if (o.att > MAX_ATT) o.att = MAX_ATT;
            }
        }

        static int OpCalc(uint phase, int env, int pm)
        {
            int p = (env << 3) + sinTab[((int)(phase >> 10) + pm) & (SIN_LEN - 1)];
            if (p >= TL_TAB_LEN) return 0;
            return tlTab[p];
        }

        // advance timers by one sample
        void TimerTick()
        {
            if ((timerCtrl & 1) != 0)
            {
                if (--taCnt <= 0)
                {
                    taCnt = 1024 - ta;
                    if ((timerCtrl & 4) != 0) Status |= 1;
                }
            }
            if ((timerCtrl & 2) != 0)
            {
                if (--tbCnt <= 0)
                {
                    tbCnt = (256 - tb) << 4;
                    if ((timerCtrl & 8) != 0) Status |= 2;
                }
            }
        }

        public void TimersOnly(int n) { for (int i = 0; i < n; i++) TimerTick(); }

        // render n samples (stereo interleaved, at the native ~53267 Hz rate)
        public void Render(int[] buf, int off, int n)
        {
            for (int s = 0; s < n; s++)
            {
                TimerTick();
                // LFO
                int lfoAm = 0, lfoPmStep = 0;
                if (lfoEn != 0)
                {
                    if (++lfoTimer >= lfoSamples[lfoFreq]) { lfoTimer = 0; lfoCnt = (lfoCnt + 1) & 127; }
                    lfoAm = lfoCnt < 64 ? lfoCnt * 2 : 126 - (lfoCnt - 64) * 2;
                    lfoPmStep = lfoCnt >> 2;
                }
                // envelope clock: every 3 samples
                bool egTick = false;
                if (++egTimer >= 3) { egTimer = 0; egTick = true; egCnt++; if (egCnt == 4096) egCnt = 1; }
                int l = 0, r = 0;
                for (int c = 0; c < 6; c++)
                {
                    var C = ch[c];
                    double pmf = 1.0;
                    if (lfoEn != 0 && C.pms != 0)
                    {
                        // triangle in [-1,1] from the 32-step PM counter
                        int st = lfoPmStep & 31;
                        double tri = st < 8 ? st / 8.0 : st < 24 ? (16 - st) / 8.0 : (st - 32) / 8.0;
                        pmf = Math.Pow(2, tri * pmsDepth[C.pms] / 1200.0);
                    }
                    for (int k = 0; k < 4; k++)
                    {
                        var o = C.op[k];
                        o.incr = Incr(C, o, k, pmf);
                        if (egTick) UpdateEnv(o);
                    }
                    int outv;
                    if (c == 5 && DacEnable) outv = (Dac - 128) << 6;
                    else outv = ChanCalc(C, lfoAm);
                    if (outv > 8191) outv = 8191; else if (outv < -8192) outv = -8192;
                    if (C.left) l += outv;
                    if (C.right) r += outv;
                }
                buf[off + s * 2] = l;
                buf[off + s * 2 + 1] = r;
            }
        }

        int Vol(Ch c, Op o, int lfoAm)
        {
            int v = o.att + (o.tl << 3);
            if (o.am != 0 && c.ams != 0) v += lfoAm >> amsShift[c.ams];
            return v;
        }

        int ChanCalc(Ch C, int lfoAm)
        {
            Op s1 = C.op[0], s3 = C.op[1], s2 = C.op[2], s4 = C.op[3];
            int m2 = 0, c1 = 0, c2 = 0, mem = 0, outp = 0;
            // restore delayed sample into its destination
            int alg = C.alg;
            switch (alg)
            {
                case 0: case 1: case 2: case 5: m2 = C.memValue; break;
                case 3: c2 = C.memValue; break;
            }
            // operator 1 (with feedback, output delayed by one sample)
            int fbIn = C.op1a + C.op1b;
            int o1 = C.op1a;
            C.op1a = C.op1b;
            o1 = C.op1a;
            switch (alg)
            {
                case 0: case 3: case 4: case 6: c1 += o1; break;
                case 1: mem += o1; break;
                case 2: c2 += o1; break;
                case 5: mem = c1 = c2 = o1; break;
                case 7: outp += o1; break;
            }
            C.op1b = 0;
            int e = Vol(C, s1, lfoAm);
            if (e < ENV_QUIET)
            {
                int pm = C.fb != 0 ? (fbIn >> (10 - C.fb)) : 0;
                C.op1b = OpCalc(s1.phase, e, pm);
            }
            // operator 3 (M2)
            e = Vol(C, s3, lfoAm);
            if (e < ENV_QUIET)
            {
                int v = OpCalc(s3.phase, e, m2 >> 1);
                switch (alg) { case 0: case 1: case 2: case 3: c2 += v; break; case 4: c2 += v; break; default: outp += v; break; }
            }
            // operator 2 (C1)
            e = Vol(C, s2, lfoAm);
            if (e < ENV_QUIET)
            {
                int v = OpCalc(s2.phase, e, c1 >> 1);
                switch (alg) { case 0: case 1: case 2: case 3: mem += v; break; default: outp += v; break; }
            }
            // operator 4 (C2)
            e = Vol(C, s4, lfoAm);
            if (e < ENV_QUIET) outp += OpCalc(s4.phase, e, c2 >> 1);
            C.memValue = mem;
            s1.phase = (s1.phase + (uint)s1.incr) & 0xFFFFF;
            s2.phase = (s2.phase + (uint)s2.incr) & 0xFFFFF;
            s3.phase = (s3.phase + (uint)s3.incr) & 0xFFFFF;
            s4.phase = (s4.phase + (uint)s4.incr) & 0xFFFFF;
            return outp;
        }
    }
}
