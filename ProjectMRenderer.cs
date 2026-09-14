using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace lifeviz;

// Thread-affine, private GL context. Only the owning render/UI thread touches native state.
// BGRA readback at the scene resolution enters the existing CPU/GPU source compositor.
internal sealed class ProjectMRenderer : IDisposable
{
    private HwndSource? _window;
    private IntPtr _dc, _context, _instance;
    private uint _fbo, _texture;
    private int _width, _height;
    private byte[] _bottomUp = Array.Empty<byte>(), _pixels = Array.Empty<byte>();
    private readonly Native.FailedCallback _failed;
    private string? _loadError;
    private GlGen? _genFbo;
    private GlBind? _bindFbo;
    private GlDelete? _deleteFbo;
    private GlAttach? _attach;
    private GlStatus? _status;

    public ProjectMRenderer()
    {
        _failed = (_, message, _) => _loadError = Marshal.PtrToStringUTF8(message) ?? "Preset loading failed.";
        IntPtr previousContext = wglGetCurrentContext(), previousDc = wglGetCurrentDC();
        try
        {
            if (!Environment.Is64BitProcess) throw new NotSupportedException("projectM requires the Windows x64 build.");
            _window = new HwndSource(new HwndSourceParameters("LifeViz projectM renderer")
            {
                Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000), WindowClassStyle = 0x20
            });
            _dc = GetDC(_window.Handle);
            var format = new PixelFormatDescriptor
            {
                Size = (ushort)Marshal.SizeOf<PixelFormatDescriptor>(), Version = 1,
                Flags = 0x25, ColorBits = 32, AlphaBits = 8, DepthBits = 24
            };
            int pixelFormat = ChoosePixelFormat(_dc, ref format);
            if (pixelFormat == 0 || !SetPixelFormat(_dc, pixelFormat, ref format)) throw new InvalidOperationException("Could not select an OpenGL pixel format.");
            _context = wglCreateContext(_dc);
            MakeCurrent();
            var create = GetGl<CreateContext>("wglCreateContextAttribsARB");
            IntPtr modern = create(_dc, IntPtr.Zero, new[] { 0x2091, 3, 0x2092, 3, 0x9126, 1, 0 });
            if (modern == IntPtr.Zero) throw new NotSupportedException("projectM requires an OpenGL 3.3 capable graphics driver.");
            wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            wglDeleteContext(_context);
            _context = modern;
            MakeCurrent();
            _genFbo = GetGl<GlGen>("glGenFramebuffers");
            _bindFbo = GetGl<GlBind>("glBindFramebuffer");
            _deleteFbo = GetGl<GlDelete>("glDeleteFramebuffers");
            _attach = GetGl<GlAttach>("glFramebufferTexture2D");
            _status = GetGl<GlStatus>("glCheckFramebufferStatus");
            _instance = Native.projectm_create();
            if (_instance == IntPtr.Zero) throw new InvalidOperationException("projectM could not initialize its OpenGL renderer.");
            Native.projectm_set_preset_switch_failed_event_callback(_instance, _failed, IntPtr.Zero);
            Native.projectm_set_preset_locked(_instance, true);
            Native.projectm_set_hard_cut_enabled(_instance, false);
            Native.projectm_set_frame_time(_instance, 0);
            SetTexturePaths(new[] { ProjectMLibrary.TextureRoot });
        }
        catch { Dispose(); throw; }
        finally { wglMakeCurrent(previousDc, previousContext); }
    }

    public string? Load(string path, double time, double transitionSeconds, bool first)
    {
        using var scope = Enter();
        _loadError = null;
        Native.projectm_set_frame_time(_instance, time);
        Native.projectm_set_soft_cut_duration(_instance, transitionSeconds);
        // Imported presets can keep their textures in a sibling textures directory.
        string full = ProjectMLibrary.Resolve(path);
        string folder = Path.GetDirectoryName(full)!;
        string[] paths = Path.IsPathFullyQualified(path)
            ? new[] { folder, Path.Combine(folder, "textures"), ProjectMLibrary.TextureRoot }
            : new[] { ProjectMLibrary.TextureRoot };
        string key = string.Join("|", paths);
        if (_texturePaths != key) SetTexturePaths(paths);
        Native.projectm_load_preset_file(_instance, full, !first && transitionSeconds > 0);
        return _loadError;
    }

    private string? _texturePaths;
    private void SetTexturePaths(string[] paths)
    {
        IntPtr[] utf8 = Array.ConvertAll(paths, Marshal.StringToCoTaskMemUTF8);
        try { Native.projectm_set_texture_search_paths(_instance, utf8, (nuint)utf8.Length); }
        finally { foreach (IntPtr p in utf8) Marshal.FreeCoTaskMem(p); }
        _texturePaths = string.Join("|", paths);
    }

    public byte[] Render(int width, int height, double time, float[] audio)
    {
        using var scope = Enter();
        if (width != _width || height != _height)
        {
            if (_fbo != 0) _deleteFbo!(1, ref _fbo);
            if (_texture != 0) glDeleteTextures(1, ref _texture);
            _width = width; _height = height;
            _bottomUp = new byte[checked(width * height * 4)];
            _pixels = new byte[_bottomUp.Length];
            glGenTextures(1, out _texture);
            glBindTexture(0x0DE1, _texture);
            glTexParameteri(0x0DE1, 0x2801, 0x2601);
            glTexParameteri(0x0DE1, 0x2800, 0x2601);
            glTexImage2D(0x0DE1, 0, 0x8058, width, height, 0, 0x1908, 0x1401, IntPtr.Zero);
            _genFbo!(1, out _fbo);
            _bindFbo!(0x8D40, _fbo);
            _attach!(0x8D40, 0x8CE0, 0x0DE1, _texture, 0);
            if (_status!(0x8D40) != 0x8CD5) throw new InvalidOperationException("Could not create the projectM render surface.");
            Native.projectm_set_window_size(_instance, (nuint)width, (nuint)height);
        }
        Native.projectm_set_frame_time(_instance, time);
        Native.projectm_pcm_add_float(_instance, audio, (uint)audio.Length, 1);
        Native.projectm_opengl_render_frame_fbo(_instance, _fbo);
        _bindFbo!(0x8D40, _fbo);
        glReadBuffer(0x8CE0);
        glReadPixels(0, 0, width, height, 0x80E1, 0x1401, _bottomUp);
        int stride = width * 4;
        for (int y = 0; y < height; y++) Buffer.BlockCopy(_bottomUp, (height - y - 1) * stride, _pixels, y * stride, stride);
        // projectM's output is a full-frame source; some presets leave arbitrary alpha.
        for (int i = 3; i < _pixels.Length; i += 4) _pixels[i] = 255;
        return _pixels;
    }

    private void MakeCurrent()
    {
        if (_context == IntPtr.Zero || !wglMakeCurrent(_dc, _context)) throw new InvalidOperationException("Could not activate projectM's OpenGL context.");
    }

    private ContextScope Enter() => new(this);
    private readonly struct ContextScope : IDisposable
    {
        private readonly IntPtr _dc, _context;
        public ContextScope(ProjectMRenderer renderer)
        {
            _dc = wglGetCurrentDC(); _context = wglGetCurrentContext(); renderer.MakeCurrent();
        }
        public void Dispose() => wglMakeCurrent(_dc, _context);
    }

    public void Dispose()
    {
        if (_context != IntPtr.Zero)
        {
            using (Enter())
            {
                if (_instance != IntPtr.Zero) Native.projectm_destroy(_instance);
                _instance = IntPtr.Zero;
                if (_fbo != 0) _deleteFbo?.Invoke(1, ref _fbo);
                if (_texture != 0) glDeleteTextures(1, ref _texture);
            }
            if (wglGetCurrentContext() == _context) wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            wglDeleteContext(_context); _context = IntPtr.Zero;
        }
        if (_dc != IntPtr.Zero && _window != null) ReleaseDC(_window.Handle, _dc);
        _dc = IntPtr.Zero; _window?.Dispose(); _window = null;
    }

    private static T GetGl<T>(string name) where T : Delegate
    {
        IntPtr p = wglGetProcAddress(name);
        if (p == IntPtr.Zero || p.ToInt64() is -1 or 1 or 2 or 3) throw new NotSupportedException($"OpenGL entry point unavailable: {name}");
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr CreateContext(IntPtr dc, IntPtr share, int[] attributes);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlGen(int n, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlDelete(int n, ref uint value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlBind(uint target, uint value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlAttach(uint target, uint attachment, uint textureTarget, uint texture, int level);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint GlStatus(uint target);

    [StructLayout(LayoutKind.Sequential)] private struct PixelFormatDescriptor
    {
        public ushort Size, Version;
        public uint Flags;
        public byte PixelType, ColorBits, RedBits, RedShift, GreenBits, GreenShift, BlueBits, BlueShift, AlphaBits, AlphaShift;
        public byte AccumBits, AccumRedBits, AccumGreenBits, AccumBlueBits, AccumAlphaBits, DepthBits, StencilBits, AuxBuffers, LayerType, Reserved;
        public uint LayerMask, VisibleMask, DamageMask;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int ChoosePixelFormat(IntPtr dc, ref PixelFormatDescriptor format);
    [DllImport("gdi32.dll")] private static extern bool SetPixelFormat(IntPtr dc, int pixelFormat, ref PixelFormatDescriptor format);
    [DllImport("opengl32.dll")] private static extern IntPtr wglCreateContext(IntPtr dc);
    [DllImport("opengl32.dll")] private static extern bool wglDeleteContext(IntPtr context);
    [DllImport("opengl32.dll")] private static extern bool wglMakeCurrent(IntPtr dc, IntPtr context);
    [DllImport("opengl32.dll")] private static extern IntPtr wglGetCurrentContext();
    [DllImport("opengl32.dll")] private static extern IntPtr wglGetCurrentDC();
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr wglGetProcAddress(string name);
    [DllImport("opengl32.dll")] private static extern void glGenTextures(int n, out uint value);
    [DllImport("opengl32.dll")] private static extern void glDeleteTextures(int n, ref uint value);
    [DllImport("opengl32.dll")] private static extern void glBindTexture(uint target, uint value);
    [DllImport("opengl32.dll")] private static extern void glTexParameteri(uint target, uint name, int value);
    [DllImport("opengl32.dll")] private static extern void glTexImage2D(uint target, int level, int format, int width, int height, int border, uint pixelFormat, uint type, IntPtr data);
    [DllImport("opengl32.dll")] private static extern void glReadBuffer(uint buffer);
    [DllImport("opengl32.dll")] private static extern void glReadPixels(int x, int y, int width, int height, uint format, uint type, [Out] byte[] data);

    private static class Native
    {
        private const string Library = "lifeviz_projectm";
        static Native() => NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, _, _) =>
            name == Library ? NativeLibrary.Load(Path.Combine(ProjectMLibrary.Root, "projectM-4.dll")) : IntPtr.Zero);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FailedCallback(IntPtr filename, IntPtr message, IntPtr data);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr projectm_create();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void projectm_destroy(IntPtr instance);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void projectm_set_frame_time(IntPtr instance, double seconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void projectm_set_soft_cut_duration(IntPtr instance, double seconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void projectm_set_window_size(IntPtr instance, nuint width, nuint height);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void projectm_set_preset_locked(IntPtr instance, [MarshalAs(UnmanagedType.I1)] bool value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void projectm_set_hard_cut_enabled(IntPtr instance, [MarshalAs(UnmanagedType.I1)] bool value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void projectm_set_texture_search_paths(IntPtr instance, IntPtr[] paths, nuint count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void projectm_set_preset_switch_failed_event_callback(IntPtr instance, FailedCallback callback, IntPtr data);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void projectm_load_preset_file(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.I1)] bool smooth);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void projectm_pcm_add_float(IntPtr instance, float[] samples, uint count, uint channels);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void projectm_opengl_render_frame_fbo(IntPtr instance, uint fbo);
    }
}
