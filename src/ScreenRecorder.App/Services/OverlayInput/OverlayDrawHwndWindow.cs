using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Numerics;
using ScreenRecorder.App.Helpers;
using ScreenRecorder.App.Services;
using ScreenRecorder.App.Services.Annotations;
using Windows.Graphics;
using Windows.Graphics.Capture;

namespace ScreenRecorder.App.Services.OverlayInput;

internal sealed class OverlayDrawHwndWindow : IDisposable
{
    private readonly AnnotationRenderer _annotations;

    private IntPtr _hwnd;
    private LayeredWindow? _layered;
    private RectInt32 _bounds;

    private bool _isDrawing;
    private bool _isInteractive;
    private bool _ignoreNextCaptureChanged;
    private IntPtr _zOrderAbove;
    private RectInt32 _passthroughRect;
    private bool _hasPassthroughRect;
    private long _lastPaintedVersion;
    private int _mouseLogRemaining = 24;

    private byte[]? _paintScratch;
    private int _paintScratchWidth;
    private int _paintScratchHeight;

    private static ushort _classAtom;
    private static readonly Dictionary<IntPtr, OverlayDrawHwndWindow> Instances = new();

    public OverlayDrawHwndWindow(AnnotationRenderer annotations)
    {
        _annotations = annotations ?? throw new ArgumentNullException(nameof(annotations));
        EnsureWindowClass();
    }

    public void ShowForCapture(GraphicsCaptureItem item, string selectedTab, RectInt32? crop)
    {
        var screenBounds = ComputeBounds(item, selectedTab, crop);
        _bounds = screenBounds;

        _annotations.EnsureSize(Math.Max(1, screenBounds.Width), Math.Max(1, screenBounds.Height));

        EnsureWindowCreated();

        // Size/position first.
        SetWindowPos(_hwnd, HWND_TOPMOST, screenBounds.X, screenBounds.Y, screenBounds.Width, screenBounds.Height,
            SWP_NOACTIVATE);

        // Default click-through.
        SetClickThrough(true);

        // Exclude from capture.
        TryExcludeHwndFromCapture(_hwnd);

        _layered ??= new LayeredWindow(_hwnd);

        // Paint a fully transparent frame BEFORE showing, so we never flash black.
        if (!TryPaintTransparentFrame())
        {
            Breadcrumbs.Write("OverlayDrawHwndWindow: initial paint failed; not showing window");
            return;
        }

        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        Breadcrumbs.Write($"OverlayDrawHwndWindow: shown bounds={screenBounds.X},{screenBounds.Y} {screenBounds.Width}x{screenBounds.Height}");
    }

    public void SetClickThrough(bool clickThrough)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowExStyle(_hwnd);
        exStyle |= WS_EX_LAYERED;
        exStyle |= WS_EX_TOOLWINDOW;
        exStyle |= WS_EX_TOPMOST;

        if (clickThrough)
        {
            exStyle |= WS_EX_TRANSPARENT;
        }
        else
        {
            exStyle &= ~WS_EX_TRANSPARENT;
        }

        SetWindowExStyle(_hwnd, exStyle);

        _isInteractive = !clickThrough;

        if (clickThrough)
        {
            // Leaving draw mode: ensure we aren't stuck in a capture/drawing state.
            CancelDrawing();
        }

        Breadcrumbs.Write($"OverlayDrawHwndWindow: SetClickThrough={(clickThrough ? "ON" : "OFF")} exStyle=0x{exStyle:X}");

        // Apply frame/style changes immediately (important for WS_EX_TRANSPARENT changes).
        SetWindowPos(
            _hwnd,
            HWND_TOPMOST,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        // IMPORTANT: Layered windows that are fully transparent (alpha=0 everywhere) can become effectively
        // "non-hittable" for input. When draw mode is enabled (clickThrough OFF) we paint a nearly invisible
        // background (alpha=1) to ensure the window can receive mouse input.
        if (_isInteractive)
        {
            TryPaintHittableBackground();
        }
        else
        {
            TryPaintTransparentFrame();
        }
    }

    public void SetTool(AnnotationTool tool) => _annotations.SetTool(tool);

    public void CancelDrawing()
    {
        _isDrawing = false;
        try
        {
            _ignoreNextCaptureChanged = true;
            ReleaseCapture();
        }
        catch { }

        try { _annotations.CancelActive(); } catch { }
        try { UpdateFromAnnotations(); } catch { }
    }

    public void SetZOrderBelow(IntPtr hwndAbove)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        _zOrderAbove = hwndAbove;
        if (_zOrderAbove == IntPtr.Zero)
        {
            return;
        }

        // Place this window directly below hwndAbove in the z-order (both are topmost).
        SetWindowPos(
            _hwnd,
            _zOrderAbove,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void SetInputPassthroughRect(RectInt32 screenRect)
    {
        _passthroughRect = screenRect;
        _hasPassthroughRect = screenRect.Width > 0 && screenRect.Height > 0;
    }

    public void Undo()
    {
        _annotations.Undo();
        UpdateFromAnnotations();
    }

    public void Clear()
    {
        _annotations.Clear();
        UpdateFromAnnotations();
    }

    public void UpdateFromAnnotations()
    {
        if (_layered is null)
        {
            return;
        }

        if (!_annotations.TryGetLatest(out var bytes, out var w, out var h, out var version))
        {
            if (_mouseLogRemaining > 0)
            {
                _mouseLogRemaining--;
                Breadcrumbs.Write("OverlayDrawHwndWindow: TryGetLatest=false (no bitmap yet)");
            }
            return;
        }

        if (bytes is null || w <= 0 || h <= 0)
        {
            if (_mouseLogRemaining > 0)
            {
                _mouseLogRemaining--;
                Breadcrumbs.Write($"OverlayDrawHwndWindow: latest invalid bytesNull={(bytes is null)} w={w} h={h}");
            }
            return;
        }

        var bytesToPaint = PreparePaintBytes(bytes, w, h);

        var ok = _layered.Update(bytesToPaint, w, h);
        if (!ok)
        {
            Breadcrumbs.Write($"OverlayDrawHwndWindow: UpdateLayeredWindow failed err={_layered.LastUpdateError}");
        }
        else if (version != _lastPaintedVersion)
        {
            _lastPaintedVersion = version;
            Breadcrumbs.Write($"OverlayDrawHwndWindow: painted overlay version={version}");
        }
    }

    public void Close()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        try { ShowWindow(_hwnd, SW_HIDE); } catch { }

        try
        {
            Instances.Remove(_hwnd);
            DestroyWindow(_hwnd);
        }
        catch { }

        _hwnd = IntPtr.Zero;
    }

    public void Dispose()
    {
        Close();
        _layered?.Dispose();
        _layered = null;
    }

    private bool TryPaintTransparentFrame()
    {
        try
        {
            if (_layered is null)
            {
                return false;
            }

            var w = Math.Max(1, _bounds.Width);
            var h = Math.Max(1, _bounds.Height);
            EnsurePaintScratch(w, h);
            Array.Clear(_paintScratch!, 0, w * h * 4);

            var ok = _layered.Update(_paintScratch!, w, h);
            if (!ok)
            {
                Breadcrumbs.Write($"OverlayDrawHwndWindow: initial UpdateLayeredWindow failed err={_layered.LastUpdateError}");
            }

            return ok;
        }
        catch (Exception ex)
        {
            Breadcrumbs.Write("OverlayDrawHwndWindow: TryPaintTransparentFrame exception");
            Breadcrumbs.Write(ex);
            return false;
        }
    }

    private void TryPaintHittableBackground()
    {
        try
        {
            if (_layered is null)
            {
                return;
            }

            var w = Math.Max(1, _bounds.Width);
            var h = Math.Max(1, _bounds.Height);
            EnsurePaintScratch(w, h);

            // Clear to transparent black, then set alpha=1 everywhere. This makes the window "hittable"
            // while remaining essentially invisible.
            var len = w * h * 4;
            Array.Clear(_paintScratch!, 0, len);
            for (var i = 3; i < len; i += 4)
            {
                _paintScratch![i] = 1;
            }

            var ok = _layered.Update(_paintScratch!, w, h);
            if (!ok)
            {
                Breadcrumbs.Write($"OverlayDrawHwndWindow: UpdateLayeredWindow failed (hittable bg) err={_layered.LastUpdateError}");
            }
        }
        catch (Exception ex)
        {
            Breadcrumbs.Write("OverlayDrawHwndWindow: TryPaintHittableBackground exception");
            Breadcrumbs.Write(ex);
        }
    }

    private void EnsurePaintScratch(int w, int h)
    {
        if (_paintScratch is not null && _paintScratchWidth == w && _paintScratchHeight == h)
        {
            return;
        }

        _paintScratchWidth = w;
        _paintScratchHeight = h;
        _paintScratch = new byte[Math.Max(1, w * h * 4)];
    }

    private byte[] PreparePaintBytes(byte[] sourceBgraPremul, int w, int h)
    {
        // For interactive input, ensure alpha is never 0 across the window so hit-testing works.
        if (!_isInteractive)
        {
            return sourceBgraPremul;
        }

        EnsurePaintScratch(w, h);

        var len = Math.Min(sourceBgraPremul.Length, w * h * 4);
        // Copy + force alpha>=1.
        // NOTE: The source buffer must remain unmodified because it's also used for burn-in.
        var dst = _paintScratch!;
        for (var i = 0; i < len; i += 4)
        {
            dst[i + 0] = sourceBgraPremul[i + 0];
            dst[i + 1] = sourceBgraPremul[i + 1];
            dst[i + 2] = sourceBgraPremul[i + 2];
            var a = sourceBgraPremul[i + 3];
            dst[i + 3] = a == 0 ? (byte)1 : a;
        }

        return dst;
    }

    private static RectInt32 ComputeBounds(GraphicsCaptureItem item, string selectedTab, RectInt32? crop)
    {
        if (string.Equals(selectedTab, "Window", StringComparison.OrdinalIgnoreCase))
        {
            if (Win32BoundsInterop.TryFindTopLevelWindowByTitle(item.DisplayName, out var hwnd)
                && Win32BoundsInterop.TryGetWindowExtendedFrameBounds(hwnd, out var wr))
            {
                return wr;
            }

            if (!Win32BoundsInterop.TryGetPrimaryMonitorBounds(out var pm))
            {
                pm = new RectInt32(0, 0, item.Size.Width, item.Size.Height);
            }

            var w = Math.Max(1, item.Size.Width);
            var h = Math.Max(1, item.Size.Height);
            var x = pm.X + Math.Max(0, (pm.Width - w) / 2);
            var y = pm.Y + Math.Max(0, (pm.Height - h) / 2);
            return new RectInt32(x, y, w, h);
        }

        // Screen/Region capture: try to locate the monitor that matches the capture item's pixel size.
        // This matters when a user selects a non-primary display via the system picker.
        RectInt32 primary;
        if (!Win32BoundsInterop.TryGetMonitorBoundsBySize(item.Size.Width, item.Size.Height, out primary))
        {
            if (!Win32BoundsInterop.TryGetPrimaryMonitorBounds(out primary))
            {
                primary = new RectInt32(0, 0, Math.Max(1, item.Size.Width), Math.Max(1, item.Size.Height));
            }
        }

        if (crop is { } c)
        {
            return new RectInt32(primary.X + c.X, primary.Y + c.Y, Math.Max(1, c.Width), Math.Max(1, c.Height));
        }

        // Screen mode (no crop): ensure the overlay/annotation pixel size matches the capture item pixel size.
        // Do NOT blindly use Win32 monitor bounds width/height here, because if they differ from
        // GraphicsCaptureItem.Size, AvRecorder will ignore the overlay (size mismatch).
        return new RectInt32(primary.X, primary.Y, Math.Max(1, item.Size.Width), Math.Max(1, item.Size.Height));
    }

    private void EnsureWindowCreated()
    {
        if (_hwnd != IntPtr.Zero)
        {
            return;
        }

        var exStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_TRANSPARENT;
        var style = WS_POPUP;

        _hwnd = CreateWindowExW(
            exStyle,
            ClassName,
            "OverlayDraw",
            style,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandleW(null),
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"CreateWindowExW failed err={err}");
        }

        Instances[_hwnd] = this;

        // Ensure it starts hidden.
        ShowWindow(_hwnd, SW_HIDE);

        Breadcrumbs.Write($"OverlayDrawHwndWindow: hwnd=0x{_hwnd.ToInt64():X}");
    }

    private static void EnsureWindowClass()
    {
        if (_classAtom != 0)
        {
            return;
        }

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcDelegate),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = GetModuleHandleW(null),
            hIcon = IntPtr.Zero,
            hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = ClassName,
            hIconSm = IntPtr.Zero
        };

        _classAtom = RegisterClassExW(ref wc);
        if (_classAtom == 0)
        {
            var err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"RegisterClassExW failed err={err}");
        }
    }

    private static readonly WndProcCallback WndProcDelegate = WndProcImpl;

    private static IntPtr WndProcImpl(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (Instances.TryGetValue(hwnd, out var inst))
            {
                return inst.WndProc(hwnd, msg, wParam, lParam);
            }
        }
        catch { }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
            {
                if (_hasPassthroughRect)
                {
                    var p = GetScreenPoint(lParam);
                    if (p.X >= _passthroughRect.X && p.X < _passthroughRect.X + _passthroughRect.Width &&
                        p.Y >= _passthroughRect.Y && p.Y < _passthroughRect.Y + _passthroughRect.Height)
                    {
                        return new IntPtr(HTTRANSPARENT);
                    }
                }

                return new IntPtr(HTCLIENT);
            }
            case WM_LBUTTONDOWN:
            {
                if (_mouseLogRemaining > 0)
                {
                    _mouseLogRemaining--;
                    Breadcrumbs.Write("OverlayDrawHwndWindow: WM_LBUTTONDOWN");
                }
                _isDrawing = true;
                SetCapture(hwnd);
                var p = GetClientPoint(lParam);
                OnPointerDown(p);
                return IntPtr.Zero;
            }
            case WM_MOUSEMOVE:
            {
                if (!_isDrawing)
                {
                    return IntPtr.Zero;
                }

                // Defensive: if we ever miss WM_LBUTTONUP, stop drawing as soon as we notice the button
                // is no longer down.
                var keys = unchecked((uint)wParam.ToInt64());
                if ((keys & MK_LBUTTON) == 0)
                {
                    var pUp = GetClientPoint(lParam);
                    _isDrawing = false;

                    // Commit the stroke BEFORE releasing capture.
                    OnPointerUp(pUp);
                    try
                    {
                        _ignoreNextCaptureChanged = true;
                        ReleaseCapture();
                    }
                    catch { }
                    return IntPtr.Zero;
                }

                if (_mouseLogRemaining > 0)
                {
                    _mouseLogRemaining--;
                    Breadcrumbs.Write("OverlayDrawHwndWindow: WM_MOUSEMOVE (drawing)");
                }

                var p = GetClientPoint(lParam);
                OnPointerMove(p);
                return IntPtr.Zero;
            }
            case WM_LBUTTONUP:
            {
                if (_mouseLogRemaining > 0)
                {
                    _mouseLogRemaining--;
                    Breadcrumbs.Write("OverlayDrawHwndWindow: WM_LBUTTONUP");
                }
                if (_isDrawing)
                {
                    var p = GetClientPoint(lParam);

                    // Commit the stroke BEFORE releasing capture.
                    OnPointerUp(p);

                    _isDrawing = false;
                    try
                    {
                        _ignoreNextCaptureChanged = true;
                        ReleaseCapture();
                    }
                    catch { }
                }
                return IntPtr.Zero;
            }
            case WM_RBUTTONDOWN:
            {
                // Quick escape hatch: right-click cancels the active stroke without committing.
                CancelDrawing();
                return IntPtr.Zero;
            }
            case WM_KEYDOWN:
            {
                var vk = unchecked((int)wParam.ToInt64());
                if (vk == VK_ESCAPE)
                {
                    CancelDrawing();
                    return IntPtr.Zero;
                }
                break;
            }
            case WM_CANCELMODE:
            {
                _isDrawing = false;
                try { ReleaseCapture(); } catch { }
                try { _annotations.CancelActive(); } catch { }
                try { UpdateFromAnnotations(); } catch { }
                return IntPtr.Zero;
            }
            case WM_CAPTURECHANGED:
            {
                if (_ignoreNextCaptureChanged)
                {
                    _ignoreNextCaptureChanged = false;
                    return IntPtr.Zero;
                }

                if (_isDrawing)
                {
                    CancelDrawing();
                }
                return IntPtr.Zero;
            }
            case WM_KILLFOCUS:
            {
                CancelDrawing();
                return IntPtr.Zero;
            }
            case WM_ACTIVATEAPP:
            {
                var active = wParam != IntPtr.Zero;
                if (!active)
                {
                    CancelDrawing();
                }
                return IntPtr.Zero;
            }
            case WM_DESTROY:
            {
                try { Instances.Remove(hwnd); } catch { }
                return IntPtr.Zero;
            }
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static (int X, int Y) GetScreenPoint(IntPtr lParam)
    {
        var xy = unchecked((uint)lParam.ToInt64());
        var x = unchecked((short)(xy & 0xFFFF));
        var y = unchecked((short)((xy >> 16) & 0xFFFF));
        return (x, y);
    }

    private void OnPointerDown(Vector2 clientPx)
    {
        var p = ClampToBounds(clientPx);
        _annotations.PointerDown(p);
        UpdateFromAnnotations();
    }

    private void OnPointerMove(Vector2 clientPx)
    {
        var p = ClampToBounds(clientPx);
        _annotations.PointerMove(p);
        UpdateFromAnnotations();
    }

    private void OnPointerUp(Vector2 clientPx)
    {
        var p = ClampToBounds(clientPx);
        _annotations.PointerUp(p);
        UpdateFromAnnotations();
    }

    private Vector2 ClampToBounds(Vector2 clientPx)
    {
        // We size the window to exactly match the overlay buffer in physical pixels.
        var x = (float)Math.Clamp(clientPx.X, 0, Math.Max(0, _bounds.Width - 1));
        var y = (float)Math.Clamp(clientPx.Y, 0, Math.Max(0, _bounds.Height - 1));
        return new Vector2(x, y);
    }

    private static Vector2 GetClientPoint(IntPtr lParam)
    {
        var xy = unchecked((uint)lParam.ToInt64());
        var x = unchecked((short)(xy & 0xFFFF));
        var y = unchecked((short)((xy >> 16) & 0xFFFF));
        return new Vector2(x, y);
    }

    private static bool TryExcludeHwndFromCapture(IntPtr hwnd)
    {
        try
        {
            return SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        }
        catch
        {
            return false;
        }
    }

    private const string ClassName = "PrismCapture.OverlayDrawHwnd";

    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_CANCELMODE = 0x001F;
    private const int WM_CAPTURECHANGED = 0x0215;
    private const int WM_KILLFOCUS = 0x0008;
    private const int WM_ACTIVATEAPP = 0x001C;
    private const int WM_DESTROY = 0x0002;

    private const int VK_ESCAPE = 0x1B;
    private const uint MK_LBUTTON = 0x0001;

    private const int HTTRANSPARENT = -1;
    private const int HTCLIENT = 1;

    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const uint WS_POPUP = 0x80000000;

    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;

    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private const int GWL_EXSTYLE = -20;

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    private static long GetWindowExStyle(IntPtr hwnd)
    {
        if (nint.Size == 8)
        {
            return GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();
        }

        return GetWindowLongW(hwnd, GWL_EXSTYLE);
    }

    private static void SetWindowExStyle(IntPtr hwnd, long exStyle)
    {
        if (nint.Size == 8)
        {
            SetWindowLongPtrW(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));
            return;
        }

        SetWindowLongW(hwnd, GWL_EXSTYLE, unchecked((int)exStyle));
    }

    private delegate IntPtr WndProcCallback(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    private const int IDC_ARROW = 32512;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int X,
        int Y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
