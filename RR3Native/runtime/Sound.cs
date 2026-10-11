// Sound subsystem: Z80 scheduling, YM2612 + SN76489 mixing and waveOut audio output
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace RR3
{
    static partial class Sound
    {
        public static byte[] Z80Ram = new byte[0x2000];
        public static Ym2612 Ym = new Ym2612();
        public static bool Enabled = true;
        public static volatile float Gain = 0.4f;
        public static double RateAdjust;
        public static void ApplyVolume() { Gain = Settings.SoundOn ? Settings.Volume / 100f * 0.5f : 0f; }
        public static void Reset()
        {
            Array.Clear(Z80Ram, 0, Z80Ram.Length);
            Ym.Reset(); Psg.Reset(); Z80.Reset();
        }

        // Z80 clock = 3579545 Hz, YM sample rate = 7670453/144 = 53267 Hz
        const double Z80PerLine = 3579545.0 / (59.922 * 262);
        const double YmRate = 7670453.0 / 144.0;
        const double YmSamplesPerZ80 = YmRate / 3579545.0;
        static double zTarget;
        static double ymDone; // YM samples rendered (in Z80-clock timeline)
        static int[] ymBuf = new int[8192 * 2];
        static int ymCount;

        public static void Init()
        {
            Z80.Init();
            Ym.Reset();
            Psg.Reset();
        }

        // ---------------- 68000 side ----------------
        public static uint Z80BusRead8(uint a)
        {
            if (a < 0x4000) return Z80Ram[a & 0x1FFF];
            if (a < 0x6000) return (uint)YmReadStatus(Z80.Cyc);
            return 0xFF;
        }
        public static void Z80BusWrite8(uint a, uint v)
        {
            if (a < 0x4000) { Z80Ram[a & 0x1FFF] = (byte)v; return; }
            if (a < 0x6000) { YmWrite((int)(a & 3), (int)v, Z80.Cyc); return; }
            if (a < 0x6100) { Z80.Bank = ((Z80.Bank >> 1) | (((int)v & 1) << 8)) & 0x1FF; return; }
        }
        public static void PsgWrite(uint v) { PsgWriteZ((int)v, Z80.Cyc); }
        public static void SyncZ80() { }
        public static void Z80ResetRelease() { Z80.Reset(); }
        public static void YmReset() { }
        public static void Z80Irq() { if (!Bus.Z80Reset && !Bus.Z80BusReq) Z80.Interrupt(); }

        // ---------------- timeline ----------------
        public static void RunLine()
        {
            zTarget += Z80PerLine;
            long t = (long)zTarget;
            if (!Bus.Z80Reset && !Bus.Z80BusReq) Z80.Run(t);
            else Z80.Cyc = t;
        }

        static void RenderTo(long cyc)
        {
            double target = cyc * YmSamplesPerZ80;
            int n = (int)(target - ymDone);
            if (n <= 0) return;
            if (ymCount + n > ymBuf.Length / 2) n = ymBuf.Length / 2 - ymCount;
            if (n <= 0) { ymDone = target; return; }
            if (Enabled) Ym.Render(ymBuf, ymCount * 2, n);
            else Ym.TimersOnly(n);
            Psg.Render(ymBuf, ymCount * 2, n);
            ymCount += n;
            ymDone += n;
        }

        public static int YmReadStatus(long cyc)
        {
            RenderTo(cyc);
            return Ym.Status & 3;
        }

        public static void YmWrite(int port, int v, long cyc)
        {
            RenderTo(cyc);
            Ym.Write(port, v);
        }

        public static void PsgWriteZ(int v, long cyc)
        {
            RenderTo(cyc);
            Psg.Write(v);
        }

        // ---------------- frame output ----------------
        static double resPos;
        static int lastL, lastR;
        static short[] outBuf = new short[4096 * 2];

        public static void EndFrame()
        {
            RenderTo(Z80.Cyc);
            // resample ymCount samples at YmRate -> 44100
            double step = YmRate / 44100.0 * (1.0 + RateAdjust);
            int o = 0;
            while (resPos < ymCount && o < outBuf.Length / 2)
            {
                int i = (int)resPos;
                double fr = resPos - i;
                int l0 = i == 0 ? lastL : ymBuf[(i - 1) * 2], r0 = i == 0 ? lastR : ymBuf[(i - 1) * 2 + 1];
                int l1 = ymBuf[i * 2], r1 = ymBuf[i * 2 + 1];
                double l = l0 + (l1 - l0) * fr, r = r0 + (r1 - r0) * fr;
                double g = Gain;
                outBuf[o * 2] = Clip(l * g);
                outBuf[o * 2 + 1] = Clip(r * g);
                o++;
                resPos += step;
            }
            resPos -= ymCount;
            if (ymCount > 0) { lastL = ymBuf[(ymCount - 1) * 2]; lastR = ymBuf[(ymCount - 1) * 2 + 1]; }
            ymCount = 0;
            if (Enabled) Audio.Queue(outBuf, o);
            if (Capture != null) for (int k = 0; k < o * 2; k++) Capture.Add(outBuf[k]);
        }

        public static List<short> Capture;

        public static void SaveWav(string path)
        {
            using (var f = new System.IO.BinaryWriter(System.IO.File.Create(path)))
            {
                int n = Capture.Count * 2;
                f.Write(0x46464952); f.Write(36 + n); f.Write(0x45564157); f.Write(0x20746D66); f.Write(16);
                f.Write((short)1); f.Write((short)2); f.Write(44100); f.Write(44100 * 4); f.Write((short)4); f.Write((short)16);
                f.Write(0x61746164); f.Write(n);
                foreach (var v in Capture) f.Write(v);
            }
        }

        static short Clip(double v)
        {
            if (v > 32767) return 32767;
            if (v < -32768) return -32768;
            return (short)v;
        }

        public static void Start() { if (Enabled) Audio.Open(44100); }
        public static void Shutdown() { Audio.Close(); }
    }

    // SN76489 PSG (clock 3579545 Hz / 16)
    static class Psg
    {
        static int[] tone = new int[4], vol = new int[4], cnt = new int[4];
        static int[] outp = new int[4];
        static int latch, lfsr = 0x8000;
        static double acc;
        static readonly int[] volTab = new int[16];
        const double TicksPerSample = (3579545.0 / 16.0) / (7670453.0 / 144.0);

        static Psg()
        {
            double v = 2800;
            for (int i = 0; i < 15; i++) { volTab[i] = (int)v; v /= 1.258925; }
            volTab[15] = 0;
        }

        public static void Reset()
        {
            for (int i = 0; i < 4; i++) { tone[i] = 0; vol[i] = 15; cnt[i] = 0; outp[i] = 1; }
            lfsr = 0x8000;
        }

        public static void Write(int v)
        {
            if ((v & 0x80) != 0)
            {
                latch = (v >> 4) & 7;
                int c = latch >> 1;
                if ((latch & 1) != 0) vol[c] = v & 15;
                else if (c == 3) { tone[3] = v & 7; lfsr = 0x8000; }
                else tone[c] = (tone[c] & 0x3F0) | (v & 15);
            }
            else
            {
                int c = latch >> 1;
                if ((latch & 1) != 0) vol[c] = v & 15;
                else if (c == 3) { tone[3] = v & 7; lfsr = 0x8000; }
                else tone[c] = (tone[c] & 0xF) | ((v & 0x3F) << 4);
            }
        }

        public static void Render(int[] buf, int off, int n)
        {
            for (int s = 0; s < n; s++)
            {
                acc += TicksPerSample;
                int ticks = (int)acc; acc -= ticks;
                for (int t = 0; t < ticks; t++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        if (--cnt[c] <= 0) { cnt[c] = tone[c] == 0 ? 1 : tone[c]; outp[c] ^= 1; if (tone[c] <= 1) outp[c] = 1; }
                    }
                    if (--cnt[3] <= 0)
                    {
                        int nr = tone[3] & 3;
                        cnt[3] = nr == 0 ? 0x10 : nr == 1 ? 0x20 : nr == 2 ? 0x40 : Math.Max(1, tone[2]);
                        cnt[3] *= 2; // noise shifts on every other edge
                        int fb = (tone[3] & 4) != 0 ? ((lfsr & 1) ^ ((lfsr >> 3) & 1)) : (lfsr & 1);
                        lfsr = (lfsr >> 1) | (fb << 15);
                        outp[3] = lfsr & 1;
                    }
                }
                int sum = 0;
                for (int c = 0; c < 4; c++) if (outp[c] != 0) sum += volTab[vol[c]];
                buf[off + s * 2] += sum;
                buf[off + s * 2 + 1] += sum;
            }
        }
    }

    // waveOut based streaming audio
    static class Audio
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }
        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int dev, ref WAVEFORMATEX f, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

        const int NBUF = 8, BUFSAMPLES = 4096;
        public static int Drops, Queued;
        static IntPtr handle = IntPtr.Zero;
        static IntPtr[] hdrs = new IntPtr[NBUF];
        static IntPtr[] data = new IntPtr[NBUF];
        static int next;
        public static bool IsOpen { get { return handle != IntPtr.Zero; } }
        public static bool WasOpen;
        const uint WHDR_DONE = 1;

        public static void Open(int rate)
        {
            var f = new WAVEFORMATEX { wFormatTag = 1, nChannels = 2, nSamplesPerSec = (uint)rate, wBitsPerSample = 16, nBlockAlign = 4, nAvgBytesPerSec = (uint)rate * 4 };
            if (waveOutOpen(out handle, -1, ref f, IntPtr.Zero, IntPtr.Zero, 0) != 0) { handle = IntPtr.Zero; return; }
            WasOpen = true;
            int hs = Marshal.SizeOf(typeof(WAVEHDR));
            for (int i = 0; i < NBUF; i++)
            {
                data[i] = Marshal.AllocHGlobal(BUFSAMPLES * 4);
                hdrs[i] = Marshal.AllocHGlobal(hs);
                var h = new WAVEHDR { lpData = data[i], dwBufferLength = 0, dwFlags = WHDR_DONE };
                Marshal.StructureToPtr(h, hdrs[i], false);
            }
        }

        static bool Done(int i) { return ((uint)Marshal.ReadInt32(hdrs[i], IntPtr.Size * 2 + 8) & WHDR_DONE) != 0; }

        // number of buffers still playing
        public static int Pending()
        {
            if (!IsOpen) return 0;
            int n = 0;
            for (int i = 0; i < NBUF; i++) if (!Done(i)) n++;
            return n;
        }

        public static void Queue(short[] buf, int samples)
        {
            if (!IsOpen || samples == 0) return;
            int i = next;
            if (!Done(i)) { Drops++; return; } // overrun: drop
            Queued++;
            int hs = Marshal.SizeOf(typeof(WAVEHDR));
            waveOutUnprepareHeader(handle, hdrs[i], hs);
            Marshal.Copy(buf, 0, data[i], samples * 2);
            var h = new WAVEHDR { lpData = data[i], dwBufferLength = (uint)(samples * 4) };
            Marshal.StructureToPtr(h, hdrs[i], false);
            waveOutPrepareHeader(handle, hdrs[i], hs);
            waveOutWrite(handle, hdrs[i], hs);
            next = (next + 1) % NBUF;
        }

        public static void Close()
        {
            if (!IsOpen) return;
            waveOutReset(handle);
            int hs = Marshal.SizeOf(typeof(WAVEHDR));
            for (int i = 0; i < NBUF; i++) waveOutUnprepareHeader(handle, hdrs[i], hs);
            waveOutClose(handle);
            handle = IntPtr.Zero;
        }
    }
}
