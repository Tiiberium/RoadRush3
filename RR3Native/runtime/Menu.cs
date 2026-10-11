// In-game pause menu and HUD overlay (drawn with GDI+, composited by the renderer)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace RR3
{
    class MenuItem
    {
        public string Label;
        public Func<string> Value;          // choice/slider text
        public Action<int> Change;          // -1 / +1
        public Action Activate;
        public GameAction? Bind;            // key binding row
        public bool Slider;
        public Func<int> SliderValue;
        public bool Separator;
        public RectangleF Rect;
        public RectangleF[] SlotRects = new RectangleF[2];
    }

    class PauseMenu
    {
        enum Page { Main, Controls, Video, Audio, ConfirmQuit, ConfirmReset }
        Page page = Page.Main;
        List<MenuItem> items = new List<MenuItem>();
        int sel, slot;
        bool waitKey;
        public bool Open;
        public bool Dirty = true;

        public Action OnResume, OnReset, OnQuit, OnVideoChanged, OnSoundChanged;

        // toasts / HUD
        string toast; DateTime toastUntil;
        public string FpsText;

        public void Show() { Open = true; page = Page.Main; Build(); sel = 0; waitKey = false; Dirty = true; }
        public void Hide() { Open = false; waitKey = false; Settings.Save(); Dirty = true; }

        public void Toast(string s, double seconds = 3)
        {
            toast = s; toastUntil = DateTime.Now.AddSeconds(seconds); Dirty = true;
        }
        public bool ToastActive { get { return toast != null && DateTime.Now < toastUntil; } }

        static string OnOff(bool b) { return b ? "Вкл" : "Выкл"; }

        void Go(Page p) { page = p; Build(); sel = FirstSelectable(); slot = 0; Dirty = true; }

        int FirstSelectable() { for (int i = 0; i < items.Count; i++) if (!items[i].Separator) return i; return 0; }

        MenuItem Btn(string label, Action a) { var m = new MenuItem { Label = label, Activate = a }; items.Add(m); return m; }
        MenuItem Choice(string label, Func<string> v, Action<int> ch) { var m = new MenuItem { Label = label, Value = v, Change = ch }; m.Activate = () => ch(1); items.Add(m); return m; }
        void Sep() { items.Add(new MenuItem { Separator = true }); }

        static int Cycle(int v, int d, int n) { return ((v + d) % n + n) % n; }

        public static string ResName(int idx)
        {
            if (idx < 0) return "Авто (" + Screen.PrimaryScreen.Bounds.Width + "×" + Screen.PrimaryScreen.Bounds.Height + ")";
            var r = Settings.Resolutions[idx];
            return r[0] + "×" + r[1];
        }

        void Build()
        {
            items.Clear();
            switch (page)
            {
                case Page.Main:
                    Btn("Продолжить", () => { if (OnResume != null) OnResume(); });
                    Btn("Управление", () => Go(Page.Controls));
                    Btn("Графика", () => Go(Page.Video));
                    Btn("Звук", () => Go(Page.Audio));
                    Sep();
                    Btn("Начать игру заново", () => Go(Page.ConfirmReset));
                    Btn("Выйти из игры", () => Go(Page.ConfirmQuit));
                    break;
                case Page.Controls:
                    for (int a = 0; a < (int)GameAction.Count; a++)
                        items.Add(new MenuItem { Label = Settings.ActionName((GameAction)a), Bind = (GameAction)a });
                    Sep();
                    Choice("Геймпад (XInput)", () => OnOff(Settings.Gamepad) + (Settings.Gamepad && Input.GamepadConnected ? " — подключён" : ""), d => { Settings.Gamepad = !Settings.Gamepad; Dirty = true; });
                    Btn("Сбросить по умолчанию", () => { Settings.Defaults(); Toast("Управление сброшено"); Dirty = true; });
                    Btn("Назад", () => Go(Page.Main));
                    break;
                case Page.Video:
                    Choice("Режим экрана", () => Settings.Fullscreen ? "Полный экран" : "Окно", d => { Settings.Fullscreen = !Settings.Fullscreen; Video(); });
                    Choice("Разрешение", () => ResName(Settings.ResIndex), d => { Settings.ResIndex = Cycle(Settings.ResIndex + 1, d, Settings.Resolutions.Length + 1) - 1; Video(); });
                    Choice("Улучшение графики", () => new[] { "Пиксели (оригинал)", "Чёткое сглаживание", "xBR HD", "xBR HD мягкий" }[Settings.Filter], d => { Settings.Filter = Cycle(Settings.Filter, d, 4); Dirty = true; });
                    Choice("Сглаживание дизеринга", () => OnOff(Settings.Dedither), d => { Settings.Dedither = !Settings.Dedither; Dirty = true; });
                    Choice("Пропорции", () => new[] { "4:3 (оригинал)", "Растянуть на экран", "Целочисленный масштаб" }[Settings.Aspect], d => { Settings.Aspect = Cycle(Settings.Aspect, d, 3); Dirty = true; });
                    Choice("Фон по краям", () => Settings.Backdrop == 1 ? "Размытая картинка" : "Чёрный", d => { Settings.Backdrop ^= 1; Dirty = true; });
                    Choice("Эффект ЭЛТ (сканлайны)", () => new[] { "Выкл", "Слабый", "Сильный" }[Settings.Scanlines], d => { Settings.Scanlines = Cycle(Settings.Scanlines, d, 3); Dirty = true; });
                    Choice("Вертикальная синхронизация", () => OnOff(Settings.VSync), d => { Settings.VSync = !Settings.VSync; Dirty = true; });
                    Choice("Показывать FPS", () => OnOff(Settings.ShowFps), d => { Settings.ShowFps = !Settings.ShowFps; Dirty = true; });
                    Sep();
                    Btn("Назад", () => Go(Page.Main));
                    break;
                case Page.Audio:
                    var vol = Choice("Громкость", () => Settings.Volume + "%", d => { Settings.Volume = Math.Max(0, Math.Min(100, Settings.Volume + d * 5)); Snd(); });
                    vol.Slider = true; vol.SliderValue = () => Settings.Volume;
                    Choice("Звук", () => OnOff(Settings.SoundOn), d => { Settings.SoundOn = !Settings.SoundOn; Snd(); });
                    Sep();
                    Btn("Назад", () => Go(Page.Main));
                    break;
                case Page.ConfirmQuit:
                    Btn("Да, выйти", () => { if (OnQuit != null) OnQuit(); });
                    Btn("Нет", () => Go(Page.Main));
                    break;
                case Page.ConfirmReset:
                    Btn("Да, начать заново", () => { if (OnReset != null) OnReset(); });
                    Btn("Нет", () => Go(Page.Main));
                    break;
            }
        }

        void Video() { Dirty = true; if (OnVideoChanged != null) OnVideoChanged(); }
        void Snd() { Dirty = true; if (OnSoundChanged != null) OnSoundChanged(); }

        string Title()
        {
            switch (page)
            {
                case Page.Controls: return "Управление";
                case Page.Video: return "Графика";
                case Page.Audio: return "Звук";
                case Page.ConfirmQuit: return "Выйти из игры?";
                case Page.ConfirmReset: return "Начать игру заново?";
            }
            return "Пауза";
        }

        // ---------------- input ----------------
        public void OnKey(Keys k)
        {
            Dirty = true;
            if (waitKey)
            {
                var it = items[sel];
                if (k == Keys.Escape) { waitKey = false; return; }
                if (k == Keys.Delete || k == Keys.Back) { Settings.Bind[(int)it.Bind.Value, slot] = Keys.None; waitKey = false; return; }
                AssignKey(it.Bind.Value, slot, k);
                waitKey = false;
                return;
            }
            switch (k)
            {
                case Keys.Escape:
                    if (page == Page.Main) { if (OnResume != null) OnResume(); }
                    else Go(page == Page.ConfirmQuit || page == Page.ConfirmReset ? Page.Main : Page.Main);
                    break;
                case Keys.Up: case Keys.W: Move(-1); break;
                case Keys.Down: case Keys.S: Move(1); break;
                case Keys.Left: case Keys.A: Side(-1); break;
                case Keys.Right: case Keys.D: Side(1); break;
                case Keys.Enter: case Keys.Space: ActivateSel(); break;
                case Keys.Delete: case Keys.Back:
                    if (items[sel].Bind != null) Settings.Bind[(int)items[sel].Bind.Value, slot] = Keys.None;
                    break;
            }
        }

        static void AssignKey(GameAction a, int slot, Keys k)
        {
            if (k == Keys.Escape || k == Keys.F11 || k == Keys.F12) return; // reserved
            // a key may only drive one action
            for (int i = 0; i < (int)GameAction.Count; i++)
                for (int s = 0; s < Settings.Slots; s++)
                    if (Settings.Bind[i, s] == k) Settings.Bind[i, s] = Keys.None;
            Settings.Bind[(int)a, slot] = k;
        }

        void Move(int d)
        {
            if (items.Count == 0) return;
            int i = sel;
            do { i = (i + d + items.Count) % items.Count; } while (items[i].Separator && i != sel);
            sel = i;
        }

        void Side(int d)
        {
            var it = items[sel];
            if (it.Bind != null) { slot = Math.Max(0, Math.Min(Settings.Slots - 1, slot + d)); return; }
            if (it.Change != null) it.Change(d);
        }

        void ActivateSel()
        {
            var it = items[sel];
            if (it.Bind != null) { waitKey = true; return; }
            if (it.Activate != null) it.Activate();
        }

        public void OnMouseMove(int x, int y)
        {
            if (waitKey) return;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Separator) continue;
                if (items[i].Rect.Contains(x, y))
                {
                    if (sel != i) { sel = i; Dirty = true; }
                    if (items[i].Bind != null)
                        for (int s = 0; s < Settings.Slots; s++)
                            if (items[i].SlotRects[s].Contains(x, y) && slot != s) { slot = s; Dirty = true; }
                }
            }
        }

        public void OnMouseClick(int x, int y, bool right)
        {
            if (waitKey) { waitKey = false; Dirty = true; return; }
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it.Separator || !it.Rect.Contains(x, y)) continue;
                sel = i; Dirty = true;
                if (it.Bind != null)
                {
                    for (int s = 0; s < Settings.Slots; s++) if (it.SlotRects[s].Contains(x, y)) slot = s;
                    if (right) Settings.Bind[(int)it.Bind.Value, slot] = Keys.None; else waitKey = true;
                    return;
                }
                if (it.Change != null) { it.Change(right ? -1 : 1); return; }
                if (!right && it.Activate != null) it.Activate();
                return;
            }
        }

        public void OnWheel(int delta)
        {
            if (!Open || waitKey) return;
            var it = items[sel];
            if (it.Slider && it.Change != null) { it.Change(delta > 0 ? 1 : -1); Dirty = true; }
            else Move(delta > 0 ? -1 : 1);
            Dirty = true;
        }

        // ---------------- drawing ----------------
        static readonly Color Accent = Color.FromArgb(255, 255, 196, 0);
        static readonly Color TextC = Color.FromArgb(255, 235, 235, 240);
        static readonly Color DimC = Color.FromArgb(255, 150, 150, 165);

        public bool HasContent { get { return Open || ToastActive || (Settings.ShowFps && FpsText != null); } }

        public void Draw(Graphics g, int w, int h)
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float u = Math.Max(0.74f, h / 1080f);
            if (Settings.ShowFps && FpsText != null)
                using (var f = new Font("Segoe UI", 15 * u, FontStyle.Bold, GraphicsUnit.Pixel))
                    Shadowed(g, FpsText, f, Accent, 12 * u, 8 * u);
            if (ToastActive && !Open)
            {
                using (var f = new Font("Segoe UI", 22 * u, FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    var sz = g.MeasureString(toast, f);
                    float bx = (w - sz.Width) / 2 - 20 * u, by = h - sz.Height - 70 * u;
                    using (var br = new SolidBrush(Color.FromArgb(190, 12, 12, 18)))
                        FillRound(g, br, new RectangleF(bx, by, sz.Width + 40 * u, sz.Height + 16 * u), 10 * u);
                    using (var br = new SolidBrush(TextC)) g.DrawString(toast, f, br, bx + 20 * u, by + 8 * u);
                }
            }
            if (!Open) return;

            using (var dim = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillRectangle(dim, 0, 0, w, h);
            bool wide = page == Page.Controls || page == Page.Video;
            float pw = (wide ? 900 : 600) * u, rowH = 52 * u, sepH = 18 * u;
            float contentH = 0;
            foreach (var it in items) contentH += it.Separator ? sepH : rowH;
            float ph = 150 * u + contentH + 70 * u;
            float px = (w - pw) / 2, py = (h - ph) / 2;
            using (var br = new SolidBrush(Color.FromArgb(235, 18, 18, 26))) FillRound(g, br, new RectangleF(px, py, pw, ph), 18 * u);
            using (var pen = new Pen(Color.FromArgb(90, 255, 196, 0), 2 * u)) DrawRound(g, pen, new RectangleF(px, py, pw, ph), 18 * u);

            using (var ft = new Font("Segoe UI", 40 * u, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var fs = new Font("Segoe UI", 15 * u, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                using (var br = new SolidBrush(Accent)) g.DrawString("ROAD RASH 3", fs, br, px + 40 * u, py + 26 * u);
                using (var br = new SolidBrush(TextC)) g.DrawString(Title(), ft, br, px + 36 * u, py + 46 * u);
            }
            float y = py + 130 * u;
            using (var fi = new Font("Segoe UI", 23 * u, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var fv = new Font("Segoe UI", 21 * u, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    if (it.Separator) { y += sepH; continue; }
                    var r = new RectangleF(px + 24 * u, y, pw - 48 * u, rowH - 6 * u);
                    it.Rect = r;
                    bool s = i == sel;
                    if (s)
                    {
                        using (var br = new SolidBrush(Color.FromArgb(60, 255, 196, 0))) FillRound(g, br, r, 10 * u);
                        using (var br = new SolidBrush(Accent)) g.FillRectangle(br, r.X, r.Y + 8 * u, 4 * u, r.Height - 16 * u);
                    }
                    var tc = s ? Color.White : TextC;
                    float ty = r.Y + (r.Height - fi.GetHeight(g)) / 2;
                    using (var br = new SolidBrush(tc)) g.DrawString(it.Label, fi, br, r.X + 18 * u, ty);
                    if (it.Bind != null)
                    {
                        float sw = 170 * u;
                        for (int k = 0; k < Settings.Slots; k++)
                        {
                            var sr = new RectangleF(r.Right - (Settings.Slots - k) * (sw + 12 * u), r.Y + 5 * u, sw, r.Height - 10 * u);
                            it.SlotRects[k] = sr;
                            bool active = s && slot == k;
                            string txt = active && waitKey ? "Нажмите клавишу…" : Settings.KeyName(Settings.Bind[(int)it.Bind.Value, k]);
                            using (var br = new SolidBrush(active ? Color.FromArgb(255, 255, 196, 0) : Color.FromArgb(255, 40, 40, 54))) FillRound(g, br, sr, 8 * u);
                            using (var f2 = new Font("Segoe UI", (active && waitKey ? 16 : 20) * u, FontStyle.Bold, GraphicsUnit.Pixel))
                            using (var br = new SolidBrush(active ? Color.Black : TextC))
                            {
                                var sz = g.MeasureString(txt, f2);
                                g.DrawString(txt, f2, br, sr.X + (sr.Width - sz.Width) / 2, sr.Y + (sr.Height - sz.Height) / 2);
                            }
                        }
                    }
                    else if (it.Value != null)
                    {
                        string v = it.Value();
                        var sz = g.MeasureString(v, fv);
                        float vx = r.Right - sz.Width - 50 * u;
                        if (it.Slider)
                        {
                            float bw = 260 * u, bx = vx - bw - 24 * u, bh = 8 * u, byy = r.Y + r.Height / 2 - bh / 2;
                            using (var br = new SolidBrush(Color.FromArgb(255, 50, 50, 64))) FillRound(g, br, new RectangleF(bx, byy, bw, bh), bh / 2);
                            using (var br = new SolidBrush(Accent)) FillRound(g, br, new RectangleF(bx, byy, bw * it.SliderValue() / 100f, bh), bh / 2);
                        }
                        using (var br = new SolidBrush(s ? Accent : DimC))
                        {
                            g.DrawString(v, fv, br, vx, r.Y + (r.Height - sz.Height) / 2);
                            if (s)
                            {
                                g.DrawString("‹", fv, br, vx - 26 * u, r.Y + (r.Height - sz.Height) / 2);
                                g.DrawString("›", fv, br, r.Right - 36 * u, r.Y + (r.Height - sz.Height) / 2);
                            }
                        }
                    }
                    y += rowH;
                }
            }
            string hint = page == Page.Controls
                ? (waitKey ? "Нажмите новую клавишу  •  Esc — отмена  •  Delete — очистить" : "Enter / клик — назначить  •  ←/→ — выбор слота  •  Delete / ПКМ — очистить  •  Esc — назад")
                : "↑/↓ — выбор  •  ←/→ — изменить  •  Enter — подтвердить  •  Esc — " + (page == Page.Main ? "продолжить" : "назад");
            using (var fh = new Font("Segoe UI", 15 * u, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var br = new SolidBrush(DimC))
            {
                var sz = g.MeasureString(hint, fh);
                g.DrawString(hint, fh, br, px + (pw - sz.Width) / 2, py + ph - 46 * u);
            }
        }

        static void Shadowed(Graphics g, string s, Font f, Color c, float x, float y)
        {
            using (var b = new SolidBrush(Color.FromArgb(200, 0, 0, 0))) g.DrawString(s, f, b, x + 2, y + 2);
            using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, y);
        }

        static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
        static void FillRound(Graphics g, Brush b, RectangleF r, float rad) { if (r.Width <= 0) return; using (var p = Round(r, rad)) g.FillPath(b, p); }
        static void DrawRound(Graphics g, Pen pen, RectangleF r, float rad) { using (var p = Round(r, rad)) g.DrawPath(pen, p); }
    }
}
