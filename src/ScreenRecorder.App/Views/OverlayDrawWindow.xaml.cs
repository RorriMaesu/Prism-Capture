using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using ScreenRecorder.App.Helpers;
using ScreenRecorder.App.Services.Annotations;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Microsoft.UI.Xaml.Input;
using System.Numerics;
using ScreenRecorder.App.Services;

namespace ScreenRecorder.App.Views;

public sealed partial class OverlayDrawWindow : Window
{
    private readonly AnnotationRenderer _annotations;
    private LayeredWindow? _layered;

    private RectInt32 _bounds;

    private bool _isDrawing;
    private uint? _pointerId;

    public OverlayDrawWindow(AnnotationRenderer annotations)
    {
        _annotations = annotations ?? throw new ArgumentNullException(nameof(annotations));
        InitializeComponent();

        Root.PointerPressed += OnPointerPressed;
        Root.PointerMoved += OnPointerMoved;
        Root.PointerReleased += OnPointerReleased;
        Root.PointerCanceled += OnPointerCanceled;
    }

    public void ShowForCapture(GraphicsCaptureItem item, string selectedTab, RectInt32? crop)
    {
        // Determine the on-screen bounds we should cover.
        // - Screen: primary monitor bounds
        // - Region: crop rect within primary monitor
        // - Window: best-effort match by window title

        RectInt32 screenBounds;

        if (string.Equals(selectedTab, "Window", StringComparison.OrdinalIgnoreCase))
        {
            if (Win32BoundsInterop.TryFindTopLevelWindowByTitle(item.DisplayName, out var hwnd)
                && Win32BoundsInterop.TryGetWindowExtendedFrameBounds(hwnd, out var wr))
            {
                screenBounds = wr;
            }
            else
            {
                // Fallback: center a window-sized overlay on the primary monitor.
                if (!Win32BoundsInterop.TryGetPrimaryMonitorBounds(out var pm))
                {
                    pm = new RectInt32(0, 0, item.Size.Width, item.Size.Height);
                }

                var w = Math.Max(1, item.Size.Width);
                var h = Math.Max(1, item.Size.Height);
                var x = pm.X + Math.Max(0, (pm.Width - w) / 2);
                var y = pm.Y + Math.Max(0, (pm.Height - h) / 2);
                screenBounds = new RectInt32(x, y, w, h);
            }
        }
        else
        {
            if (!Win32BoundsInterop.TryGetMonitorBoundsBySize(item.Size.Width, item.Size.Height, out var pm))
            {
                if (!Win32BoundsInterop.TryGetPrimaryMonitorBounds(out pm))
                {
                    pm = new RectInt32(0, 0, Math.Max(1, item.Size.Width), Math.Max(1, item.Size.Height));
                }
            }

            if (crop is { } c)
            {
                screenBounds = new RectInt32(pm.X + c.X, pm.Y + c.Y, Math.Max(1, c.Width), Math.Max(1, c.Height));
            }
            else
            {
                // Screen mode (no crop): keep origin from the monitor bounds, but size from capture pixels.
                screenBounds = new RectInt32(pm.X, pm.Y, Math.Max(1, item.Size.Width), Math.Max(1, item.Size.Height));
            }
        }

        _bounds = screenBounds;

        // Ensure the annotation backing store exists at the same pixel size as the overlay window.
        // Without this, the layered window never gets an initial transparent frame and will appear black.
        _annotations.EnsureSize(Math.Max(1, screenBounds.Width), Math.Max(1, screenBounds.Height));

        var hwndOverlay = WindowInterop.GetWindowHandle(this);

        // Exclude the overlay from capture so Screen capture doesn’t double-bake.
        _ = WindowInterop.TryExcludeWindowFromCapture(this);

        // Remove borders/titlebar and make it always on top.
        var appWindow = GetAppWindowForCurrentWindow();
        var presenter = appWindow.Presenter as OverlappedPresenter;
        presenter?.SetBorderAndTitleBar(false, false);
        if (presenter is not null)
        {
            presenter.IsAlwaysOnTop = true;
        }

        // Position and size to target bounds (physical pixels).
        SetWindowBounds(hwndOverlay, screenBounds);

        // Make layered + toolwindow; click-through can be toggled externally.
        SetLayeredStyles(hwndOverlay, clickThrough: true);

        _layered ??= new LayeredWindow(hwndOverlay);

        // Start visible.
        Activate();

        Breadcrumbs.Write($"OverlayDrawWindow: shown bounds={screenBounds.X},{screenBounds.Y} {screenBounds.Width}x{screenBounds.Height} clickThrough=true");

        // Initial paint (empty). If this fails, close the overlay to avoid blocking the screen.
        if (!TryPaintTransparentFrame())
        {
            Breadcrumbs.Write("OverlayDrawWindow: initial paint failed; closing overlay window to avoid black screen");
            try { Close(); } catch { }
            return;
        }
    }

    public void HideOverlay()
    {
        try
        {
            ClearOverlay();
            SetLayeredStyles(WindowInterop.GetWindowHandle(this), clickThrough: true);
        }
        catch { }

        try { Close(); } catch { }
    }

    public void SetClickThrough(bool clickThrough)
    {
        try
        {
            SetLayeredStyles(WindowInterop.GetWindowHandle(this), clickThrough);

            // If we are switching into draw mode (non click-through), bring to front.
            if (!clickThrough)
            {
                try { Activate(); } catch { }
            }
        }
        catch { }
    }

    public void SetTool(AnnotationTool tool)
    {
        _annotations.SetTool(tool);
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
        if (!_annotations.TryGetLatest(out var bytes, out var w, out var h, out _))
        {
            return;
        }

        if (bytes is null || w <= 0 || h <= 0)
        {
            return;
        }

        if (_layered is null)
        {
            return;
        }

        var ok = _layered.Update(bytes, w, h);
        if (!ok)
        {
            Breadcrumbs.Write($"OverlayDrawWindow: UpdateLayeredWindow failed (err={_layered.LastUpdateError}); hiding overlay");
            try { Close(); } catch { }
        }
    }

    public void ClearOverlay()
    {
        if (_annotations.TryGetLatest(out _, out var w, out var h, out _))
        {
            var clear = new byte[Math.Max(1, w * h * 4)];
            _layered?.Update(clear, w, h);
        }
    }

    private bool TryPaintTransparentFrame()
    {
        try
        {
            if (!_annotations.TryGetLatest(out _, out var w, out var h, out _))
            {
                return false;
            }

            var clear = new byte[Math.Max(1, w * h * 4)];

            if (_layered is null)
            {
                return false;
            }

            var ok = _layered.Update(clear, w, h);
            if (!ok)
            {
                Breadcrumbs.Write($"OverlayDrawWindow: UpdateLayeredWindow initial failed (err={_layered.LastUpdateError})");
            }
            return ok;
        }
        catch (Exception ex)
        {
            Breadcrumbs.Write("OverlayDrawWindow: TryPaintTransparentFrame exception");
            Breadcrumbs.Write(ex);
            return false;
        }
    }

    private AppWindow GetAppWindowForCurrentWindow()
    {
        var hwnd = WindowInterop.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    private static void SetWindowBounds(IntPtr hwnd, RectInt32 bounds)
    {
        SetWindowPos(hwnd, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOACTIVATE);
    }

    private static void SetLayeredStyles(IntPtr hwnd, bool clickThrough)
    {
        var style = GetWindowExStyle(hwnd);

        style |= WS_EX_LAYERED;
        style |= WS_EX_TOOLWINDOW;

        if (clickThrough)
        {
            style |= WS_EX_TRANSPARENT;
        }
        else
        {
            style &= ~WS_EX_TRANSPARENT;
        }

        SetWindowExStyle(hwnd, style);

        // Keep it topmost.
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private static long GetWindowExStyle(IntPtr hwnd)
    {
        if (nint.Size == 8)
        {
            return GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        }

        return GetWindowLong(hwnd, GWL_EXSTYLE);
    }

    private static void SetWindowExStyle(IntPtr hwnd, long exStyle)
    {
        if (nint.Size == 8)
        {
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));
            return;
        }

        SetWindowLong(hwnd, GWL_EXSTYLE, unchecked((int)exStyle));
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Breadcrumbs.Write("OverlayDrawWindow: PointerPressed");
        _pointerId = e.Pointer.PointerId;
        _isDrawing = true;
        Root.CapturePointer(e.Pointer);

        if (TryGetPixelPoint(e.GetCurrentPoint(Root).Position, out var p))
        {
            _annotations.PointerDown(p);
            UpdateFromAnnotations();
        }

        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDrawing || _pointerId is null || e.Pointer.PointerId != _pointerId.Value)
        {
            return;
        }

        if (TryGetPixelPoint(e.GetCurrentPoint(Root).Position, out var p))
        {
            _annotations.PointerMove(p);
            UpdateFromAnnotations();
        }

        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        Breadcrumbs.Write("OverlayDrawWindow: PointerReleased");
        if (!_isDrawing || _pointerId is null || e.Pointer.PointerId != _pointerId.Value)
        {
            return;
        }

        if (TryGetPixelPoint(e.GetCurrentPoint(Root).Position, out var p))
        {
            _annotations.PointerUp(p);
            UpdateFromAnnotations();
        }

        _isDrawing = false;
        _pointerId = null;
        Root.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        Breadcrumbs.Write("OverlayDrawWindow: PointerCanceled");
        if (_pointerId is null || e.Pointer.PointerId != _pointerId.Value)
        {
            return;
        }

        _isDrawing = false;
        _pointerId = null;
        Root.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private bool TryGetPixelPoint(Windows.Foundation.Point dip, out Vector2 pixel)
    {
        pixel = default;

        var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        var x = (float)Math.Clamp(dip.X * scale, 0, Math.Max(0, _bounds.Width - 1));
        var y = (float)Math.Clamp(dip.Y * scale, 0, Math.Max(0, _bounds.Height - 1));
        pixel = new Vector2(x, y);
        return true;
    }

    private const int GWL_EXSTYLE = -20;

    private const long WS_EX_LAYERED = 0x00080000L;
    private const long WS_EX_TRANSPARENT = 0x00000020L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;

    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
