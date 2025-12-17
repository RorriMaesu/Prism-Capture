using System;
using System.Runtime.InteropServices;

namespace ScreenRecorder.App.Helpers;

internal sealed class LayeredWindow : IDisposable
{
    private readonly IntPtr _hwnd;

    private IntPtr _screenDc;
    private IntPtr _memDc;
    private IntPtr _hBitmap;
    private IntPtr _oldBitmap;
    private IntPtr _bits;
    private int _width;
    private int _height;

    public int LastUpdateError { get; private set; }

    public LayeredWindow(IntPtr hwnd)
    {
        _hwnd = hwnd;
    }

    public void EnsureSize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (_width == width && _height == height && _memDc != IntPtr.Zero && _hBitmap != IntPtr.Zero)
        {
            return;
        }

        CleanupGdi();

        _width = width;
        _height = height;

        _screenDc = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(_screenDc);

        _cachedBmi = new BITMAPINFO();
        _cachedBmi.bmiHeader.biSize = Marshal.SizeOf<BITMAPINFOHEADER>();
        _cachedBmi.bmiHeader.biWidth = width;
        _cachedBmi.bmiHeader.biHeight = -height; // top-down
        _cachedBmi.bmiHeader.biPlanes = 1;
        _cachedBmi.bmiHeader.biBitCount = 32;
        _cachedBmi.bmiHeader.biCompression = BI_RGB;

        _hBitmap = CreateDIBSection(_screenDc, ref _cachedBmi, DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
        if (_hBitmap == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreateDIBSection failed.");
        }

        _oldBitmap = SelectObject(_memDc, _hBitmap);
    }

    public bool Update(byte[] bgraPremul, int width, int height)
    {
        if (bgraPremul is null) throw new ArgumentNullException(nameof(bgraPremul));
        EnsureSize(width, height);

        var bytesExpected = width * height * 4;
        if (bgraPremul.Length < bytesExpected)
        {
            throw new ArgumentException("Overlay buffer too small.", nameof(bgraPremul));
        }

        // Copy into the DIB section (premultiplied BGRA).
        Marshal.Copy(bgraPremul, 0, _bits, bytesExpected);

        var ptSrc = new POINT(0, 0);
        var size = new SIZE(width, height);

        // Window position is already set via SetWindowPos; let ULW use current top-left.
        var ptDst = new POINT(0, 0);

        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA
        };

        LastUpdateError = 0;
        var ok = UpdateLayeredWindow(_hwnd, _screenDc, IntPtr.Zero, ref size, _memDc, ref ptSrc, 0, ref blend, ULW_ALPHA);
        if (!ok)
        {
            LastUpdateError = Marshal.GetLastWin32Error();
        }

        return ok;
    }

    // We reuse the same BITMAPINFO for SetDIBitsToDevice.
    private BITMAPINFO _cachedBmi;

    private void CleanupGdi()
    {
        try
        {
            if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
            {
                SelectObject(_memDc, _oldBitmap);
            }
        }
        catch { }

        if (_hBitmap != IntPtr.Zero)
        {
            try { DeleteObject(_hBitmap); } catch { }
            _hBitmap = IntPtr.Zero;
        }

        _bits = IntPtr.Zero;

        if (_memDc != IntPtr.Zero)
        {
            try { DeleteDC(_memDc); } catch { }
            _memDc = IntPtr.Zero;
        }

        if (_screenDc != IntPtr.Zero)
        {
            try { ReleaseDC(IntPtr.Zero, _screenDc); } catch { }
            _screenDc = IntPtr.Zero;
        }

        _oldBitmap = IntPtr.Zero;
        _cachedBmi = default;
    }

    public void Dispose()
    {
        CleanupGdi();
    }

    private const uint DIB_RGB_COLORS = 0;
    private const uint BI_RGB = 0;

    private const byte AC_SRC_OVER = 0;
    private const byte AC_SRC_ALPHA = 1;
    private const int ULW_ALPHA = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;

        public SIZE(int x, int y)
        {
            cx = x;
            cy = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, [In] ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr hdcDst,
        IntPtr pptDst,
        ref SIZE psize,
        IntPtr hdcSrc,
        ref POINT pptSrc,
        int crKey,
        ref BLENDFUNCTION pblend,
        int dwFlags);
}
