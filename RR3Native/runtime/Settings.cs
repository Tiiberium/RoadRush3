// User settings persisted in settings.ini
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace RR3
{
    enum GameAction { Gas, Brake, Left, Right, Up, Down, Punch, Kick, Start, Count }

    static class Settings
    {
        public const int Slots = 2;
        public static Keys[,] Bind = new Keys[(int)GameAction.Count, Slots];

        // video
        public static bool Fullscreen;
        public static int ResIndex = -1;      // index into Resolutions, -1 = auto
        public static int Filter = 2;         // 0 pixels, 1 sharp, 2 xBR, 3 xBR soft
        public static int Aspect;             // 0 4:3, 1 stretch, 2 integer
        public static int Backdrop = 1;       // 0 black, 1 blurred
        public static int Scanlines;          // 0 off, 1 light, 2 strong
        public static bool VSync = true;
        public static bool ShowFps;
        public static bool Dedither = true;  // blend checkerboard dithering like composite video
        // sound
        public static int Volume = 80;
        public static bool SoundOn = true;
        // input
        public static bool Gamepad = true;

        public static readonly int[][] Resolutions = {
            new[] { 1280, 720 }, new[] { 1366, 768 }, new[] { 1600, 900 }, new[] { 1920, 1080 },
            new[] { 2560, 1440 }, new[] { 3840, 2160 } };

        static string path;

        public static void Defaults()
        {
            Bind = new Keys[(int)GameAction.Count, Slots];
            Set(GameAction.Gas, Keys.W, Keys.None);
            Set(GameAction.Brake, Keys.A, Keys.None);
            Set(GameAction.Left, Keys.S, Keys.Left);
            Set(GameAction.Right, Keys.D, Keys.Right);
            Set(GameAction.Up, Keys.Up, Keys.None);
            Set(GameAction.Down, Keys.Down, Keys.None);
            Set(GameAction.Punch, Keys.Q, Keys.None);
            Set(GameAction.Kick, Keys.E, Keys.None);
            Set(GameAction.Start, Keys.Enter, Keys.Space);
        }

        static void Set(GameAction a, Keys k1, Keys k2) { Bind[(int)a, 0] = k1; Bind[(int)a, 1] = k2; }

        public static string ActionName(GameAction a)
        {
            switch (a)
            {
                case GameAction.Gas: return "Газ";
                case GameAction.Brake: return "Тормоз";
                case GameAction.Left: return "Поворот налево";
                case GameAction.Right: return "Поворот направо";
                case GameAction.Up: return "Вверх (меню)";
                case GameAction.Down: return "Вниз (меню)";
                case GameAction.Punch: return "Удар";
                case GameAction.Kick: return "Удар назад (ногой)";
                case GameAction.Start: return "Старт / выбор";
            }
            return a.ToString();
        }

        public static string KeyName(Keys k)
        {
            switch (k)
            {
                case Keys.None: return "—";
                case Keys.Up: return "↑";
                case Keys.Down: return "↓";
                case Keys.Left: return "←";
                case Keys.Right: return "→";
                case Keys.Enter: return "Enter";
                case Keys.Space: return "Пробел";
                case Keys.ShiftKey: case Keys.LShiftKey: return "Shift";
                case Keys.RShiftKey: return "П.Shift";
                case Keys.ControlKey: case Keys.LControlKey: return "Ctrl";
                case Keys.RControlKey: return "П.Ctrl";
                case Keys.Menu: case Keys.LMenu: return "Alt";
                case Keys.Back: return "Backspace";
                case Keys.Tab: return "Tab";
                case Keys.Capital: return "Caps Lock";
            }
            if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return "Num " + (k - Keys.NumPad0);
            return k.ToString();
        }

        static string FilePath()
        {
            if (path != null) return path;
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            path = Path.Combine(dir, "settings.ini");
            try
            {
                using (File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite)) { }
            }
            catch
            {
                string ad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RR3Native");
                Directory.CreateDirectory(ad);
                path = Path.Combine(ad, "settings.ini");
            }
            return path;
        }

        public static void Load()
        {
            Defaults();
            try
            {
                string p = FilePath();
                if (!File.Exists(p)) return;
                foreach (var line in File.ReadAllLines(p))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    int iv; int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out iv);
                    bool bv = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                    switch (k)
                    {
                        case "fullscreen": Fullscreen = bv; break;
                        case "resolution": ResIndex = Math.Max(-1, Math.Min(Resolutions.Length - 1, iv)); break;
                        case "filter": Filter = Clamp(iv, 0, 3); break;
                        case "aspect": Aspect = Clamp(iv, 0, 2); break;
                        case "backdrop": Backdrop = Clamp(iv, 0, 1); break;
                        case "scanlines": Scanlines = Clamp(iv, 0, 2); break;
                        case "vsync": VSync = bv; break;
                        case "showfps": ShowFps = bv; break;
                        case "dedither": Dedither = bv; break;
                        case "volume": Volume = Clamp(iv, 0, 100); break;
                        case "sound": SoundOn = bv; break;
                        case "gamepad": Gamepad = bv; break;
                        default:
                            if (k.StartsWith("key."))
                            {
                                var parts = k.Substring(4).Split('.');
                                GameAction a; int slot;
                                if (parts.Length == 2 && Enum.TryParse(parts[0], out a) && int.TryParse(parts[1], out slot) && slot >= 0 && slot < Slots && a < GameAction.Count)
                                {
                                    Keys key;
                                    if (Enum.TryParse(v, out key)) Bind[(int)a, slot] = key;
                                }
                            }
                            break;
                    }
                }
            }
            catch { }
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

        public static bool NoSave;

        public static void Save()
        {
            if (NoSave) return;
            try
            {
                var L = new List<string>();
                L.Add("; Road Rash 3 native - settings");
                L.Add("fullscreen=" + (Fullscreen ? 1 : 0));
                L.Add("resolution=" + ResIndex);
                L.Add("filter=" + Filter);
                L.Add("aspect=" + Aspect);
                L.Add("backdrop=" + Backdrop);
                L.Add("scanlines=" + Scanlines);
                L.Add("vsync=" + (VSync ? 1 : 0));
                L.Add("showfps=" + (ShowFps ? 1 : 0));
                L.Add("dedither=" + (Dedither ? 1 : 0));
                L.Add("volume=" + Volume);
                L.Add("sound=" + (SoundOn ? 1 : 0));
                L.Add("gamepad=" + (Gamepad ? 1 : 0));
                for (int a = 0; a < (int)GameAction.Count; a++)
                    for (int s = 0; s < Slots; s++)
                        L.Add(string.Format("key.{0}.{1}={2}", (GameAction)a, s, Bind[a, s]));
                File.WriteAllLines(FilePath(), L);
            }
            catch { }
        }
    }
}
