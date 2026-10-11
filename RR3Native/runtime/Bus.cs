// Mega Drive memory map as seen by the 68000
using System;

namespace RR3
{
    static class Bus
    {
        public static byte[] Rom;
        public static byte[] Ram = new byte[0x10000];
        public static byte[] Sram = new byte[0x10000];

        // Z80 bus control
        public static bool Z80BusReq;   // 68k requested the bus
        public static bool Z80Reset = true; // reset line asserted

        public static void Init(byte[] rom) { Rom = rom; }

        public static uint R8(uint a)
        {
            a &= 0xFFFFFF;
            if (a < 0x400000) return a < (uint)Rom.Length ? Rom[a] : 0xFFu;
            if (a >= 0xE00000) return Ram[a & 0xFFFF];
            return Io8(a);
        }

        public static uint R16(uint a)
        {
            a &= 0xFFFFFE;
            if (a < 0x400000) return a < (uint)Rom.Length ? (uint)((Rom[a] << 8) | Rom[a + 1]) : 0xFFFFu;
            if (a >= 0xE00000) { uint o = a & 0xFFFF; return (uint)((Ram[o] << 8) | Ram[o + 1]); }
            return Io16(a);
        }

        public static uint R32(uint a)
        {
            a &= 0xFFFFFE;
            if (a >= 0xE00000 && (a & 0xFFFF) <= 0xFFFC)
            {
                uint o = a & 0xFFFF;
                return (uint)((Ram[o] << 24) | (Ram[o + 1] << 16) | (Ram[o + 2] << 8) | Ram[o + 3]);
            }
            if (a + 3 < (uint)Rom.Length)
                return (uint)((Rom[a] << 24) | (Rom[a + 1] << 16) | (Rom[a + 2] << 8) | Rom[a + 3]);
            return (R16(a) << 16) | R16(a + 2);
        }

        public static void W8(uint a, uint v)
        {
            a &= 0xFFFFFF;
            if (a >= 0xE00000) { Ram[a & 0xFFFF] = (byte)v; return; }
            IoW8(a, v & 0xFF);
        }

        public static void W16(uint a, uint v)
        {
            a &= 0xFFFFFE;
            if (a >= 0xE00000) { uint o = a & 0xFFFF; Ram[o] = (byte)(v >> 8); Ram[o + 1] = (byte)v; return; }
            IoW16(a, v & 0xFFFF);
        }

        public static void W32(uint a, uint v)
        {
            a &= 0xFFFFFE;
            if (a >= 0xE00000 && (a & 0xFFFF) <= 0xFFFC)
            {
                uint o = a & 0xFFFF;
                Ram[o] = (byte)(v >> 24); Ram[o + 1] = (byte)(v >> 16); Ram[o + 2] = (byte)(v >> 8); Ram[o + 3] = (byte)v;
                return;
            }
            W16(a, v >> 16); W16(a + 2, v & 0xFFFF);
        }

        // ---------- I/O ----------
        static uint Io8(uint a)
        {
            if (a >= 0xC00000 && a < 0xC00020)
            {
                uint w = Vdp.ReadPort(a & 0x1E);
                return (a & 1) != 0 ? w & 0xFF : w >> 8;
            }
            if (a >= 0xA00000 && a < 0xA10000)
            {
                return Sound.Z80BusRead8(a & 0x7FFF);
            }
            if (a >= 0xA10000 && a < 0xA10020) return Pad.ReadIo(a & 0x1F);
            if (a == 0xA11100 || a == 0xA11101)
            {
                // bit0 = 0 when bus is granted to the 68000
                uint busy = (Z80BusReq && !Z80Reset) ? 0u : 1u;
                return (a & 1) != 0 ? 0u : busy;
            }
            return 0xFF;
        }

        static uint Io16(uint a)
        {
            if (a >= 0xC00000 && a < 0xC00020) return Vdp.ReadPort(a & 0x1E);
            if (a >= 0xA00000 && a < 0xA10000)
            {
                uint b = Sound.Z80BusRead8(a & 0x7FFF);
                return (b << 8) | b;
            }
            if (a == 0xA11100) return Io8(a) << 8;
            if (a >= 0xA10000 && a < 0xA10020) { uint b = Pad.ReadIo((a + 1) & 0x1F); return (b << 8) | b; }
            return 0xFFFF;
        }

        static void IoW8(uint a, uint v)
        {
            if (a >= 0xC00000 && a < 0xC00020)
            {
                if (a >= 0xC00010 && a < 0xC00018) { if ((a & 1) != 0) Sound.PsgWrite(v); return; }
                Vdp.WritePort(a & 0x1E, (v << 8) | v);
                return;
            }
            if (a >= 0xA00000 && a < 0xA10000) { Sound.Z80BusWrite8(a & 0x7FFF, v); return; }
            if (a >= 0xA10000 && a < 0xA10020) { Pad.WriteIo(a & 0x1F, v); return; }
            if (a == 0xA11100) { SetBusReq((v & 1) != 0); return; }
            if (a == 0xA11200) { SetReset((v & 1) == 0); return; }
        }

        static void IoW16(uint a, uint v)
        {
            if (a >= 0xC00000 && a < 0xC00020)
            {
                if (a >= 0xC00010 && a < 0xC00018) { Sound.PsgWrite(v & 0xFF); return; }
                Vdp.WritePort(a & 0x1E, v);
                return;
            }
            if (a >= 0xA00000 && a < 0xA10000) { Sound.Z80BusWrite8(a & 0x7FFF, v >> 8); return; }
            if (a >= 0xA10000 && a < 0xA10020) { Pad.WriteIo((a + 1) & 0x1F, v & 0xFF); return; }
            if (a == 0xA11100) { SetBusReq((v & 0x100) != 0); return; }
            if (a == 0xA11200) { SetReset((v & 0x100) == 0); return; }
        }

        static void SetBusReq(bool req)
        {
            if (Z80BusReq && !req) Sound.SyncZ80();
            if (!Z80BusReq && req) Sound.SyncZ80();
            Z80BusReq = req;
        }

        static void SetReset(bool reset)
        {
            Sound.SyncZ80();
            if (Z80Reset && !reset) Sound.Z80ResetRelease();
            if (reset) Sound.YmReset();
            Z80Reset = reset;
        }
    }
}
