// Controller ports (3-button pad on port 1, driven by the keyboard)
namespace RR3
{
    static class Pad
    {
        // button bits (1 = pressed)
        public const int Up = 1, Down = 2, Left = 4, Right = 8, B = 16, C = 32, A = 64, Start = 128;
        public static volatile int Buttons1, Buttons2;
        static byte[] data = new byte[3], ctrl = new byte[3];

        public static uint ReadIo(uint a)
        {
            switch (a)
            {
                case 0x01: return 0xA0; // overseas, NTSC, no expansion unit
                case 0x03: return ReadPad(0, Buttons1);
                case 0x05: return ReadPad(1, Buttons2);
                case 0x07: return 0x7F;
                case 0x09: return ctrl[0];
                case 0x0B: return ctrl[1];
                case 0x0D: return ctrl[2];
            }
            return 0;
        }

        public static void WriteIo(uint a, uint v)
        {
            switch (a)
            {
                case 0x03: data[0] = (byte)v; break;
                case 0x05: data[1] = (byte)v; break;
                case 0x07: data[2] = (byte)v; break;
                case 0x09: ctrl[0] = (byte)v; break;
                case 0x0B: ctrl[1] = (byte)v; break;
                case 0x0D: ctrl[2] = (byte)v; break;
            }
        }

        static uint ReadPad(int p, int b)
        {
            // TH as output follows the data register, as input it floats high
            bool th = (ctrl[p] & 0x40) != 0 ? (data[p] & 0x40) != 0 : true;
            uint v;
            if (th)
                v = 0x40u | (uint)(~(b & 0x3F) & 0x3F);              // ?1CBRLDU
            else
                v = (uint)(~(b & 0x03) & 0x03) | ((b & A) != 0 ? 0u : 0x10u) | ((b & Start) != 0 ? 0u : 0x20u); // ?0SA00DU
            // output bits read back what was written
            uint outMask = ctrl[p] & 0x7Fu;
            v = (v & ~outMask) | (data[p] & outMask);
            return (v & 0x7F) | (data[p] & 0x80u);
        }
    }
}
