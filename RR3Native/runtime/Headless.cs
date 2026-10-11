// Headless test runner: RR3.exe --frames N [--shot every] [--input "frame:buttons,frame:buttons"] [--out dir]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace RR3
{
    static class Headless
    {
        public static int Run(string[] args, int frames, string dir)
        {
            int shotEvery = 0;
            string outDir = Path.Combine(dir, "shots");
            var input = new SortedDictionary<int, int>();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--shot") shotEvery = int.Parse(args[i + 1]);
                if (args[i] == "--out") outDir = args[i + 1];
                if (args[i] == "--input")
                    foreach (var p in args[i + 1].Split(','))
                    {
                        var kv = p.Split(':');
                        input[int.Parse(kv[0])] = ParseButtons(kv[1]);
                    }
            }
            Directory.CreateDirectory(outDir);
            bool wav = Array.IndexOf(args, "--wav") >= 0;
            int rnd = -1;
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--random") rnd = int.Parse(args[i + 1]);
            var rng = new Random(rnd);
            if (wav) Sound.Capture = new List<short>();
            var sw = Stopwatch.StartNew();
            int f = 0;
            try
            {
                for (f = 0; f < frames; f++)
                {
                    if (input.ContainsKey(f)) Pad.Buttons1 = input[f];
                    if (rnd >= 100)
                    {
                        // menu exploration: short taps of single buttons
                        if (f % 20 == 0) { int[] bt = { Pad.Up, Pad.Down, Pad.Left, Pad.Right, Pad.A, Pad.B, Pad.C, Pad.Start, Pad.Start, Pad.Down, Pad.Right };
                            Pad.Buttons1 = bt[rng.Next(bt.Length)]; }
                        else if (f % 20 == 6) Pad.Buttons1 = 0;
                    }
                    else if (rnd >= 0 && f % 12 == 0)
                    {
                        // random play: mostly hold throttle, steer and attack, sometimes menus
                        int b = 0;
                        int r = rng.Next(100);
                        if (r < 60) b |= Pad.B;
                        if (rng.Next(4) == 0) b |= Pad.Left; else if (rng.Next(3) == 0) b |= Pad.Right;
                        if (rng.Next(8) == 0) b |= Pad.A;
                        if (rng.Next(10) == 0) b |= Pad.C;
                        if (rng.Next(12) == 0) b |= Pad.Up;
                        if (rng.Next(12) == 0) b |= Pad.Down;
                        if (rng.Next(25) == 0) b = Pad.Start;
                        Pad.Buttons1 = b;
                    }
                    Vdp.RunFrame();
                    if (shotEvery > 0 && (f + 1) % shotEvery == 0) Save(Path.Combine(outDir, string.Format("f{0:D5}.png", f + 1)));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("frame {0}: {1}", f, ex.Message);
                Console.WriteLine(ex.StackTrace);
                DumpState();
                Save(Path.Combine(outDir, "crash.png"));
                return 2;
            }
            Console.WriteLine("{0} frames in {1} ms ({2:F1} fps)", frames, sw.ElapsedMilliseconds, frames * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds));
            Console.WriteLine("DAC writes: {0}", Ym2612.DacWrites);
            DumpState();
            Save(Path.Combine(outDir, "last.png"));
            if (wav) Sound.SaveWav(Path.Combine(outDir, "audio.wav"));
            return 0;
        }

        static int ParseButtons(string s)
        {
            int b = 0;
            foreach (char c in s.ToUpperInvariant())
            {
                switch (c)
                {
                    case 'U': b |= Pad.Up; break;
                    case 'D': b |= Pad.Down; break;
                    case 'L': b |= Pad.Left; break;
                    case 'R': b |= Pad.Right; break;
                    case 'A': b |= Pad.A; break;
                    case 'B': b |= Pad.B; break;
                    case 'C': b |= Pad.C; break;
                    case 'S': b |= Pad.Start; break;
                }
            }
            return b;
        }

        public static void DumpState()
        {
            Console.WriteLine("PC={0:X6} SR={1:X4} D0-7: {2:X8} {3:X8} {4:X8} {5:X8} {6:X8} {7:X8} {8:X8} {9:X8}",
                M68K.PC, M68K.GetSR(), M68K.D0, M68K.D1, M68K.D2, M68K.D3, M68K.D4, M68K.D5, M68K.D6, M68K.D7);
            Console.WriteLine("A0-7: {0:X8} {1:X8} {2:X8} {3:X8} {4:X8} {5:X8} {6:X8} {7:X8}",
                M68K.A0, M68K.A1, M68K.A2, M68K.A3, M68K.A4, M68K.A5, M68K.A6, M68K.A7);
            Console.Write("VDP regs:");
            for (int i = 0; i < 24; i++) Console.Write(" {0:X2}", Vdp.Reg[i]);
            Console.WriteLine();
        }

        public static void Save(string path)
        {
            int w = Vdp.ScreenWidth, h = Vdp.ScreenHeight;
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb))
            {
                var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                for (int y = 0; y < h; y++) Marshal.Copy(Vdp.Frame, y * Vdp.W, d.Scan0 + y * d.Stride, w);
                bmp.UnlockBits(d);
                bmp.Save(path, ImageFormat.Png);
            }
        }
    }
}
