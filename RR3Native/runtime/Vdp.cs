// Mega Drive VDP (YM7101): ports, DMA, interrupts and scanline renderer
using System;

namespace RR3
{
    static class Vdp
    {
        public static byte[] Vram = new byte[0x10000];
        public static ushort[] Cram = new ushort[64];
        public static ushort[] Vsram = new ushort[40];
        public static byte[] Reg = new byte[32];

        static bool pending;
        static int code;
        static uint addr;
        static bool fillPending;

        public static bool VIntPending, HIntPending;
        public static bool InVBlank;
        public static int Line;
        static int hcounter;
        static bool spriteOverflow, spriteCollision;

        public const int W = 320, H = 240;
        public static int[] Frame = new int[W * H];
        public static int ScreenWidth = 320, ScreenHeight = 224;
        static int[] palRgb = new int[64 * 3]; // normal, shadow, highlight
        static bool palDirty = true;

        public static long LineStartCyc;

        public static void HardReset()
        {
            Array.Clear(Vram, 0, Vram.Length); Array.Clear(Cram, 0, Cram.Length); Array.Clear(Vsram, 0, Vsram.Length);
            VIntPending = HIntPending = false; fillPending = false; code = 0; addr = 0;
            palDirty = true;
            Reset();
        }

        public static void Reset()
        {
            Array.Clear(Reg, 0, 32);
            pending = false;
        }

        // ---------------- ports ----------------
        public static uint ReadPort(uint p)
        {
            if (p < 4) return ReadData();
            if (p < 8) return ReadStatus();
            if (p < 0x10) return HVCounter();
            return 0xFFFF;
        }

        public static void WritePort(uint p, uint v)
        {
            if (p < 4) WriteData(v);
            else if (p < 8) WriteControl(v);
        }

        static uint ReadStatus()
        {
            pending = false;
            uint s = 0x3400 | 0x200; // FIFO empty
            if (VIntPending) s |= 0x80;
            if (spriteOverflow) s |= 0x40;
            if (spriteCollision) s |= 0x20;
            if (InVBlank || (Reg[1] & 0x40) == 0) s |= 0x08;
            long c = M68K.Cyc - LineStartCyc;
            if (c >= HBlankStart) s |= 0x04; // hblank
            spriteOverflow = false; spriteCollision = false;
            return s;
        }

        static uint HVCounter()
        {
            int v = Line;
            if (v > 0xEA) v -= 6;
            long c = M68K.Cyc - LineStartCyc;
            int hc = (int)(c * (H40 ? 210 : 171) / 488) & 0xFF;
            return (uint)(((v & 0xFF) << 8) | hc);
        }

        public static bool H40 { get { return (Reg[12] & 0x01) != 0; } }

        static void WriteControl(uint v)
        {
            if (pending)
            {
                code = (code & 3) | (int)((v >> 2) & 0x3C);
                addr = (addr & 0x3FFF) | ((v & 3) << 14);
                pending = false;
                if ((code & 0x20) != 0 && (Reg[1] & 0x10) != 0)
                {
                    int mode = Reg[23] >> 6;
                    if (mode == 2) fillPending = true;
                    else if (mode == 3) DmaCopy();
                    else Dma68k();
                }
                return;
            }
            if ((v & 0xC000) == 0x8000)
            {
                int r = (int)((v >> 8) & 0x1F);
                if (r < 24)
                {
                    Reg[r] = (byte)v;
                    if (r == 1 || r == 0) M68K.IrqLevel = IrqLine();
                }
                code &= 0x3C; // writing register clears code low bits on real HW (approx)
                return;
            }
            code = (code & 0x3C) | (int)(v >> 14);
            addr = (addr & 0xC000) | (v & 0x3FFF);
            pending = true;
        }

        static void WriteData(uint v)
        {
            pending = false;
            if (fillPending)
            {
                fillPending = false;
                WriteTarget(v);
                DmaFill(v);
                return;
            }
            WriteTarget(v);
        }

        static void WriteTarget(uint v)
        {
            switch (code & 0x0F)
            {
                case 1: // VRAM
                    {
                        uint a = addr & 0xFFFF;
                        if ((a & 1) != 0) v = ((v & 0xFF) << 8) | (v >> 8);
                        Vram[a & 0xFFFE] = (byte)(v >> 8);
                        Vram[(a & 0xFFFE) | 1] = (byte)v;
                        break;
                    }
                case 3: Cram[(addr >> 1) & 63] = (ushort)(v & 0xEEE); palDirty = true; break;
                case 5: { int i = (int)((addr >> 1) & 63); if (i < 40) Vsram[i] = (ushort)(v & 0x7FF); break; }
            }
            addr = (addr + Reg[15]) & 0xFFFF;
        }

        static uint ReadData()
        {
            pending = false;
            uint r = 0;
            switch (code & 0x0F)
            {
                case 0: r = (uint)((Vram[addr & 0xFFFE] << 8) | Vram[(addr & 0xFFFE) | 1]); break;
                case 8: r = Cram[(addr >> 1) & 63]; break;
                case 4: { int i = (int)((addr >> 1) & 63); r = i < 40 ? Vsram[i] : 0u; break; }
            }
            addr = (addr + Reg[15]) & 0xFFFF;
            return r;
        }

        static int DmaLen()
        {
            int len = Reg[19] | (Reg[20] << 8);
            return len == 0 ? 0x10000 : len;
        }

        static void Dma68k()
        {
            int len = DmaLen();
            uint src = (uint)((Reg[21] << 1) | (Reg[22] << 9) | ((Reg[23] & 0x7F) << 17));
            for (int i = 0; i < len; i++)
            {
                uint w = Bus.R16(src);
                src = (src & 0xFE0000) | ((src + 2) & 0x1FFFF);
                WriteTarget(w);
            }
            M68K.Cyc += len * 2;
            Reg[19] = Reg[20] = 0;
            Reg[21] = (byte)(src >> 1); Reg[22] = (byte)(src >> 9);
            Reg[23] = (byte)((Reg[23] & 0x80) | ((src >> 17) & 0x7F));
        }

        static void DmaFill(uint v)
        {
            int len = DmaLen();
            byte b = (byte)(v >> 8);
            for (int i = 0; i < len; i++)
            {
                switch (code & 0x0F)
                {
                    case 1: Vram[addr & 0xFFFF] = b; break;
                    case 3: Cram[(addr >> 1) & 63] = (ushort)(v & 0xEEE); palDirty = true; break;
                    case 5: { int k = (int)((addr >> 1) & 63); if (k < 40) Vsram[k] = (ushort)(v & 0x7FF); break; }
                }
                addr = (addr + Reg[15]) & 0xFFFF;
            }
            Reg[19] = Reg[20] = 0;
        }

        static void DmaCopy()
        {
            int len = DmaLen();
            uint src = (uint)(Reg[21] | (Reg[22] << 8));
            for (int i = 0; i < len; i++)
            {
                Vram[addr & 0xFFFF] = Vram[src & 0xFFFF];
                src = (src + 1) & 0xFFFF;
                addr = (addr + Reg[15]) & 0xFFFF;
            }
            Reg[19] = Reg[20] = 0;
            Reg[21] = (byte)src; Reg[22] = (byte)(src >> 8);
        }

        // ---------------- interrupts ----------------
        public static int IrqLine()
        {
            if (VIntPending && (Reg[1] & 0x20) != 0) return 6;
            if (HIntPending && (Reg[0] & 0x10) != 0) return 4;
            return 0;
        }

        public static void AckIrq(int lvl)
        {
            if (lvl == 6) VIntPending = false;
            else if (lvl == 4) HIntPending = false;
            M68K.IrqLevel = IrqLine();
        }

        // ---------------- frame timing ----------------
        public const int CyclesPerLine = 488;
        public const int LinesPerFrame = 262;

        // cycle (within a 488-cycle line) where active display ends and HBlank begins
        const int HBlankStart = 390;

        public static void RunFrame()
        {
            int active = (Reg[1] & 0x08) != 0 ? 240 : 224;
            ScreenHeight = active;
            ScreenWidth = H40 ? 320 : 256;
            for (Line = 0; Line < LinesPerFrame; Line++)
            {
                LineStartCyc = M68K.Cyc;
                InVBlank = Line >= active;
                if (Line == active)
                {
                    // VInt fires shortly after the start of the first VBlank line
                    M68K.Run(LineStartCyc + 16);
                    VIntPending = true;
                    M68K.IrqLevel = IrqLine();
                    Sound.Z80Irq();
                }
                // the line is displayed with the state left by the previous HBlank
                if (Line < active) RenderLine(Line);
                M68K.Run(LineStartCyc + HBlankStart);
                // HInt counter: decremented at the end of each active line, reloaded in VBlank
                if (Line <= active)
                {
                    if (--hcounter < 0)
                    {
                        hcounter = Reg[10];
                        HIntPending = true;
                        M68K.IrqLevel = IrqLine();
                    }
                }
                else hcounter = Reg[10];
                M68K.Run(LineStartCyc + CyclesPerLine);
                Sound.RunLine();
            }
            Sound.EndFrame();
        }

        // ---------------- rendering ----------------
        static int[] lineA = new int[512], lineB = new int[512], lineS = new int[512];

        static void UpdatePalette()
        {
            for (int i = 0; i < 64; i++)
            {
                int c = Cram[i];
                int r = (c >> 1) & 7, g = (c >> 5) & 7, b = (c >> 9) & 7;
                palRgb[i] = Rgb(r * 2, g * 2, b * 2);
                palRgb[i + 64] = Rgb(r, g, b);
                palRgb[i + 128] = Rgb(r + 7, g + 7, b + 7);
            }
            palDirty = false;
        }

        static readonly int[] lvl = { 0, 18, 36, 54, 72, 90, 108, 126, 144, 162, 180, 198, 216, 234, 255 };
        static int Rgb(int r, int g, int b)
        {
            return unchecked((int)0xFF000000) | (lvl[r] << 16) | (lvl[g] << 8) | lvl[b];
        }

        // pixel encoding in line buffers: bits0-5 color index (0 = transparent), bit 6 priority
        static void RenderPlane(int[] buf, int ntBase, bool planeA, int line, int width)
        {
            int hs = Reg[16] & 3, vs = (Reg[16] >> 4) & 3;
            int pw = hs == 0 ? 32 : hs == 1 ? 64 : 128;
            int ph = vs == 0 ? 32 : vs == 1 ? 64 : 128;
            if (hs == 2) pw = 32;
            if (vs == 2) ph = 32;
            int hsBase = (Reg[13] & 0x3F) << 10;
            int hmode = Reg[11] & 3;
            int hidx;
            if (hmode == 0) hidx = 0;
            else if (hmode == 2) hidx = (line & ~7) * 4;
            else if (hmode == 3) hidx = line * 4;
            else hidx = (line & 7) * 4;
            int ho = hsBase + hidx + (planeA ? 0 : 2);
            int hscroll = ((Vram[ho & 0xFFFF] << 8) | Vram[(ho + 1) & 0xFFFF]) & 0x3FF;
            bool colV = (Reg[11] & 4) != 0;
            int pxMaskW = pw * 8 - 1, pxMaskH = ph * 8 - 1;
            for (int x = 0; x < width; x++)
            {
                int vscroll;
                if (colV)
                {
                    int col = ((x) >> 4);
                    if (col > 19) col = 19;
                    vscroll = Vsram[col * 2 + (planeA ? 0 : 1)] & 0x3FF;
                }
                else vscroll = Vsram[planeA ? 0 : 1] & 0x3FF;
                int px = (x - hscroll) & pxMaskW;
                int py = (line + vscroll) & pxMaskH;
                int ntAddr = ntBase + (((py >> 3) * pw + (px >> 3)) << 1);
                int nt = (Vram[ntAddr & 0xFFFF] << 8) | Vram[(ntAddr + 1) & 0xFFFF];
                buf[x] = TilePixel(nt, px & 7, py & 7);
            }
        }

        static int TilePixel(int nt, int tx, int ty)
        {
            int tile = nt & 0x7FF;
            if ((nt & 0x0800) != 0) tx = 7 - tx;
            if ((nt & 0x1000) != 0) ty = 7 - ty;
            int a = (tile << 5) + (ty << 2) + (tx >> 1);
            int b = Vram[a & 0xFFFF];
            int c = (tx & 1) == 0 ? b >> 4 : b & 15;
            if (c == 0) return 0;
            return c | ((nt >> 9) & 0x30) | ((nt & 0x8000) != 0 ? 0x40 : 0);
        }

        static void RenderWindowLine(int[] buf, int line, int width, out int wStart, out int wEnd)
        {
            // determine window region on this line
            int wh = Reg[17], wv = Reg[18];
            int vcell = (wv & 0x1F) * 8;
            bool vin = (wv & 0x80) != 0 ? line >= vcell : line < vcell;
            wStart = 0; wEnd = 0;
            if (vin) { wStart = 0; wEnd = width; }
            else
            {
                int hcell = (wh & 0x1F) * 16;
                if (hcell == 0 && (wh & 0x80) == 0) return;
                if ((wh & 0x80) != 0) { wStart = Math.Min(hcell, width); wEnd = width; }
                else { wStart = 0; wEnd = Math.Min(hcell, width); }
                if (wStart >= wEnd) return;
            }
            int baseW = H40 ? (Reg[3] & 0x3C) << 10 : (Reg[3] & 0x3E) << 10;
            int pw = H40 ? 64 : 32;
            for (int x = wStart; x < wEnd; x++)
            {
                int ntAddr = baseW + (((line >> 3) * pw + (x >> 3)) << 1);
                int nt = (Vram[ntAddr & 0xFFFF] << 8) | Vram[(ntAddr + 1) & 0xFFFF];
                buf[x] = TilePixel(nt, x & 7, line & 7);
            }
        }

        static void RenderSprites(int line, int width)
        {
            for (int i = 0; i < width; i++) lineS[i] = 0;
            int sat = H40 ? (Reg[5] & 0x7E) << 9 : (Reg[5] & 0x7F) << 9;
            int maxSprites = H40 ? 80 : 64;
            int maxPerLine = H40 ? 20 : 16;
            int maxPixels = H40 ? 320 : 256;
            int idx = 0, count = 0, onLine = 0, pixels = 0;
            bool masked = false, prevOnLine = false;
            int y = line + 128;
            while (count < maxSprites)
            {
                int a = sat + idx * 8;
                int sy = ((Vram[a & 0xFFFF] << 8) | Vram[(a + 1) & 0xFFFF]) & 0x3FF;
                int sz = Vram[(a + 2) & 0xFFFF];
                int link = Vram[(a + 3) & 0xFFFF] & 0x7F;
                int hsz = ((sz >> 2) & 3) + 1, vsz = (sz & 3) + 1;
                if (y >= sy && y < sy + vsz * 8)
                {
                    int attr = (Vram[(a + 4) & 0xFFFF] << 8) | Vram[(a + 5) & 0xFFFF];
                    int sx = ((Vram[(a + 6) & 0xFFFF] << 8) | Vram[(a + 7) & 0xFFFF]) & 0x1FF;
                    onLine++;
                    if (onLine > maxPerLine) { spriteOverflow = true; break; }
                    if (sx == 0) { if (prevOnLine) masked = true; }
                    else prevOnLine = true;
                    int row = y - sy;
                    if ((attr & 0x1000) != 0) row = vsz * 8 - 1 - row;
                    for (int cx = 0; cx < hsz * 8; cx++)
                    {
                        if (pixels >= maxPixels) break;
                        pixels++;
                        if (masked) continue;
                        int xx = sx - 128 + cx;
                        if (xx < 0 || xx >= width) continue;
                        int col = cx;
                        if ((attr & 0x0800) != 0) col = hsz * 8 - 1 - cx;
                        int tile = (attr & 0x7FF) + (col >> 3) * vsz + (row >> 3);
                        int ta = ((tile & 0x7FF) << 5) + ((row & 7) << 2) + ((col & 7) >> 1);
                        int b = Vram[ta & 0xFFFF];
                        int c = (col & 1) == 0 ? b >> 4 : b & 15;
                        if (c == 0) continue;
                        if (lineS[xx] != 0) { spriteCollision = true; continue; }
                        lineS[xx] = c | ((attr >> 9) & 0x30) | ((attr & 0x8000) != 0 ? 0x40 : 0);
                    }
                    if (pixels >= maxPixels) { spriteOverflow = true; break; }
                }
                count++;
                if (link == 0 || link >= maxSprites) break;
                idx = link;
            }
        }

        static void RenderLine(int line)
        {
            if (palDirty) UpdatePalette();
            int width = H40 ? 320 : 256;
            int off = line * W;
            int bg = palRgb[Reg[7] & 63];
            if ((Reg[1] & 0x40) == 0)
            {
                for (int x = 0; x < W; x++) Frame[off + x] = bg;
                return;
            }
            int ntA = (Reg[2] & 0x38) << 10;
            int ntB = (Reg[4] & 7) << 13;
            RenderPlane(lineB, ntB, false, line, width);
            RenderPlane(lineA, ntA, true, line, width);
            int ws, we;
            RenderWindowLine(lineA, line, width, out ws, out we);
            RenderSprites(line, width);
            bool sh = (Reg[12] & 0x08) != 0;
            int bgi = Reg[7] & 63;
            for (int x = 0; x < width; x++)
            {
                int a = lineA[x], b = lineB[x], s = lineS[x];
                int pix;
                // priority: S hi > A hi > B hi > S lo > A lo > B lo > bg
                if ((s & 0x40) != 0 && (s & 15) != 0) pix = s;
                else if ((a & 0x40) != 0 && (a & 15) != 0) pix = a;
                else if ((b & 0x40) != 0 && (b & 15) != 0) pix = b;
                else if ((s & 15) != 0) pix = s;
                else if ((a & 15) != 0) pix = a;
                else if ((b & 15) != 0) pix = b;
                else pix = bgi;
                int ci = pix & 63;
                if (sh)
                {
                    bool hiPri = ((a | b) & 0x40) != 0;
                    int mode = hiPri ? 0 : 1; // 0 normal, 1 shadow, 2 highlight
                    if ((s & 15) != 0 && pix == s)
                    {
                        int sc = s & 63;
                        if (sc == 0x3E) { mode = mode == 1 ? 0 : 2; ci = OpaqueBelow(a, b, bgi); }
                        else if (sc == 0x3F) { mode = 1; ci = OpaqueBelow(a, b, bgi); }
                        else if ((s & 0x40) != 0 || (s & 15) == 14) mode = 0;
                    }
                    Frame[off + x] = palRgb[ci + mode * 64];
                }
                else Frame[off + x] = palRgb[ci];
            }
            for (int x = width; x < W; x++) Frame[off + x] = bg;
        }

        static int OpaqueBelow(int a, int b, int bgi)
        {
            if ((a & 0x40) != 0 && (a & 15) != 0) return a & 63;
            if ((b & 0x40) != 0 && (b & 15) != 0) return b & 63;
            if ((a & 15) != 0) return a & 63;
            if ((b & 15) != 0) return b & 63;
            return bgi;
        }
    }
}
