using System;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace SlickLadder.Core.Tests;

/// <summary>
/// An OpenGL context current on the calling thread (hidden window, WGL) and a Skia GPU context
/// on it. TryCreate returns null where there's no usable OpenGL, such as CI runners
/// </summary>
internal sealed class GlContext : IDisposable
{
    private IntPtr _window;
    private IntPtr _dc;
    private IntPtr _gl;

    public GRContext Gpu { get; private set; } = null!;

    private GlContext() { }

    public static GlContext? TryCreate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var context = new GlContext();
        context._window = CreateWindowEx(0, "STATIC", "", WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        context._dc = GetDC(context._window);
        var format = new PixelFormatDescriptor
        {
            Size = (ushort)Marshal.SizeOf<PixelFormatDescriptor>(),
            Version = 1,
            Flags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER,
            ColorBits = 32,
            DepthBits = 24,
            StencilBits = 8,
        };
        var index = ChoosePixelFormat(context._dc, ref format);
        if (index != 0 && SetPixelFormat(context._dc, index, ref format))
        {
            context._gl = wglCreateContext(context._dc);
        }
        // GDI's software OpenGL 1.1 has no GRGlInterface or GRContext
        using var gl = context._gl != IntPtr.Zero && wglMakeCurrent(context._dc, context._gl) ? GRGlInterface.Create() : null;
        var gpu = gl != null ? GRContext.CreateGl(gl) : null;
        if (gpu == null)
        {
            context.Dispose();
            return null;
        }
        context.Gpu = gpu;
        return context;
    }

    public void Dispose()
    {
        Gpu?.Dispose();
        if (_gl != IntPtr.Zero)
        {
            wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            wglDeleteContext(_gl);
        }
        if (_dc != IntPtr.Zero) ReleaseDC(_window, _dc);
        if (_window != IntPtr.Zero) DestroyWindow(_window);
        (_gl, _dc, _window) = (IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    private const uint WS_POPUP = 0x80000000;
    private const uint PFD_DOUBLEBUFFER = 0x1;
    private const uint PFD_DRAW_TO_WINDOW = 0x4;
    private const uint PFD_SUPPORT_OPENGL = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelFormatDescriptor
    {
        public ushort Size, Version;
        public uint Flags;
        public byte PixelType, ColorBits, RedBits, RedShift, GreenBits, GreenShift, BlueBits, BlueShift;
        public byte AlphaBits, AlphaShift, AccumBits, AccumRedBits, AccumGreenBits, AccumBlueBits, AccumAlphaBits;
        public byte DepthBits, StencilBits, AuxBuffers, LayerType, Reserved;
        public uint LayerMask, VisibleMask, DamageMask;
    }

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32")] private static extern int ChoosePixelFormat(IntPtr dc, ref PixelFormatDescriptor format);
    [DllImport("gdi32")] private static extern bool SetPixelFormat(IntPtr dc, int index, ref PixelFormatDescriptor format);
    [DllImport("opengl32")] private static extern IntPtr wglCreateContext(IntPtr dc);
    [DllImport("opengl32")] private static extern bool wglMakeCurrent(IntPtr dc, IntPtr gl);
    [DllImport("opengl32")] private static extern bool wglDeleteContext(IntPtr gl);
}
