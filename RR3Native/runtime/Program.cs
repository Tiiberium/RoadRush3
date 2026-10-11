// Native host: window/fullscreen, OpenGL presentation, pause menu, input, frame pacing
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace RR3
{
    class GameForm : Form
    {
        public volatile bool Running = true;
        public readonly PauseMenu Pause = new PauseMenu();
        public volatile bool MenuOpen;
        public volatile bool ResetRequested;

        // overlay bitmap shared with the render thread
        public readonly object OverlaySync = new object();
        public int[] OverlayPixels; public int OverlayW, OverlayH, OverlayVersion; public bool OverlayVisible;
        Bitmap overlayBmp;
        System.Windows.Forms.Timer uiTimer;

        // output (render) size; menu coordinates live in this space
        public volatile int OutW, OutH, ClientW, ClientH;
        public bool Offscreen; public int OffW, OffH;

        // GDI fallback when OpenGL is unavailable
        public bool GdiFallback;
        public Bitmap GdiFrame = new Bitmap(Vdp.W, Vdp.H, PixelFormat.Format32bppRgb);

        public GameForm()
        {
            Text = "Road Rash 3";
            BackColor = Color.Black;
            KeyPreview = true;
            DoubleBuffered = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Pause.OnResume = () => CloseMenu();
            Pause.OnQuit = () => Close();
            Pause.OnReset = () => { ResetRequested = true; CloseMenu(); Pause.Toast("Игра перезапущена"); };
            Pause.OnVideoChanged = () => ApplyVideo();
            Pause.OnSoundChanged = () => Sound.ApplyVolume();
            uiTimer = new System.Windows.Forms.Timer { Interval = 15 };
            uiTimer.Tick += (s, e) => RefreshOverlay();
            uiTimer.Start();
        }

        public void ApplyVideo()
        {
            if (Offscreen)
            {
                ClientSize = new Size(320, 240);
                OutW = OffW; OutH = OffH; ClientW = 320; ClientH = 240;
                Pause.Dirty = true;
                return;
            }
            var scr = IsHandleCreated ? Screen.FromHandle(Handle) : Screen.PrimaryScreen;
            if (Settings.Fullscreen)
            {
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Normal;
                Bounds = scr.Bounds;
            }
            else
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                WindowState = FormWindowState.Normal;
                int w = 1280, h = 720;
                if (Settings.ResIndex >= 0) { w = Settings.Resolutions[Settings.ResIndex][0]; h = Settings.Resolutions[Settings.ResIndex][1]; }
                var wa = scr.WorkingArea;
                // keep the window (with frame) inside the work area
                int fw = Width - ClientSize.Width, fh = Height - ClientSize.Height;
                if (w + fw > wa.Width || h + fh > wa.Height)
                {
                    float k = Math.Min((wa.Width - fw) / (float)w, (wa.Height - fh) / (float)h);
                    w = (int)(w * k); h = (int)(h * k);
                }
                ClientSize = new Size(w, h);
                Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);
            }
            UpdateSizes();
        }

        void UpdateSizes()
        {
            if (Offscreen) return;
            ClientW = Math.Max(1, ClientSize.Width); ClientH = Math.Max(1, ClientSize.Height);
            if (Settings.Fullscreen && Settings.ResIndex >= 0)
            {
                OutW = Settings.Resolutions[Settings.ResIndex][0]; OutH = Settings.Resolutions[Settings.ResIndex][1];
            }
            else { OutW = ClientW; OutH = ClientH; }
            Pause.Dirty = true;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateSizes(); if (GdiFallback) Invalidate(); }

        public void OpenMenu()
        {
            if (MenuOpen) return;
            Input.Clear();
            Pause.Show();
            MenuOpen = true;
        }

        public void CloseMenu()
        {
            if (!MenuOpen) return;
            Pause.Hide();
            Input.Clear();
            MenuOpen = false;
        }

        bool toastWasActive;
        void RefreshOverlay()
        {
            int w = OutW, h = OutH;
            if (w <= 0 || h <= 0) return;
            bool visible = Pause.HasContent;
            bool toastNow = Pause.ToastActive;
            bool toastChanged = toastNow != toastWasActive;
            toastWasActive = toastNow;
            if (!Pause.Dirty && visible == OverlayVisible && !toastChanged) return;
            Pause.Dirty = false;
            if (!visible) { lock (OverlaySync) { OverlayVisible = false; OverlayVersion++; } if (GdiFallback) Invalidate(); return; }
            if (overlayBmp == null || overlayBmp.Width != w || overlayBmp.Height != h)
            {
                if (overlayBmp != null) overlayBmp.Dispose();
                overlayBmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            }
            using (var g = Graphics.FromImage(overlayBmp)) Pause.Draw(g, w, h);
            var d = overlayBmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            lock (OverlaySync)
            {
                if (OverlayPixels == null || OverlayPixels.Length != w * h) OverlayPixels = new int[w * h];
                Marshal.Copy(d.Scan0, OverlayPixels, 0, w * h);
                OverlayW = w; OverlayH = h; OverlayVisible = true; OverlayVersion++;
            }
            overlayBmp.UnlockBits(d);
            if (GdiFallback) Invalidate();
        }

        // ---------------- keyboard ----------------
        public void HandleKey(Keys k, bool down)
        {
            Keys code = k & Keys.KeyCode;
            if (!down) { Input.KeyUp(code); return; }
            if (code == Keys.F11 || (code == Keys.Enter && (k & Keys.Alt) != 0))
            {
                Settings.Fullscreen = !Settings.Fullscreen; ApplyVideo(); Settings.Save(); return;
            }
            if (code == Keys.F12) { Program.ShotRequested = true; return; }
            if (MenuOpen) { Pause.OnKey(code); return; }
            if (code == Keys.Escape || code == Keys.Pause) { OpenMenu(); return; }
            Input.KeyDown(code);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // WM_KEYDOWN / WM_SYSKEYDOWN: take every key so arrows, Tab, Enter, Alt reach the game
            if (msg.Msg == 0x100 || msg.Msg == 0x104)
            {
                if ((keyData & Keys.KeyCode) == Keys.F4 && (keyData & Keys.Alt) != 0) return base.ProcessCmdKey(ref msg, keyData);
                bool repeat = ((long)msg.LParam & 0x40000000) != 0;
                if (!repeat || MenuOpen) HandleKey(keyData, true);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyUp(KeyEventArgs e) { HandleKey(e.KeyCode, false); e.Handled = true; }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x105) { HandleKey((Keys)(int)m.WParam, false); return; } // WM_SYSKEYUP
            if (m.Msg == 0x0112 && ((int)m.WParam & 0xFFF0) == 0xF100) return;  // SC_KEYMENU: no Alt menu
            base.WndProc(ref m);
        }

        protected override void OnDeactivate(EventArgs e) { Input.Clear(); base.OnDeactivate(e); }

        // ---------------- mouse ----------------
        Point ToOut(Point p)
        {
            if (ClientW == OutW && ClientH == OutH) return p;
            float s = Math.Min(ClientW / (float)OutW, ClientH / (float)OutH);
            float ox = (ClientW - OutW * s) / 2, oy = (ClientH - OutH * s) / 2;
            return new Point((int)((p.X - ox) / s), (int)((p.Y - oy) / s));
        }
        protected override void OnMouseMove(MouseEventArgs e) { if (MenuOpen) { var p = ToOut(e.Location); Pause.OnMouseMove(p.X, p.Y); } base.OnMouseMove(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (MenuOpen) { var p = ToOut(e.Location); Pause.OnMouseClick(p.X, p.Y, e.Button == MouseButtons.Right); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseWheel(MouseEventArgs e) { if (MenuOpen) Pause.OnWheel(e.Delta); base.OnMouseWheel(e); }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (!MenuOpen && e.Button == MouseButtons.Left) { Settings.Fullscreen = !Settings.Fullscreen; ApplyVideo(); Settings.Save(); }
            base.OnMouseDoubleClick(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e) { Settings.Save(); base.OnFormClosing(e); }
        protected override void OnFormClosed(FormClosedEventArgs e) { Running = false; base.OnFormClosed(e); }

        // ---------------- GDI fallback ----------------
        protected override void OnPaint(PaintEventArgs e)
        {
            if (!GdiFallback) return;
            var g = e.Graphics;
            g.Clear(Color.Black);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            int sw = Vdp.ScreenWidth, sh = Vdp.ScreenHeight;
            int cw = ClientSize.Width, ch = ClientSize.Height;
            float dh = ch, dw = ch * 4f / 3f;
            if (dw > cw) { dw = cw; dh = cw * 3f / 4f; }
            lock (GdiFrame) g.DrawImage(GdiFrame, new RectangleF((cw - dw) / 2, (ch - dh) / 2, dw, dh), new RectangleF(0, 0, sw, sh), GraphicsUnit.Pixel);
            if (overlayBmp != null && OverlayVisible) g.DrawImage(overlayBmp, 0, 0, cw, ch);
        }
    }

    static class Program
    {
        static GameForm form;
        public static System.Text.StringBuilder PaceLog = new System.Text.StringBuilder();
        public static volatile bool ShotRequested;
        static string baseDir;
        static int autoShot = -1, autoExit = -1;
        static HashSet<int> autoShots = new HashSet<int>();
        static string autoShotName = "autoshot.png";
        static string wavPath;
        static SortedDictionary<int, List<string>> keyScript = new SortedDictionary<int, List<string>>();

        public static void InitMachine(byte[] rom)
        {
            Bus.Init(rom);
            M68K.Init();
            Vdp.Reset();
            Sound.Init();
            M68K.Reset();
        }

        public static void ResetMachine()
        {
            Array.Clear(Bus.Ram, 0, Bus.Ram.Length);
            Vdp.HardReset();
            Sound.Reset();
            Bus.Z80BusReq = false; Bus.Z80Reset = true;
            M68K.D0 = M68K.D1 = M68K.D2 = M68K.D3 = M68K.D4 = M68K.D5 = M68K.D6 = M68K.D7 = 0;
            M68K.A0 = M68K.A1 = M68K.A2 = M68K.A3 = M68K.A4 = M68K.A5 = M68K.A6 = 0;
            M68K.IrqLevel = 0;
            M68K.Reset();
        }

        [STAThread]
        static int Main(string[] args)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            byte[] rom = LoadRom(dir);
            if (rom == null)
            {
                MessageBox.Show("Не найден файл данных игры 'rr3.bin' (или 'Road Rash 3 (R).bin') рядом с RR3.exe.", "Road Rash 3");
                return 1;
            }
            InitMachine(rom);
            M68K.MissingHook = pc => { try { File.AppendAllText(Path.Combine(dir, "missing.txt"), pc.ToString("X6") + "\n"); } catch { } };

            // headless test mode: RR3.exe --frames N [--shot every] [--input script]
            int hf = -1;
            for (int i = 0; i < args.Length; i++) if (args[i] == "--frames") hf = int.Parse(args[i + 1]);
            if (hf >= 0) return Headless.Run(args, hf, dir);

            Settings.Load();
            baseDir = dir;
            bool offscreen = false; int offW = 1920, offH = 1080;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--autoshot") foreach (var n in args[i + 1].Split('/')) { autoShot = int.Parse(n); autoShots.Add(autoShot); }
                if (args[i] == "--autoshot-name") autoShotName = args[i + 1];
                if (args[i] == "--autoexit") autoExit = int.Parse(args[i + 1]);
                if (args[i] == "--wav") { wavPath = Path.Combine(dir, args[i + 1]); Sound.Capture = new List<short>(); }
                if (args[i] == "--offscreen") { offscreen = true; var p = args[i + 1].Split('x'); offW = int.Parse(p[0]); offH = int.Parse(p[1]); }
                if (args[i] == "--keys")
                    foreach (var e in args[i + 1].Split(','))
                    {
                        var kv = e.Split(':');
                        int f = int.Parse(kv[0]);
                        if (!keyScript.ContainsKey(f)) keyScript[f] = new List<string>();
                        keyScript[f].Add(kv[1]);
                    }
                if (args[i] == "--set")
                {
                    // test overrides, e.g. filter=3
                    var kv = args[i + 1].Split('=');
                    int v = int.Parse(kv[1]);
                    if (kv[0] == "filter") Settings.Filter = v;
                    if (kv[0] == "aspect") Settings.Aspect = v;
                    if (kv[0] == "backdrop") Settings.Backdrop = v;
                    if (kv[0] == "scanlines") Settings.Scanlines = v;
                    if (kv[0] == "dedither") Settings.Dedither = v != 0;
                }
            }
            Sound.ApplyVolume();

            Application.EnableVisualStyles();
            form = new GameForm();
            if (offscreen)
            {
                Settings.NoSave = true;
                form.Offscreen = true; form.OffW = offW; form.OffH = offH;
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
                form.FormBorderStyle = FormBorderStyle.None; form.Location = new Point(-4000, -4000);
                Sound.Enabled = false;
            }
            form.ApplyVideo();
            var th = new Thread(EmuThread) { IsBackground = true };
            form.Shown += (s, e) => { form.ApplyVideo(); th.Start(); };
            Application.Run(form);
            Sound.Shutdown();
            if (wavPath != null)
            {
                Sound.SaveWav(wavPath);
                File.WriteAllText(wavPath + ".log", string.Format("queued={0} drops={1} gain={2} audioOpen={3} {4}", Audio.Queued, Audio.Drops, Sound.Gain, Audio.WasOpen, Program.PaceLog));
            }
            if (!offscreen) Settings.Save();
            return 0;
        }

        static byte[] LoadRom(string dir)
        {
            foreach (var n in new[] { "rr3.bin", "Road Rash 3 (R).bin", Path.Combine("..", "Road Rash 3 (R).bin") })
            {
                var p = Path.Combine(dir, n);
                if (File.Exists(p)) return File.ReadAllBytes(p);
            }
            return null;
        }

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr dc, int idx);
        [DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);

        static int RefreshRate()
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            int r = GetDeviceCaps(dc, 116);
            ReleaseDC(IntPtr.Zero, dc);
            return r;
        }

        static void RunKeyScript(int frame)
        {
            List<string> keys;
            if (!keyScript.TryGetValue(frame, out keys)) return;
            foreach (var s in keys)
            {
                bool press = !s.StartsWith("-"), tap = !(s.StartsWith("+") || s.StartsWith("-"));
                Keys k;
                if (!Enum.TryParse(s.TrimStart('+', '-'), true, out k)) continue;
                form.Invoke((Action)(() => form.HandleKey(k, press)));
                if (tap)
                {
                    if (!keyScript.ContainsKey(frame + 6)) keyScript[frame + 6] = new List<string>();
                    keyScript[frame + 6].Add("-" + s);
                }
            }
        }

        static void SaveShot(int[] px, int w, int h, string path)
        {
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb))
            {
                var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                Marshal.Copy(px, 0, d.Scan0, w * h);
                bmp.UnlockBits(d);
                bmp.Save(path, ImageFormat.Png);
            }
        }

        static void EmuThread()
        {
            timeBeginPeriod(1);
            var sw = Stopwatch.StartNew();
            double frameMs = 1000.0 / 59.922;
            double next = 0;
            int loop = 0, fpsFrames = 0, ovVersion = -1;
            long fpsT = 0;
            int[] present = new int[Vdp.W * Vdp.H];
            bool frameNew = true;
            GlRenderer gl = null;
            int[] smooth = new int[Vdp.W * Vdp.H];
            bool ditherState = Settings.Dedither;
            int refresh = RefreshRate();
            try
            {
                try { gl = new GlRenderer(form.Handle); }
                catch (Exception ex)
                {
                    form.GdiFallback = true;
                    try { File.WriteAllText(Path.Combine(baseDir, "opengl_error.txt"), ex.ToString()); } catch { }
                }
                Sound.Start();
                form.BeginInvoke((Action)(() => form.Pause.Toast("Esc — меню паузы и настройки   •   F11 — полный экран", 6)));
                while (form.Running)
                {
                    RunKeyScript(loop);
                    loop++;
                    if (form.ResetRequested) { form.ResetRequested = false; ResetMachine(); }
                    bool menu = form.MenuOpen;
                    Input.Update(menu);
                    if (Input.GamepadMenuRequest)
                    {
                        Input.GamepadMenuRequest = false;
                        form.BeginInvoke((Action)(() => { if (form.MenuOpen) form.CloseMenu(); else form.OpenMenu(); }));
                    }
                    if (menu) MenuGamepad();
                    if (!menu)
                    {
                        Vdp.RunFrame();
                        fpsFrames++;
                        Array.Copy(Vdp.Frame, present, present.Length);
                        frameNew = true;
                    }
                    int sw_ = Vdp.ScreenWidth, sh_ = Vdp.ScreenHeight;
                    bool isAuto = autoShots.Contains(loop);
                    bool shot = ShotRequested || isAuto;
                    if (gl != null)
                    {
                        gl.SetVSync(Settings.VSync && !form.Offscreen);
                        if (frameNew || ditherState != Settings.Dedither)
                        {
                            ditherState = Settings.Dedither;
                            if (Settings.Dedither) { Dedither(present, smooth, sw_, sh_); gl.UploadFrame(smooth); }
                            else gl.UploadFrame(present);
                            frameNew = false;
                        }
                        bool ov;
                        lock (form.OverlaySync)
                        {
                            ov = form.OverlayVisible;
                            if (ov && form.OverlayVersion != ovVersion) gl.UploadOverlay(form.OverlayPixels, form.OverlayW, form.OverlayH);
                            ovVersion = form.OverlayVersion;
                        }
                        int ow = form.OutW, oh = form.OutH;
                        gl.Render(form.ClientW, form.ClientH, ow, oh, sw_, sh_, ov);
                        if (shot)
                        {
                            bool fbo = ow != form.ClientW || oh != form.ClientH;
                            int cw = fbo ? ow : form.ClientW, ch = fbo ? oh : form.ClientH;
                            var px = gl.Capture(cw, ch);
                            bool auto = isAuto;
                            string name = auto ? Path.Combine(baseDir, autoShots.Count > 1 ? Path.GetFileNameWithoutExtension(autoShotName) + "_" + loop + ".png" : autoShotName)
                                : Path.Combine(ShotDir(), "RR3_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                            if (auto) SaveShot(px, cw, ch, name);
                            else
                            {
                                ThreadPool.QueueUserWorkItem(_ => { try { SaveShot(px, cw, ch, name); } catch { } });
                                form.BeginInvoke((Action)(() => form.Pause.Toast("Скриншот сохранён: " + Path.GetFileName(name))));
                            }
                        }
                        gl.Swap();
                    }
                    else
                    {
                        lock (form.GdiFrame)
                        {
                            var d = form.GdiFrame.LockBits(new Rectangle(0, 0, Vdp.W, Vdp.H), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                            Marshal.Copy(present, 0, d.Scan0, present.Length);
                            form.GdiFrame.UnlockBits(d);
                        }
                        try { form.BeginInvoke((Action)form.Invalidate); } catch { }
                    }
                    ShotRequested = false;
                    if (autoExit > 0 && loop >= autoExit) { form.BeginInvoke((Action)form.Close); break; }

                    long ms = sw.ElapsedMilliseconds;
                    if (ms - fpsT >= 1000)
                    {
                        string t = fpsFrames + " FPS";
                        if (wavPath != null) PaceLog.AppendFormat("t={0} fps={1} pending={2} adj={3:F4} queued={4} drops={5} menu={6}{7}", ms, fpsFrames, Audio.Pending(), Sound.RateAdjust, Audio.Queued, Audio.Drops, menu, string.Format(" pad={0} btn={1:X4} lt={2} rt={3} lx={4} ly={5} md={6:X2}", Input.GamepadConnected, Input.LastPad.Gamepad.wButtons, Input.LastPad.Gamepad.bLeftTrigger, Input.LastPad.Gamepad.bRightTrigger, Input.LastPad.Gamepad.sThumbLX, Input.LastPad.Gamepad.sThumbLY, Pad.Buttons1) + Environment.NewLine);
                        fpsFrames = 0; fpsT = ms;
                        form.BeginInvoke((Action)(() => { form.Pause.FpsText = t; if (Settings.ShowFps) form.Pause.Dirty = true; }));
                    }

                    // ---- pacing ----
                    if (form.Offscreen) { Thread.Sleep(1); continue; }
                    if (menu) { if (gl == null || !Settings.VSync) Thread.Sleep(15); next = sw.Elapsed.TotalMilliseconds; continue; }
                    bool vsyncPace = gl != null && Settings.VSync && refresh >= 59 && refresh <= 61 && Audio.IsOpen;
                    if (vsyncPace)
                    {
                        // the display drives the game; nudge the audio resampler to keep ~3 buffers queued
                        int p = Audio.Pending();
                        Sound.RateAdjust = Math.Max(-0.01, Math.Min(0.01, (p - 3) * 0.003));
                        if (p >= 6) Thread.Sleep(4);
                        next = sw.Elapsed.TotalMilliseconds;
                    }
                    else if (Audio.IsOpen)
                    {
                        Sound.RateAdjust = 0;
                        while (Audio.Pending() >= 3 && form.Running) Thread.Sleep(1);
                        next = sw.Elapsed.TotalMilliseconds;
                    }
                    else
                    {
                        next += frameMs;
                        double now = sw.Elapsed.TotalMilliseconds;
                        if (next - now > 1) Thread.Sleep((int)(next - now));
                        else if (now - next > 100) next = now;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Road Rash 3 — ошибка");
                try { form.BeginInvoke((Action)form.Close); } catch { }
            }
            finally { if (gl != null) gl.Dispose(); }
        }

        // Blend horizontal ABAB dither runs (>= 4 pixels) the way composite video did
        static void Dedither(int[] src, int[] dst, int w, int h)
        {
            Array.Copy(src, dst, src.Length);
            for (int y = 0; y < h; y++)
            {
                int o = y * Vdp.W;
                for (int x = 1; x < w - 1; x++)
                {
                    if (y == 0 || y == h - 1) continue;
                    int a = src[o + x - 1], b = src[o + x], c = src[o + x + 1];
                    if (a != c || a == b) continue;
                    // only a full 3x3 checkerboard: 4-neighbours share one colour, diagonals match the centre
                    int up = o - Vdp.W + x, dn = o + Vdp.W + x;
                    if (src[up] != a || src[dn] != a) continue;
                    if (src[up - 1] != b || src[dn + 1] != b || src[up + 1] != b || src[dn - 1] != b) continue;
                    // (2*b + a + c) / 4 per channel
                    int r = ((b >> 16 & 255) * 2 + (a >> 16 & 255) + (c >> 16 & 255)) >> 2;
                    int g = ((b >> 8 & 255) * 2 + (a >> 8 & 255) + (c >> 8 & 255)) >> 2;
                    int bl = ((b & 255) * 2 + (a & 255) + (c & 255)) >> 2;
                    dst[o + x] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | bl;
                }
            }
        }

        static string ShotDir()
        {
            string d = Path.Combine(baseDir, "screenshots");
            try { Directory.CreateDirectory(d); } catch { d = baseDir; }
            return d;
        }

        // gamepad navigation inside the pause menu
        static ushort lastMenuButtons;
        static void MenuGamepad()
        {
            if (!Input.GamepadConnected) return;
            ushort b = Input.PadButtons;
            var g = Input.LastPad.Gamepad;
            if (g.sThumbLY > 20000) b |= 0x0001;
            if (g.sThumbLY < -20000) b |= 0x0002;
            if (g.sThumbLX < -20000) b |= 0x0004;
            if (g.sThumbLX > 20000) b |= 0x0008;
            ushort pressed = (ushort)(b & ~lastMenuButtons);
            lastMenuButtons = b;
            if (pressed == 0) return;
            Keys k = Keys.None;
            if ((pressed & 0x0001) != 0) k = Keys.Up;
            else if ((pressed & 0x0002) != 0) k = Keys.Down;
            else if ((pressed & 0x0004) != 0) k = Keys.Left;
            else if ((pressed & 0x0008) != 0) k = Keys.Right;
            else if ((pressed & 0x1000) != 0) k = Keys.Enter;
            else if ((pressed & 0x2000) != 0 || (pressed & 0x0010) != 0) k = Keys.Escape;
            if (k != Keys.None) form.BeginInvoke((Action)(() => form.Pause.OnKey(k)));
        }
    }
}
