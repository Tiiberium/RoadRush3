// Keyboard + XInput gamepad -> Mega Drive pad state
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RR3
{
    static class Input
    {
        static readonly bool[] down = new bool[256];
        public static volatile bool GamepadMenuRequest;
        static bool prevBack;
        public static bool GamepadConnected;

        public static void KeyDown(Keys k) { int i = (int)(k & Keys.KeyCode); if (i < 256) down[i] = true; }
        public static void KeyUp(Keys k) { int i = (int)(k & Keys.KeyCode); if (i < 256) down[i] = false; }
        public static void Clear() { Array.Clear(down, 0, down.Length); }

        static bool Held(GameAction a)
        {
            for (int s = 0; s < Settings.Slots; s++)
            {
                Keys k = Settings.Bind[(int)a, s];
                if (k != Keys.None && (int)k < 256 && down[(int)k]) return true;
            }
            return false;
        }

        static int ActionBits(GameAction a)
        {
            switch (a)
            {
                case GameAction.Gas: return Pad.B;
                case GameAction.Brake: return Pad.A;
                case GameAction.Left: return Pad.Left;
                case GameAction.Right: return Pad.Right;
                case GameAction.Up: return Pad.Up;
                case GameAction.Down: return Pad.Down;
                case GameAction.Punch: return Pad.C;
                case GameAction.Kick: return Pad.C | Pad.Down;
                case GameAction.Start: return Pad.Start;
            }
            return 0;
        }

        // Called once per emulated frame
        public static void Update(bool menuOpen)
        {
            int b = 0;
            if (!menuOpen)
                for (int a = 0; a < (int)GameAction.Count; a++)
                    if (Held((GameAction)a)) b |= ActionBits((GameAction)a);
            b |= PollGamepad(menuOpen);
            // opposite directions cancel out
            if ((b & (Pad.Left | Pad.Right)) == (Pad.Left | Pad.Right)) b &= ~(Pad.Left | Pad.Right);
            if ((b & (Pad.Up | Pad.Down)) == (Pad.Up | Pad.Down)) b &= ~(Pad.Up | Pad.Down);
            Pad.Buttons1 = b;
        }

        // ---------------- XInput ----------------
        [StructLayout(LayoutKind.Sequential)]
        public struct XGamepad { public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }
        [StructLayout(LayoutKind.Sequential)]
        public struct XState { public uint dwPacketNumber; public XGamepad Gamepad; }
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern int XInputGetState14(int idx, out XState s);
        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")] static extern int XInputGetState910(int idx, out XState s);
        static int xinputLib; // 0 untried, 1 = 1.4, 2 = 9.1.0, -1 none
        static int pollDelay;

        static bool GetState(int idx, out XState s)
        {
            s = new XState();
            try
            {
                if (xinputLib == 0 || xinputLib == 1)
                {
                    try { int r = XInputGetState14(idx, out s); xinputLib = 1; return r == 0; }
                    catch (DllNotFoundException) { xinputLib = 2; }
                    catch (EntryPointNotFoundException) { xinputLib = 2; }
                }
                if (xinputLib == 2)
                {
                    try { return XInputGetState910(idx, out s) == 0; }
                    catch { xinputLib = -1; }
                }
            }
            catch { xinputLib = -1; }
            return false;
        }

        public static XState LastPad;
        public static ushort PadButtonsPrev, PadButtons;

        static int PollGamepad(bool menuOpen)
        {
            if (!Settings.Gamepad || xinputLib < 0) { GamepadConnected = false; return 0; }
            if (!GamepadConnected && pollDelay > 0) { pollDelay--; return 0; } // disconnected pads are slow to poll
            XState s;
            if (!GetState(0, out s)) { GamepadConnected = false; pollDelay = 120; return 0; }
            GamepadConnected = true;
            LastPad = s;
            PadButtonsPrev = PadButtons; PadButtons = s.Gamepad.wButtons;
            var g = s.Gamepad;
            bool back = (g.wButtons & 0x0020) != 0;
            if (back && !prevBack) GamepadMenuRequest = true;
            prevBack = back;
            if (menuOpen) return 0;
            int b = 0;
            if ((g.wButtons & 0x0001) != 0 || g.sThumbLY > 16000) b |= Pad.Up;
            if ((g.wButtons & 0x0002) != 0 || g.sThumbLY < -16000) b |= Pad.Down;
            if ((g.wButtons & 0x0004) != 0 || g.sThumbLX < -12000) b |= Pad.Left;
            if ((g.wButtons & 0x0008) != 0 || g.sThumbLX > 12000) b |= Pad.Right;
            if ((g.wButtons & 0x0010) != 0) b |= Pad.Start;
            if ((g.wButtons & 0x1000) != 0 || g.bRightTrigger > 60) b |= Pad.B;          // A / RT: gas
            if ((g.wButtons & 0x4000) != 0 || g.bLeftTrigger > 60) b |= Pad.A;           // X / LT: brake
            if ((g.wButtons & 0x2000) != 0 || (g.wButtons & 0x0200) != 0) b |= Pad.C;    // B / RB: punch
            if ((g.wButtons & 0x8000) != 0 || (g.wButtons & 0x0100) != 0) b |= Pad.C | Pad.Down; // Y / LB: kick
            return b;
        }
    }
}
