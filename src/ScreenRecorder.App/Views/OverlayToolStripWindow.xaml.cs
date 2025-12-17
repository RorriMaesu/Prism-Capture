using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition;
using System.Runtime.InteropServices;
using ScreenRecorder.App.Helpers;
using ScreenRecorder.App.Services;
using ScreenRecorder.App.Services.Annotations;
using Windows.Graphics;

namespace ScreenRecorder.App.Views;

public sealed partial class OverlayToolStripWindow : Window
{
    public event EventHandler<bool>? DrawModeChanged;
    public event EventHandler<bool>? BubbleChanged;
    public event EventHandler<AnnotationTool>? ToolChanged;
    public event EventHandler? UndoRequested;
    public event EventHandler? ClearRequested;
    public event EventHandler<RectInt32>? BoundsChanged;

    private AppWindow? _appWindow;
    private bool _pendingDrag;
    private Windows.Foundation.Point _dragStart;
    private bool _didPopIn;

    public OverlayToolStripWindow()
    {
        InitializeComponent();

        // Ensure title bar uses the app name.
        try
        {
            Title = "Prism Capture";
        }
        catch { }

        // Premium Win11 look (best-effort). If unsupported, it fails silently.
        TryApplyBackdrop();

        ToolCombo.SelectedIndex = 0;

        DrawModeToggle.Checked += OnDrawModeCheckedChanged;
        DrawModeToggle.Unchecked += OnDrawModeCheckedChanged;
        BubbleToggle.Checked += OnBubbleCheckedChanged;
        BubbleToggle.Unchecked += OnBubbleCheckedChanged;
        ToolCombo.SelectionChanged += OnToolSelectionChanged;
        UndoButton.Click += (_, __) => UndoRequested?.Invoke(this, EventArgs.Empty);
        ClearButton.Click += (_, __) => ClearRequested?.Invoke(this, EventArgs.Empty);

        // Allow moving the tool strip by click-dragging anywhere on it.
        // We only start moving once the pointer has moved a few pixels so normal clicks still work.
        RootBorder.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnRootPointerPressed), true);
        RootBorder.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnRootPointerMoved), true);
        RootBorder.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnRootPointerReleased), true);

        Activated += (_, __) => TryRunPopInAnimation();
    }

    private void TryApplyBackdrop()
    {
        try
        {
            // For a floating overlay, Acrylic generally reads more "premium overlay" than Mica.
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch
        {
            try
            {
                SystemBackdrop = new MicaBackdrop();
            }
            catch { }
        }
    }

    private void TryRunPopInAnimation()
    {
        try
        {
            if (_didPopIn)
            {
                return;
            }

            if (RootBorder is null)
            {
                return;
            }

            _didPopIn = true;

            var visual = ElementCompositionPreview.GetElementVisual(RootBorder);
            var compositor = visual.Compositor;

            // Set initial state (slightly smaller + transparent), then animate to normal.
            visual.CenterPoint = new System.Numerics.Vector3(0.5f * (float)RootBorder.ActualWidth, 0.5f * (float)RootBorder.ActualHeight, 0f);
            visual.Opacity = 0f;
            visual.Scale = new System.Numerics.Vector3(0.92f, 0.92f, 1f);

            var duration = TimeSpan.FromMilliseconds(320);
            var easing = compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.2f, 0.0f), new System.Numerics.Vector2(0.0f, 1.0f));

            var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.Duration = duration;
            opacity.InsertKeyFrame(0f, 0f);
            opacity.InsertKeyFrame(1f, 1f, easing);

            var scale = compositor.CreateVector3KeyFrameAnimation();
            scale.Duration = duration;
            scale.InsertKeyFrame(0f, visual.Scale);
            scale.InsertKeyFrame(1f, new System.Numerics.Vector3(1f, 1f, 1f), easing);

            visual.StartAnimation(nameof(visual.Opacity), opacity);
            visual.StartAnimation(nameof(visual.Scale), scale);
        }
        catch
        {
            // Best-effort.
        }
    }

    public nint Hwnd => WindowInterop.GetWindowHandle(this);

    public void ShowAtTopLeftOfPrimaryMonitor()
    {
        Breadcrumbs.Write("OverlayToolStripWindow: ShowAtTopLeftOfPrimaryMonitor()");
        // Exclude from capture so Screen capture won't record the tool strip.
        _ = WindowInterop.TryExcludeWindowFromCapture(this);

        _appWindow = GetAppWindowForCurrentWindow();

        try
        {
            _appWindow.Title = "Prism Capture";
        }
        catch { }

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var width = 640;
        var height = 108;

        if (!Win32BoundsInterop.TryGetPrimaryMonitorBounds(out var pm))
        {
            pm = new RectInt32(0, 0, 1920, 1080);
        }

        // Small inset so it's not exactly at (0,0).
        var x = pm.X + 16;
        var y = pm.Y + 16;
        Breadcrumbs.Write($"OverlayToolStripWindow: MoveAndResize x={x} y={y} w={width} h={height} (primary={pm.X},{pm.Y} {pm.Width}x{pm.Height})");
        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));

        // Listen for user-driven moves so the overlay can keep its passthrough region in sync.
        _appWindow.Changed -= OnAppWindowChanged;
        _appWindow.Changed += OnAppWindowChanged;

        RaiseBoundsChanged();

        Activate();
        BringToFront();
    }

    public void BringToFront()
    {
        try
        {
            var hwnd = WindowInterop.GetWindowHandle(this);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch { }
    }

    public bool TryGetBounds(out RectInt32 bounds)
    {
        return Win32BoundsInterop.TryGetWindowExtendedFrameBounds(Hwnd, out bounds);
    }

    public void SetDrawMode(bool enabled)
    {
        DrawModeToggle.IsChecked = enabled;
    }

    public void SetBubbleMode(bool enabled)
    {
        BubbleToggle.IsChecked = enabled;
    }

    public void SetTool(AnnotationTool tool)
    {
        ToolCombo.SelectedIndex = tool switch
        {
            AnnotationTool.Arrow => 1,
            AnnotationTool.Highlighter => 2,
            _ => 0
        };
    }

    private void OnDrawModeCheckedChanged(object sender, RoutedEventArgs e)
    {
        var on = DrawModeToggle.IsChecked == true;
        Breadcrumbs.Write($"OverlayToolStripWindow: DrawMode toggled {(on ? "ON" : "OFF")}");
        DrawModeChanged?.Invoke(this, on);
    }

    private void OnBubbleCheckedChanged(object sender, RoutedEventArgs e)
    {
        var on = BubbleToggle.IsChecked == true;
        Breadcrumbs.Write($"OverlayToolStripWindow: Bubble toggled {(on ? "ON" : "OFF")}");
        BubbleChanged?.Invoke(this, on);
    }

    private void OnToolSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tool = ToolCombo.SelectedIndex switch
        {
            1 => AnnotationTool.Arrow,
            2 => AnnotationTool.Highlighter,
            _ => AnnotationTool.Pen
        };

        ToolChanged?.Invoke(this, tool);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange)
        {
            RaiseBoundsChanged();
        }
    }

    private void RaiseBoundsChanged()
    {
        if (TryGetBounds(out var b))
        {
            BoundsChanged?.Invoke(this, b);
        }
    }

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(RootBorder);
        if (!pt.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pendingDrag = true;
        _dragStart = pt.Position;
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pendingDrag)
        {
            return;
        }

        var pt = e.GetCurrentPoint(RootBorder);
        if (!pt.Properties.IsLeftButtonPressed)
        {
            _pendingDrag = false;
            return;
        }

        var dx = pt.Position.X - _dragStart.X;
        var dy = pt.Position.Y - _dragStart.Y;
        if ((dx * dx + dy * dy) < 36) // 6px threshold
        {
            return;
        }

        _pendingDrag = false;

        // Start native window move.
        try
        {
            var hwnd = Hwnd;
            _ = ReleaseCapture();
            _ = SendMessageW(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
        }
        catch { }
    }

    private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _pendingDrag = false;
    }

    private AppWindow GetAppWindowForCurrentWindow()
    {
        var hwnd = WindowInterop.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    private static readonly nint HWND_TOPMOST = new(-1);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(nint hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;
}
