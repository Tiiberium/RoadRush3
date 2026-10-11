// OpenGL presentation: shader upscaling (xBR / sharp bilinear / pixels), blurred backdrop, CRT, overlay
using System;
using System.Runtime.InteropServices;

namespace RR3
{
    class GlRenderer : IDisposable
    {
        // ---------------- native ----------------
        [StructLayout(LayoutKind.Sequential)]
        struct PFD
        {
            public ushort nSize, nVersion; public uint dwFlags; public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift, cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits, cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
            public uint dwLayerMask, dwVisibleMask, dwDamageMask;
        }
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern int ChoosePixelFormat(IntPtr dc, ref PFD p);
        [DllImport("gdi32.dll")] static extern bool SetPixelFormat(IntPtr dc, int fmt, ref PFD p);
        [DllImport("gdi32.dll")] static extern bool SwapBuffers(IntPtr dc);
        [DllImport("opengl32.dll")] static extern IntPtr wglCreateContext(IntPtr dc);
        [DllImport("opengl32.dll")] static extern bool wglMakeCurrent(IntPtr dc, IntPtr ctx);
        [DllImport("opengl32.dll")] static extern bool wglDeleteContext(IntPtr ctx);
        [DllImport("opengl32.dll")] static extern IntPtr wglGetProcAddress(string name);
        [DllImport("opengl32.dll")] static extern void glViewport(int x, int y, int w, int h);
        [DllImport("opengl32.dll")] static extern void glClearColor(float r, float g, float b, float a);
        [DllImport("opengl32.dll")] static extern void glClear(uint mask);
        [DllImport("opengl32.dll")] static extern void glGenTextures(int n, uint[] t);
        [DllImport("opengl32.dll")] static extern void glDeleteTextures(int n, uint[] t);
        [DllImport("opengl32.dll")] static extern void glBindTexture(uint target, uint t);
        [DllImport("opengl32.dll")] static extern void glTexParameteri(uint target, uint p, int v);
        [DllImport("opengl32.dll")] static extern void glTexImage2D(uint target, int level, int ifmt, int w, int h, int border, uint fmt, uint type, IntPtr data);
        [DllImport("opengl32.dll")] static extern void glTexSubImage2D(uint target, int level, int x, int y, int w, int h, uint fmt, uint type, int[] data);
        [DllImport("opengl32.dll")] static extern void glTexSubImage2D(uint target, int level, int x, int y, int w, int h, uint fmt, uint type, IntPtr data);
        [DllImport("opengl32.dll")] static extern void glEnable(uint cap);
        [DllImport("opengl32.dll")] static extern void glDisable(uint cap);
        [DllImport("opengl32.dll")] static extern void glBlendFunc(uint s, uint d);
        [DllImport("opengl32.dll")] static extern void glBegin(uint mode);
        [DllImport("opengl32.dll")] static extern void glEnd();
        [DllImport("opengl32.dll")] static extern void glTexCoord2f(float u, float v);
        [DllImport("opengl32.dll")] static extern void glVertex2f(float x, float y);
        [DllImport("opengl32.dll")] static extern void glColor4f(float r, float g, float b, float a);
        [DllImport("opengl32.dll")] static extern void glMatrixMode(uint m);
        [DllImport("opengl32.dll")] static extern void glLoadIdentity();
        [DllImport("opengl32.dll")] static extern void glOrtho(double l, double r, double b, double t, double n, double f);
        [DllImport("opengl32.dll")] static extern IntPtr glGetString(uint name);
        [DllImport("opengl32.dll")] static extern void glReadPixels(int x, int y, int w, int h, uint fmt, uint type, int[] data);
        [DllImport("opengl32.dll")] static extern void glPixelStorei(uint p, int v);
        [DllImport("opengl32.dll")] static extern uint glGetError();

        const uint GL_TEXTURE_2D = 0x0DE1, GL_RGBA = 0x1908, GL_BGRA = 0x80E1, GL_UNSIGNED_BYTE = 0x1401;
        const uint GL_TEXTURE_MIN_FILTER = 0x2801, GL_TEXTURE_MAG_FILTER = 0x2800, GL_TEXTURE_WRAP_S = 0x2802, GL_TEXTURE_WRAP_T = 0x2803;
        const int GL_NEAREST = 0x2600, GL_LINEAR = 0x2601, GL_CLAMP_TO_EDGE = 0x812F;
        const uint GL_COLOR_BUFFER_BIT = 0x4000, GL_QUADS = 7, GL_BLEND = 0x0BE2, GL_ONE = 1, GL_ONE_MINUS_SRC_ALPHA = 0x0303;
        const uint GL_PROJECTION = 0x1701, GL_MODELVIEW = 0x1700, GL_VERSION = 0x1F02, GL_RENDERER = 0x1F01;
        const uint GL_FRAGMENT_SHADER = 0x8B30, GL_VERTEX_SHADER = 0x8B31, GL_COMPILE_STATUS = 0x8B81, GL_LINK_STATUS = 0x8B82;
        const uint GL_FRAMEBUFFER = 0x8D40, GL_COLOR_ATTACHMENT0 = 0x8CE0, GL_FRAMEBUFFER_COMPLETE = 0x8CD5;
        const uint GL_UNPACK_ROW_LENGTH = 0x0CF2, GL_PACK_ALIGNMENT = 0x0D05, GL_TEXTURE0 = 0x84C0;

        delegate uint dCreateShader(uint type);
        delegate void dShaderSource(uint s, int count, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] src, int[] len);
        delegate void dCompileShader(uint s);
        delegate void dGetShaderiv(uint s, uint p, out int v);
        delegate void dGetInfoLog(uint s, int max, out int len, System.Text.StringBuilder log);
        delegate uint dCreateProgram();
        delegate void dAttachShader(uint p, uint s);
        delegate void dLinkProgram(uint p);
        delegate void dUseProgram(uint p);
        delegate int dGetUniformLocation(uint p, string name);
        delegate void dUniform1i(int loc, int v);
        delegate void dUniform1f(int loc, float v);
        delegate void dUniform2f(int loc, float a, float b);
        delegate bool dSwapInterval(int i);
        delegate void dGenFramebuffers(int n, uint[] f);
        delegate void dBindFramebuffer(uint target, uint f);
        delegate void dFramebufferTexture2D(uint target, uint att, uint textarget, uint tex, int level);
        delegate uint dCheckFramebufferStatus(uint target);
        delegate void dActiveTexture(uint t);

        dCreateShader glCreateShader; dShaderSource glShaderSource; dCompileShader glCompileShader;
        dGetShaderiv glGetShaderiv, glGetProgramiv; dGetInfoLog glGetShaderInfoLog, glGetProgramInfoLog;
        dCreateProgram glCreateProgram; dAttachShader glAttachShader; dLinkProgram glLinkProgram; dUseProgram glUseProgram;
        dGetUniformLocation glGetUniformLocation; dUniform1i glUniform1i; dUniform1f glUniform1f; dUniform2f glUniform2f;
        dSwapInterval wglSwapIntervalEXT; dGenFramebuffers glGenFramebuffers; dBindFramebuffer glBindFramebuffer;
        dFramebufferTexture2D glFramebufferTexture2D; dCheckFramebufferStatus glCheckFramebufferStatus; dActiveTexture glActiveTexture;

        T Proc<T>(string name) where T : class
        {
            IntPtr p = wglGetProcAddress(name);
            long v = p.ToInt64();
            if (v == 0 || v == 1 || v == 2 || v == 3 || v == -1) return null;
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        // ---------------- state ----------------
        IntPtr hwnd, hdc, ctx;
        uint texGame, texOverlay, texFbo, fbo;
        int fboW, fboH;
        uint progPixel, progSharp, progXbr, progXbrSoft, progBlur, progCopy;
        int overlayW, overlayH;
        int swapInterval = -1;
        public string Info = "";
        public bool Ok;

        const int TW = 320, TH = 240; // game texture size

        public GlRenderer(IntPtr hwnd)
        {
            this.hwnd = hwnd;
            hdc = GetDC(hwnd);
            var pfd = new PFD { nSize = (ushort)Marshal.SizeOf(typeof(PFD)), nVersion = 1, dwFlags = 0x4 | 0x20 | 0x1, iPixelType = 0, cColorBits = 32, cAlphaBits = 8, iLayerType = 0 };
            int fmt = ChoosePixelFormat(hdc, ref pfd);
            if (fmt == 0 || !SetPixelFormat(hdc, fmt, ref pfd)) throw new Exception("SetPixelFormat failed");
            ctx = wglCreateContext(hdc);
            if (ctx == IntPtr.Zero || !wglMakeCurrent(hdc, ctx)) throw new Exception("wglCreateContext failed");
            Info = Marshal.PtrToStringAnsi(glGetString(GL_RENDERER)) + " / GL " + Marshal.PtrToStringAnsi(glGetString(GL_VERSION));

            glCreateShader = Proc<dCreateShader>("glCreateShader");
            glShaderSource = Proc<dShaderSource>("glShaderSource");
            glCompileShader = Proc<dCompileShader>("glCompileShader");
            glGetShaderiv = Proc<dGetShaderiv>("glGetShaderiv");
            glGetProgramiv = Proc<dGetShaderiv>("glGetProgramiv");
            glGetShaderInfoLog = Proc<dGetInfoLog>("glGetShaderInfoLog");
            glGetProgramInfoLog = Proc<dGetInfoLog>("glGetProgramInfoLog");
            glCreateProgram = Proc<dCreateProgram>("glCreateProgram");
            glAttachShader = Proc<dAttachShader>("glAttachShader");
            glLinkProgram = Proc<dLinkProgram>("glLinkProgram");
            glUseProgram = Proc<dUseProgram>("glUseProgram");
            glGetUniformLocation = Proc<dGetUniformLocation>("glGetUniformLocation");
            glUniform1i = Proc<dUniform1i>("glUniform1i");
            glUniform1f = Proc<dUniform1f>("glUniform1f");
            glUniform2f = Proc<dUniform2f>("glUniform2f");
            glActiveTexture = Proc<dActiveTexture>("glActiveTexture");
            wglSwapIntervalEXT = Proc<dSwapInterval>("wglSwapIntervalEXT");
            glGenFramebuffers = Proc<dGenFramebuffers>("glGenFramebuffers") ?? Proc<dGenFramebuffers>("glGenFramebuffersEXT");
            glBindFramebuffer = Proc<dBindFramebuffer>("glBindFramebuffer") ?? Proc<dBindFramebuffer>("glBindFramebufferEXT");
            glFramebufferTexture2D = Proc<dFramebufferTexture2D>("glFramebufferTexture2D") ?? Proc<dFramebufferTexture2D>("glFramebufferTexture2DEXT");
            glCheckFramebufferStatus = Proc<dCheckFramebufferStatus>("glCheckFramebufferStatus") ?? Proc<dCheckFramebufferStatus>("glCheckFramebufferStatusEXT");
            if (glCreateShader == null || glUseProgram == null) throw new Exception("OpenGL 2.0 shaders are not supported: " + Info);

            var t = new uint[3];
            glGenTextures(3, t);
            texGame = t[0]; texOverlay = t[1]; texFbo = t[2];
            glBindTexture(GL_TEXTURE_2D, texGame);
            SetTexParams(GL_NEAREST);
            glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA, TW, TH, 0, GL_BGRA, GL_UNSIGNED_BYTE, IntPtr.Zero);
            glBindTexture(GL_TEXTURE_2D, texOverlay);
            SetTexParams(GL_LINEAR);

            progPixel = Program(Shaders.Pixel);
            progSharp = Program(Shaders.Sharp);
            progXbr = Program(Shaders.Xbr(false));
            progXbrSoft = Program(Shaders.Xbr(true));
            progBlur = Program(Shaders.Blur);
            progCopy = Program(Shaders.Copy);
            Ok = true;
        }

        void SetTexParams(int filter)
        {
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, filter);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, filter);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        }

        uint Compile(uint type, string src)
        {
            uint s = glCreateShader(type);
            glShaderSource(s, 1, new[] { src }, null);
            glCompileShader(s);
            int ok; glGetShaderiv(s, GL_COMPILE_STATUS, out ok);
            if (ok == 0)
            {
                var sb = new System.Text.StringBuilder(4096); int len;
                glGetShaderInfoLog(s, 4096, out len, sb);
                throw new Exception("Shader compile error: " + sb);
            }
            return s;
        }

        uint Program(string frag)
        {
            uint p = glCreateProgram();
            glAttachShader(p, Compile(GL_VERTEX_SHADER, Shaders.Vertex));
            glAttachShader(p, Compile(GL_FRAGMENT_SHADER, frag));
            glLinkProgram(p);
            int ok; glGetProgramiv(p, GL_LINK_STATUS, out ok);
            if (ok == 0)
            {
                var sb = new System.Text.StringBuilder(4096); int len;
                glGetProgramInfoLog(p, 4096, out len, sb);
                throw new Exception("Shader link error: " + sb);
            }
            return p;
        }

        public void SetVSync(bool on)
        {
            int v = on ? 1 : 0;
            if (v != swapInterval && wglSwapIntervalEXT != null) { wglSwapIntervalEXT(v); swapInterval = v; }
        }

        public void UploadFrame(int[] frame)
        {
            glBindTexture(GL_TEXTURE_2D, texGame);
            glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, TW, TH, GL_BGRA, GL_UNSIGNED_BYTE, frame);
        }

        void Quad(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1)
        {
            glBegin(GL_QUADS);
            glTexCoord2f(u0, v0); glVertex2f(x0, y0);
            glTexCoord2f(u1, v0); glVertex2f(x1, y0);
            glTexCoord2f(u1, v1); glVertex2f(x1, y1);
            glTexCoord2f(u0, v1); glVertex2f(x0, y1);
            glEnd();
        }

        void Ortho(int w, int h)
        {
            glViewport(0, 0, w, h);
            glMatrixMode(GL_PROJECTION); glLoadIdentity(); glOrtho(0, w, h, 0, -1, 1);
            glMatrixMode(GL_MODELVIEW); glLoadIdentity();
        }

        void Uniforms(uint p, float srcW, float srcH, float outW, float outH)
        {
            glUseProgram(p);
            glUniform1i(glGetUniformLocation(p, "tex"), 0);
            int l = glGetUniformLocation(p, "texSize"); if (l >= 0) glUniform2f(l, TW, TH);
            l = glGetUniformLocation(p, "srcSize"); if (l >= 0) glUniform2f(l, srcW, srcH);
            l = glGetUniformLocation(p, "outSize"); if (l >= 0) glUniform2f(l, outW, outH);
            l = glGetUniformLocation(p, "scan"); if (l >= 0) glUniform1f(l, Settings.Scanlines == 0 ? 0f : Settings.Scanlines == 1 ? 0.25f : 0.5f);
        }

        bool EnsureFbo(int w, int h)
        {
            if (glGenFramebuffers == null) return false;
            if (fbo != 0 && fboW == w && fboH == h) return true;
            if (fbo == 0) { var f = new uint[1]; glGenFramebuffers(1, f); fbo = f[0]; }
            glBindTexture(GL_TEXTURE_2D, texFbo);
            SetTexParams(GL_LINEAR);
            glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA, w, h, 0, GL_BGRA, GL_UNSIGNED_BYTE, IntPtr.Zero);
            glBindFramebuffer(GL_FRAMEBUFFER, fbo);
            glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, texFbo, 0);
            bool ok = glCheckFramebufferStatus(GL_FRAMEBUFFER) == GL_FRAMEBUFFER_COMPLETE;
            glBindFramebuffer(GL_FRAMEBUFFER, 0);
            fboW = w; fboH = h;
            if (!ok) { fbo = 0; return false; }
            return true;
        }

        // Draw the game frame into the current target of size w x h
        void DrawScene(int w, int h, int sw, int sh)
        {
            glClearColor(0, 0, 0, 1);
            glClear(GL_COLOR_BUFFER_BIT);
            float u1 = sw / (float)TW, v1 = sh / (float)TH;
            // destination rectangle
            float dw, dh;
            if (Settings.Aspect == 1) { dw = w; dh = h; }
            else if (Settings.Aspect == 2)
            {
                int k = Math.Max(1, Math.Min(w / 320, h / sh));
                dw = 320 * k; dh = sh * k;
                if (k * 320 > w || k * sh > h) { dw = w; dh = h; }
            }
            else
            {
                dh = h; dw = h * 4f / 3f;
                if (dw > w) { dw = w; dh = w * 3f / 4f; }
            }
            float dx = (w - dw) / 2, dy = (h - dh) / 2;
            glBindTexture(GL_TEXTURE_2D, texGame);
            // blurred ambient backdrop when the picture does not fill the screen
            if (Settings.Backdrop == 1 && (dw < w - 1 || dh < h - 1))
            {
                SetTexParams(GL_LINEAR);
                Uniforms(progBlur, sw, sh, w, h);
                // zoom the picture so it covers the whole screen
                float s = Math.Max(w / dw, h / dh) * 1.08f;
                float bw = dw * s, bh = dh * s;
                Quad((w - bw) / 2, (h - bh) / 2, (w + bw) / 2, (h + bh) / 2, 0, 0, u1, v1);
            }
            uint prog;
            int filt = GL_NEAREST;
            switch (Settings.Filter)
            {
                case 0: prog = progPixel; break;
                case 1: prog = progSharp; filt = GL_LINEAR; break;
                case 3: prog = progXbrSoft; break;
                default: prog = progXbr; break;
            }
            SetTexParams(filt);
            Uniforms(prog, sw, sh, dw, dh);
            Quad(dx, dy, dx + dw, dy + dh, 0, 0, u1, v1);
            glUseProgram(0);
        }

        bool lastUsedFbo;

        // Renders scene + overlay at outW x outH (via FBO when it differs from the client size), then shows it.
        public void Render(int clientW, int clientH, int outW, int outH, int sw, int sh, bool overlay)
        {
            if (clientW <= 0 || clientH <= 0) return;
            if (outW <= 0 || outH <= 0) { outW = clientW; outH = clientH; }
            bool useFbo = (outW != clientW || outH != clientH) && EnsureFbo(outW, outH);
            if (!useFbo) { outW = clientW; outH = clientH; }
            lastUsedFbo = useFbo;
            if (useFbo) glBindFramebuffer(GL_FRAMEBUFFER, fbo);
            Ortho(outW, outH);
            DrawScene(outW, outH, sw, sh);
            if (overlay && overlayW > 0)
            {
                glEnable(GL_BLEND);
                glBlendFunc(GL_ONE, GL_ONE_MINUS_SRC_ALPHA);
                glBindTexture(GL_TEXTURE_2D, texOverlay);
                glUseProgram(progCopy);
                glUniform1i(glGetUniformLocation(progCopy, "tex"), 0);
                Quad(0, 0, outW, outH, 0, 0, 1, 1);
                glUseProgram(0);
                glDisable(GL_BLEND);
            }
            if (useFbo)
            {
                glBindFramebuffer(GL_FRAMEBUFFER, 0);
                Ortho(clientW, clientH);
                glClearColor(0, 0, 0, 1); glClear(GL_COLOR_BUFFER_BIT);
                // keep the aspect of the render target inside the window
                float s = Math.Min(clientW / (float)outW, clientH / (float)outH);
                float w = outW * s, h = outH * s, x = (clientW - w) / 2, y = (clientH - h) / 2;
                glBindTexture(GL_TEXTURE_2D, texFbo);
                glUseProgram(progCopy);
                glUniform1i(glGetUniformLocation(progCopy, "tex"), 0);
                Quad(x, y, x + w, y + h, 0, 1, 1, 0); // FBO texture is bottom-up
                glUseProgram(0);
            }
        }

        // Read back the last rendered image (top-down BGRA)
        public int[] Capture(int w, int h)
        {
            if (lastUsedFbo) glBindFramebuffer(GL_FRAMEBUFFER, fbo);
            var px = new int[w * h];
            glPixelStorei(GL_PACK_ALIGNMENT, 4);
            glReadPixels(0, 0, w, h, GL_BGRA, GL_UNSIGNED_BYTE, px);
            if (lastUsedFbo) glBindFramebuffer(GL_FRAMEBUFFER, 0);
            var o = new int[w * h];
            for (int y = 0; y < h; y++) Array.Copy(px, (h - 1 - y) * w, o, y * w, w);
            return o;
        }

        public void UploadOverlay(int[] bgra, int w, int h)
        {
            glBindTexture(GL_TEXTURE_2D, texOverlay);
            if (w != overlayW || h != overlayH)
            {
                glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA, w, h, 0, GL_BGRA, GL_UNSIGNED_BYTE, IntPtr.Zero);
                overlayW = w; overlayH = h;
            }
            glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w, h, GL_BGRA, GL_UNSIGNED_BYTE, bgra);
        }

        public void ClearOverlay() { overlayW = 0; overlayH = 0; }

        public void Swap() { SwapBuffers(hdc); }

        public void Dispose()
        {
            if (ctx != IntPtr.Zero) { wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); wglDeleteContext(ctx); ctx = IntPtr.Zero; }
            if (hdc != IntPtr.Zero) { ReleaseDC(hwnd, hdc); hdc = IntPtr.Zero; }
        }
    }

    static class Shaders
    {
        public const string Vertex = @"#version 120
varying vec2 uv;
void main() { gl_Position = gl_ModelViewProjectionMatrix * gl_Vertex; uv = gl_MultiTexCoord0.xy; }";

        const string Common = @"#version 120
uniform sampler2D tex;
uniform vec2 texSize;
uniform vec2 srcSize;
uniform vec2 outSize;
uniform float scan;
varying vec2 uv;
vec3 crt(vec3 c) {
    if (scan <= 0.0) return c;
    float y = fract(uv.y * texSize.y);
    float w = 0.5 + 0.5 * cos((y - 0.5) * 6.2831853);
    float l = dot(c, vec3(0.299, 0.587, 0.114));
    float k = mix(1.0, w, scan * (1.0 - 0.6 * l));
    return c * k * (1.0 + scan * 0.35);
}
";

        public const string Pixel = Common + @"
void main() { gl_FragColor = vec4(crt(texture2D(tex, uv).rgb), 1.0); }";

        // sharp bilinear: integer nearest prescale, then bilinear for the fractional remainder
        public const string Sharp = Common + @"
void main() {
    vec2 scale = max(floor(outSize / srcSize), vec2(1.0));
    vec2 texel = uv * texSize;
    vec2 tf = floor(texel);
    vec2 s = fract(texel);
    vec2 rr = 0.5 - 0.5 / scale;
    vec2 cd = s - 0.5;
    vec2 f = (cd - clamp(cd, -rr, rr)) * scale + 0.5;
    gl_FragColor = vec4(crt(texture2D(tex, (tf + f) / texSize).rgb), 1.0);
}";

        public const string Copy = @"#version 120
uniform sampler2D tex;
varying vec2 uv;
void main() { gl_FragColor = texture2D(tex, uv); }";

        // wide blur + darken for the ambient backdrop
        public const string Blur = Common + @"
void main() {
    vec3 acc = vec3(0.0);
    float tw = 0.0;
    vec2 r = vec2(18.0, 14.0) / texSize;
    for (int y = -3; y <= 3; y++)
        for (int x = -3; x <= 3; x++) {
            float w = exp(-float(x * x + y * y) / 8.0);
            acc += texture2D(tex, uv + vec2(float(x), float(y)) * r / 3.0).rgb * w;
            tw += w;
        }
    vec3 c = acc / tw;
    gl_FragColor = vec4(c * 0.38, 1.0);
}";

        // xBR level 2 (after Hyllian's xbr-lv2 shader), resolution independent
        public static string Xbr(bool soft)
        {
            return Common + (soft ? "#define XBR_DELTA 0.75\n#define XBR_FINAL_BLEND 0.35\n" : "#define XBR_DELTA 0.40\n#define XBR_FINAL_BLEND 0.0\n") + @"
#define XBR_Y_WEIGHT 48.0
#define XBR_EQ_THRESHOLD 15.0
#define XBR_LV2_COEFFICIENT 2.0
const vec4 Ao = vec4( 1.0, -1.0, -1.0, 1.0 );
const vec4 Bo = vec4( 1.0,  1.0, -1.0,-1.0 );
const vec4 Co = vec4( 1.5,  0.5, -0.5, 0.5 );
const vec4 Ax = vec4( 1.0, -1.0, -1.0, 1.0 );
const vec4 Bx = vec4( 0.5,  2.0, -0.5,-2.0 );
const vec4 Cx = vec4( 1.0,  1.0, -0.5, 0.0 );
const vec4 Ay = vec4( 1.0, -1.0, -1.0, 1.0 );
const vec4 By = vec4( 2.0,  0.5, -2.0,-0.5 );
const vec4 Cy = vec4( 2.0,  0.0, -1.0, 0.5 );
const vec4 Ci = vec4(0.25, 0.25, 0.25, 0.25);
const vec3 Yw = vec3(0.2126, 0.7152, 0.0722);

vec4 df(vec4 A, vec4 B) { return abs(A - B); }
float c_df(vec3 c1, vec3 c2) { vec3 d = abs(c1 - c2); return d.r + d.g + d.b; }
vec4 eqv(vec4 A, vec4 B) { return vec4(lessThan(df(A, B), vec4(XBR_EQ_THRESHOLD))); }
vec4 neqv(vec4 A, vec4 B) { return 1.0 - eqv(A, B); }
vec4 wd(vec4 a, vec4 b, vec4 c, vec4 d, vec4 e, vec4 f, vec4 g, vec4 h) {
    return df(a, b) + df(a, c) + df(d, e) + df(d, f) + 4.0 * df(g, h);
}
vec3 T(vec2 o) { return texture2D(tex, uv + o / texSize).rgb; }
float lum(vec3 c) { return dot(c, Yw) * XBR_Y_WEIGHT; }
vec4 L4(vec3 a, vec3 b, vec3 c, vec3 d) { return vec4(lum(a), lum(b), lum(c), lum(d)); }
vec4 OR(vec4 a, vec4 b) { return min(a + b, 1.0); }

void main() {
    vec2 fp = fract(uv * texSize);
    // snap sampling to the texel center
    vec2 base = (floor(uv * texSize) + 0.5) / texSize;
    vec2 d = 1.0 / texSize;
    #define S(x, y) texture2D(tex, base + vec2(x, y) * d).rgb
    vec3 A1 = S(-1.0,-2.0), B1 = S(0.0,-2.0), C1 = S(1.0,-2.0);
    vec3 A  = S(-1.0,-1.0), B  = S(0.0,-1.0), C  = S(1.0,-1.0);
    vec3 D  = S(-1.0, 0.0), E  = S(0.0, 0.0), F  = S(1.0, 0.0);
    vec3 G  = S(-1.0, 1.0), H  = S(0.0, 1.0), I  = S(1.0, 1.0);
    vec3 G5 = S(-1.0, 2.0), H5 = S(0.0, 2.0), I5 = S(1.0, 2.0);
    vec3 A0 = S(-2.0,-1.0), D0 = S(-2.0, 0.0), G0 = S(-2.0, 1.0);
    vec3 C4 = S( 2.0,-1.0), F4 = S( 2.0, 0.0), I4 = S( 2.0, 1.0);

    vec4 b = L4(B, D, H, F);
    vec4 c = L4(C, A, G, I);
    vec4 e = L4(E, E, E, E);
    vec4 dd = b.yzwx;
    vec4 f = b.wxyz;
    vec4 g = c.zwxy;
    vec4 h = b.zwxy;
    vec4 i = c.wxyz;
    vec4 i4 = L4(I4, C1, A0, G5);
    vec4 i5 = L4(I5, C4, A1, G0);
    vec4 h5 = L4(H5, F4, B1, D0);
    vec4 f4 = h5.yzwx;

    vec4 fx      = Ao * fp.y + Bo * fp.x;
    vec4 fx_left = Ax * fp.y + Bx * fp.x;
    vec4 fx_up   = Ay * fp.y + By * fp.x;

    vec4 irlv0 = vec4(notEqual(e, f)) * vec4(notEqual(e, h));
    vec4 irlv1 = irlv0 * OR(OR(OR(neqv(f, b) * neqv(f, c), neqv(h, dd) * neqv(h, g)),
                 eqv(e, i) * OR(neqv(f, f4) * neqv(f, i4), neqv(h, h5) * neqv(h, i5))), OR(eqv(e, g), eqv(e, c)));
    vec4 irlv2l = vec4(notEqual(e, g)) * vec4(notEqual(dd, g));
    vec4 irlv2u = vec4(notEqual(e, c)) * vec4(notEqual(b, c));

    vec4 delta = vec4(XBR_DELTA);
    vec4 fx45i = smoothstep(Ci + Co - delta, Ci + Co + delta, fx);
    vec4 fx45  = smoothstep(Co - delta, Co + delta, fx);
    vec4 fx30  = smoothstep(Cx - delta, Cx + delta, fx_left);
    vec4 fx60  = smoothstep(Cy - delta, Cy + delta, fx_up);

    vec4 w1 = wd(e, c, g, i, h5, f4, h, f);
    vec4 w2 = wd(h, dd, i5, f, i4, b, e, i);
    vec4 edri = vec4(lessThanEqual(w1, w2)) * irlv0;
    vec4 edr  = vec4(lessThan(w1, w2)) * irlv1;
    vec4 edrl = vec4(lessThanEqual(XBR_LV2_COEFFICIENT * df(f, g), df(h, c))) * irlv2l * edr;
    vec4 edru = vec4(greaterThanEqual(df(f, g), XBR_LV2_COEFFICIENT * df(h, c))) * irlv2u * edr;

    fx45  = edr * fx45;
    fx30  = edrl * fx30;
    fx60  = edru * fx60;
    fx45i = edri * fx45i;

    vec4 px = vec4(lessThanEqual(df(e, f), df(e, h)));
    vec4 mx = max(max(fx30, fx60), max(fx45, fx45i));

    vec3 res1 = E;
    res1 = mix(res1, mix(H, F, px.x), mx.x);
    res1 = mix(res1, mix(B, D, px.z), mx.z);
    vec3 res2 = E;
    res2 = mix(res2, mix(F, B, px.y), mx.y);
    res2 = mix(res2, mix(D, H, px.w), mx.w);
    vec3 res = mix(res1, res2, step(c_df(E, res1), c_df(E, res2)));
    if (XBR_FINAL_BLEND > 0.0) {
        // gentle smoothing of the remaining pixel steps
        vec2 q = fp - 0.5;
        vec2 o = vec2(q.x < 0.0 ? -1.0 : 1.0, q.y < 0.0 ? -1.0 : 1.0);
        vec2 w = abs(q);
        vec3 lin = mix(mix(E, S(o.x, 0.0), w.x), mix(S(0.0, o.y), S(o.x, o.y), w.x), w.y);
        res = mix(res, lin, XBR_FINAL_BLEND * (1.0 - max(max(mx.x, mx.y), max(mx.z, mx.w))));
    }
    gl_FragColor = vec4(crt(res), 1.0);
}";
        }
    }
}
