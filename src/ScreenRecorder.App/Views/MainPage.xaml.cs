using Microsoft.UI.Dispatching;
using ScreenRecorder.App.Helpers;
using Windows.Graphics;
using Microsoft.UI.Xaml.Input;
using System.Numerics;
using ScreenRecorder.App.Services.Annotations;
using ScreenRecorder.App.Services.OverlayInput;
using ScreenRecorder.App.Services.Persona;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;

namespace ScreenRecorder.App.Views
{
    public partial class MainPage : Page
    {
        private ScreenRecorder.App.Services.CapturePreview? _preview;
        private ScreenRecorder.App.Services.AvRecorder? _recorder;
        private ScreenRecorder.App.Services.Audio.WasapiMixedAudioSource? _audio;
        private ScreenRecorder.App.Services.Audio.AudioMonitor? _monitor;
        private DispatcherQueueTimer? _meterTimer;

        private AnnotationRenderer? _annotations;
        private bool _isDrawing;
        private uint? _drawingPointerId;

        private OverlayDrawHwndWindow? _overlayWindow;
        private OverlayToolStripWindow? _toolStripWindow;

        private WebcamFrameSource? _webcam;
        private bool _bubbleEnabled;

        private readonly List<List<UIElement>> _drawUiPrimitives = new();
        private Polyline? _activePolyline;
        private Line? _activeArrowLine;
        private Line? _activeArrowHead1;
        private Line? _activeArrowHead2;

        private bool _isDraggingRegion;
        private Windows.Foundation.Point _regionStart;

        private enum RegionDragMode
        {
            None,
            NewSelection,
            Move,
            Resize
        }

        [Flags]
        private enum ResizeEdges
        {
            None = 0,
            Left = 1,
            Top = 2,
            Right = 4,
            Bottom = 8
        }

        private RegionDragMode _regionDragMode = RegionDragMode.None;
        private ResizeEdges _resizeEdges = ResizeEdges.None;

        private Windows.Foundation.Point _dragStartPoint;
        private double _dragStartLeft;
        private double _dragStartTop;
        private double _dragStartWidth;
        private double _dragStartHeight;

        public MainPage()
        {
            this.InitializeComponent();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Breadcrumbs.Write("MainPage: Loaded");

            // Premium Fluent motion: staggered entrance of primary surfaces.
            // Best-effort; failures should never block the app.
            TryRunEntranceAnimations();

            if (DataContext is ScreenRecorder.App.ViewModels.MainViewModel vm)
            {
                Breadcrumbs.Write("MainPage: DataContext is MainViewModel");
                _preview ??= new ScreenRecorder.App.Services.CapturePreview(PreviewPanel);
                _audio ??= new ScreenRecorder.App.Services.Audio.WasapiMixedAudioSource();
                _monitor ??= new ScreenRecorder.App.Services.Audio.AudioMonitor(_audio);

                // Recorder uses the same audio source so monitoring/meters match recording.
                _recorder ??= new ScreenRecorder.App.Services.AvRecorder(_audio, manageAudio: false);
                _recorder.Status -= OnRecorderStatus;
                _recorder.Status += OnRecorderStatus;

                _annotations ??= new AnnotationRenderer();
                _recorder.SetAnnotationOverlaySource(_annotations);

                vm.CaptureItemChanged -= OnCaptureItemChanged;
                vm.CaptureItemChanged += OnCaptureItemChanged;

                vm.RecordingToggled -= OnRecordingToggled;
                vm.RecordingToggled += OnRecordingToggled;

                vm.PropertyChanged -= OnViewModelPropertyChanged;
                vm.PropertyChanged += OnViewModelPropertyChanged;

                ApplyAudioSettingsFromViewModel(vm);
                EnsureMeterTimer(vm);

                // Region selection overlay input
                RegionOverlay.PointerPressed += OnRegionPointerPressed;
                RegionOverlay.PointerMoved += OnRegionPointerMoved;
                RegionOverlay.PointerReleased += OnRegionPointerReleased;

                DrawOverlay.PointerPressed += OnDrawPointerPressed;
                DrawOverlay.PointerMoved += OnDrawPointerMoved;
                DrawOverlay.PointerReleased += OnDrawPointerReleased;
                DrawOverlay.PointerCanceled += OnDrawPointerCanceled;

                DrawToolCombo.SelectionChanged += OnDrawToolSelectionChanged;
                UndoDrawButton.Click += OnUndoDraw;
                ClearDrawButton.Click += OnClearDraw;

                DrawToolCombo.SelectedIndex = 0;

                HandleNW.PointerPressed += OnHandlePointerPressed;
                HandleN.PointerPressed += OnHandlePointerPressed;
                HandleNE.PointerPressed += OnHandlePointerPressed;
                HandleE.PointerPressed += OnHandlePointerPressed;
                HandleSE.PointerPressed += OnHandlePointerPressed;
                HandleS.PointerPressed += OnHandlePointerPressed;
                HandleSW.PointerPressed += OnHandlePointerPressed;
                HandleW.PointerPressed += OnHandlePointerPressed;

                HandleNW.PointerReleased += OnHandlePointerReleased;
                HandleN.PointerReleased += OnHandlePointerReleased;
                HandleNE.PointerReleased += OnHandlePointerReleased;
                HandleE.PointerReleased += OnHandlePointerReleased;
                HandleSE.PointerReleased += OnHandlePointerReleased;
                HandleS.PointerReleased += OnHandlePointerReleased;
                HandleSW.PointerReleased += OnHandlePointerReleased;
                HandleW.PointerReleased += OnHandlePointerReleased;

                // Start capture so the meters are live in Studio View.
                // Monitoring remains off unless explicitly enabled.
                TryStartAudioForMeters(vm);

                await vm.InitializeAsync();
                Breadcrumbs.Write("MainPage: InitializeAsync complete");
            }

            Unloaded -= OnUnloaded;
            Unloaded += OnUnloaded;
        }

        private void TryRunEntranceAnimations()
        {
            try
            {
                // These elements are defined in MainPage.xaml.
                // If names are missing (e.g. XAML edit conflict), just no-op.
                var surfaces = new List<(UIElement? element, Vector3 fromOffset, int delayMs)>
                {
                    (LeftCard,  new Vector3(-24, 0, 0), 0),
                    (PreviewCard, new Vector3(0, 18, 0), 60),
                    (ActionBarCard, new Vector3(0, 18, 0), 120),
                    (RightCard, new Vector3(24, 0, 0), 180)
                };

                foreach (var (element, fromOffset, delayMs) in surfaces)
                {
                    if (element is null)
                    {
                        continue;
                    }

                    RunEntranceAnimation(element, fromOffset, delayMs);
                }
            }
            catch
            {
                // Never fail page load due to animation.
            }
        }

        private static void RunEntranceAnimation(UIElement element, Vector3 fromOffset, int delayMs)
        {
            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(element);
                var compositor = visual.Compositor;

                // Start slightly offset and transparent.
                visual.Opacity = 0f;
                visual.Offset = visual.Offset + fromOffset;

                var duration = TimeSpan.FromMilliseconds(420);
                var delay = TimeSpan.FromMilliseconds(Math.Max(0, delayMs));

                var opacity = compositor.CreateScalarKeyFrameAnimation();
                opacity.Duration = duration;
                opacity.DelayTime = delay;
                opacity.InsertKeyFrame(0f, 0f);
                opacity.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.0f), new Vector2(0.0f, 1.0f)));

                var offset = compositor.CreateVector3KeyFrameAnimation();
                offset.Duration = duration;
                offset.DelayTime = delay;
                offset.InsertKeyFrame(0f, visual.Offset);
                offset.InsertKeyFrame(1f, visual.Offset - fromOffset, compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.0f), new Vector2(0.0f, 1.0f)));

                visual.StartAnimation(nameof(visual.Opacity), opacity);
                visual.StartAnimation(nameof(visual.Offset), offset);
            }
            catch
            {
                // Best-effort.
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is ScreenRecorder.App.ViewModels.MainViewModel vm)
            {
                vm.CaptureItemChanged -= OnCaptureItemChanged;
                vm.RecordingToggled -= OnRecordingToggled;
                vm.PropertyChanged -= OnViewModelPropertyChanged;
            }

            try { _meterTimer?.Stop(); } catch { }
            _meterTimer = null;

            try { _monitor?.Dispose(); } catch { }
            _monitor = null;

            try { _audio?.Dispose(); } catch { }
            _audio = null;

            _preview?.Dispose();
            _preview = null;

            _recorder?.Dispose();
            _recorder = null;

            try { _webcam?.Dispose(); } catch { }
            _webcam = null;
            _bubbleEnabled = false;

            try { _toolStripWindow?.Close(); } catch { }
            _toolStripWindow = null;

            try { _overlayWindow?.Close(); } catch { }
            _overlayWindow = null;

            try { _annotations?.Dispose(); } catch { }
            _annotations = null;

            try
            {
                RegionOverlay.PointerPressed -= OnRegionPointerPressed;
                RegionOverlay.PointerMoved -= OnRegionPointerMoved;
                RegionOverlay.PointerReleased -= OnRegionPointerReleased;

                HandleNW.PointerPressed -= OnHandlePointerPressed;
                HandleN.PointerPressed -= OnHandlePointerPressed;
                HandleNE.PointerPressed -= OnHandlePointerPressed;
                HandleE.PointerPressed -= OnHandlePointerPressed;
                HandleSE.PointerPressed -= OnHandlePointerPressed;
                HandleS.PointerPressed -= OnHandlePointerPressed;
                HandleSW.PointerPressed -= OnHandlePointerPressed;
                HandleW.PointerPressed -= OnHandlePointerPressed;

                HandleNW.PointerReleased -= OnHandlePointerReleased;
                HandleN.PointerReleased -= OnHandlePointerReleased;
                HandleNE.PointerReleased -= OnHandlePointerReleased;
                HandleE.PointerReleased -= OnHandlePointerReleased;
                HandleSE.PointerReleased -= OnHandlePointerReleased;
                HandleS.PointerReleased -= OnHandlePointerReleased;
                HandleSW.PointerReleased -= OnHandlePointerReleased;
                HandleW.PointerReleased -= OnHandlePointerReleased;

                DrawOverlay.PointerPressed -= OnDrawPointerPressed;
                DrawOverlay.PointerMoved -= OnDrawPointerMoved;
                DrawOverlay.PointerReleased -= OnDrawPointerReleased;
                DrawOverlay.PointerCanceled -= OnDrawPointerCanceled;

                DrawToolCombo.SelectionChanged -= OnDrawToolSelectionChanged;
                UndoDrawButton.Click -= OnUndoDraw;
                ClearDrawButton.Click -= OnClearDraw;
            }
            catch { }
        }

        private void OnDrawToolSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_annotations is null)
            {
                return;
            }

            var tool = DrawToolCombo.SelectedIndex switch
            {
                1 => AnnotationTool.Arrow,
                2 => AnnotationTool.Highlighter,
                _ => AnnotationTool.Pen
            };

            _annotations.SetTool(tool);

            // Keep overlay window in sync if active.
            _overlayWindow?.SetTool(tool);
        }

        private void OnUndoDraw(object sender, RoutedEventArgs e)
        {
            if (_overlayWindow is not null)
            {
                _overlayWindow.Undo();
                return;
            }

            _annotations?.Undo();

            if (_drawUiPrimitives.Count > 0)
            {
                var last = _drawUiPrimitives[^1];
                _drawUiPrimitives.RemoveAt(_drawUiPrimitives.Count - 1);
                foreach (var el in last)
                {
                    DrawOverlay.Children.Remove(el);
                }
            }
        }

        private void OnClearDraw(object sender, RoutedEventArgs e)
        {
            if (_overlayWindow is not null)
            {
                _overlayWindow.Clear();
                return;
            }

            _annotations?.Clear();

            _drawUiPrimitives.Clear();
            DrawOverlay.Children.Clear();
            _activePolyline = null;
            _activeArrowLine = null;
            _activeArrowHead1 = null;
            _activeArrowHead2 = null;
        }

        private void OnDrawPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (DataContext is not ScreenRecorder.App.ViewModels.MainViewModel vm || !vm.IsRecording)
            {
                return;
            }

            if (_annotations is null)
            {
                return;
            }

            var overlayPos = e.GetCurrentPoint(DrawOverlay).Position;
            if (!TryMapPointToOutput(vm, overlayPos, out var p))
            {
                return;
            }

            BeginUiPrimitive(overlayPos);

            _drawingPointerId = e.Pointer.PointerId;
            _isDrawing = true;
            DrawOverlay.CapturePointer(e.Pointer);
            _annotations.PointerDown(p);
            e.Handled = true;
        }

        private void OnDrawPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDrawing || _drawingPointerId is null || e.Pointer.PointerId != _drawingPointerId.Value)
            {
                return;
            }

            if (DataContext is not ScreenRecorder.App.ViewModels.MainViewModel vm || !vm.IsRecording)
            {
                return;
            }

            if (_annotations is null)
            {
                return;
            }

            var overlayPos = e.GetCurrentPoint(DrawOverlay).Position;
            if (!TryMapPointToOutput(vm, overlayPos, out var p))
            {
                return;
            }

            UpdateUiPrimitive(overlayPos);

            _annotations.PointerMove(p);
            e.Handled = true;
        }

        private void OnDrawPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDrawing || _drawingPointerId is null || e.Pointer.PointerId != _drawingPointerId.Value)
            {
                return;
            }

            if (DataContext is not ScreenRecorder.App.ViewModels.MainViewModel vm || !vm.IsRecording)
            {
                _isDrawing = false;
                _drawingPointerId = null;
                return;
            }

            if (_annotations is null)
            {
                _isDrawing = false;
                _drawingPointerId = null;
                return;
            }

            var overlayPos = e.GetCurrentPoint(DrawOverlay).Position;
            if (TryMapPointToOutput(vm, overlayPos, out var p))
            {
                EndUiPrimitive(overlayPos);
                _annotations.PointerUp(p);
            }
            else
            {
                CancelActiveUiPrimitive();
            }

            _isDrawing = false;
            _drawingPointerId = null;
            DrawOverlay.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }

        private void OnDrawPointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (_drawingPointerId is null || e.Pointer.PointerId != _drawingPointerId.Value)
            {
                return;
            }

            _isDrawing = false;
            _drawingPointerId = null;
            CancelActiveUiPrimitive();
            DrawOverlay.ReleasePointerCapture(e.Pointer);
        }

        private void HideSelectionHandles()
        {
            HandleNW.Visibility = Visibility.Collapsed;
            HandleN.Visibility = Visibility.Collapsed;
            HandleNE.Visibility = Visibility.Collapsed;
            HandleE.Visibility = Visibility.Collapsed;
            HandleSE.Visibility = Visibility.Collapsed;
            HandleS.Visibility = Visibility.Collapsed;
            HandleSW.Visibility = Visibility.Collapsed;
            HandleW.Visibility = Visibility.Collapsed;
        }

        private void ShowSelectionHandles()
        {
            HandleNW.Visibility = Visibility.Visible;
            HandleN.Visibility = Visibility.Visible;
            HandleNE.Visibility = Visibility.Visible;
            HandleE.Visibility = Visibility.Visible;
            HandleSE.Visibility = Visibility.Visible;
            HandleS.Visibility = Visibility.Visible;
            HandleSW.Visibility = Visibility.Visible;
            HandleW.Visibility = Visibility.Visible;
        }

        private void UpdateHandlesFromSelectionRect()
        {
            if (RegionSelectionRect.Visibility != Visibility.Visible)
            {
                HideSelectionHandles();
                return;
            }

            var left = Canvas.GetLeft(RegionSelectionRect);
            var top = Canvas.GetTop(RegionSelectionRect);
            var w = RegionSelectionRect.Width;
            var h = RegionSelectionRect.Height;

            const double hs = 10; // handle size
            var cx = left + (w / 2.0);
            var cy = top + (h / 2.0);

            Canvas.SetLeft(HandleNW, left - (hs / 2.0));
            Canvas.SetTop(HandleNW, top - (hs / 2.0));

            Canvas.SetLeft(HandleN, cx - (hs / 2.0));
            Canvas.SetTop(HandleN, top - (hs / 2.0));

            Canvas.SetLeft(HandleNE, (left + w) - (hs / 2.0));
            Canvas.SetTop(HandleNE, top - (hs / 2.0));

            Canvas.SetLeft(HandleE, (left + w) - (hs / 2.0));
            Canvas.SetTop(HandleE, cy - (hs / 2.0));

            Canvas.SetLeft(HandleSE, (left + w) - (hs / 2.0));
            Canvas.SetTop(HandleSE, (top + h) - (hs / 2.0));

            Canvas.SetLeft(HandleS, cx - (hs / 2.0));
            Canvas.SetTop(HandleS, (top + h) - (hs / 2.0));

            Canvas.SetLeft(HandleSW, left - (hs / 2.0));
            Canvas.SetTop(HandleSW, (top + h) - (hs / 2.0));

            Canvas.SetLeft(HandleW, left - (hs / 2.0));
            Canvas.SetTop(HandleW, cy - (hs / 2.0));

            ShowSelectionHandles();
        }

        private static bool PointInRect(double x, double y, double left, double top, double width, double height)
        {
            return x >= left && x <= left + width && y >= top && y <= top + height;
        }

        private bool TryComputePreviewRect(ScreenRecorder.App.ViewModels.MainViewModel vm,
            out double previewX,
            out double previewY,
            out double previewW,
            out double previewH,
            out int itemW,
            out int itemH)
        {
            previewX = previewY = previewW = previewH = 0;
            itemW = itemH = 0;

            var item = vm.SelectedCaptureItem;
            if (item is null)
            {
                return false;
            }

            var overlayW = RegionOverlay.ActualWidth;
            var overlayH = RegionOverlay.ActualHeight;
            if (overlayW <= 0 || overlayH <= 0)
            {
                return false;
            }

            itemW = Math.Max(1, item.Size.Width);
            itemH = Math.Max(1, item.Size.Height);

            var captureAspect = itemW / (double)itemH;
            var overlayAspect = overlayW / overlayH;

            if (overlayAspect > captureAspect)
            {
                // Pillarbox
                previewH = overlayH;
                previewW = overlayH * captureAspect;
                previewX = (overlayW - previewW) / 2.0;
                previewY = 0;
            }
            else
            {
                // Letterbox
                previewW = overlayW;
                previewH = overlayW / captureAspect;
                previewX = 0;
                previewY = (overlayH - previewH) / 2.0;
            }

            return previewW > 0 && previewH > 0;
        }

        private bool TryOverlayRectToCaptureRect(ScreenRecorder.App.ViewModels.MainViewModel vm,
            double left,
            double top,
            double width,
            double height,
            out RectInt32 captureRect)
        {
            captureRect = default;

            if (!TryComputePreviewRect(vm, out var previewX, out var previewY, out var previewW, out var previewH, out var itemW, out var itemH))
            {
                return false;
            }

            var x1 = left;
            var y1 = top;
            var x2 = left + width;
            var y2 = top + height;

            x1 = Math.Clamp(x1, previewX, previewX + previewW);
            x2 = Math.Clamp(x2, previewX, previewX + previewW);
            y1 = Math.Clamp(y1, previewY, previewY + previewH);
            y2 = Math.Clamp(y2, previewY, previewY + previewH);

            if (x2 - x1 < 4 || y2 - y1 < 4)
            {
                return false;
            }

            var nx1 = (x1 - previewX) / previewW;
            var ny1 = (y1 - previewY) / previewH;
            var nx2 = (x2 - previewX) / previewW;
            var ny2 = (y2 - previewY) / previewH;

            var px = (int)Math.Round(nx1 * itemW);
            var py = (int)Math.Round(ny1 * itemH);
            var px2 = (int)Math.Round(nx2 * itemW);
            var py2 = (int)Math.Round(ny2 * itemH);

            var pw = Math.Max(0, px2 - px);
            var ph = Math.Max(0, py2 - py);

            px = Math.Clamp(px, 0, itemW - 2);
            py = Math.Clamp(py, 0, itemH - 2);
            pw = Math.Clamp(pw, 2, itemW - px);
            ph = Math.Clamp(ph, 2, itemH - py);

            // Even values for yuv420p friendliness.
            px -= px % 2;
            py -= py % 2;
            pw -= pw % 2;
            ph -= ph % 2;

            if (pw < 2 || ph < 2)
            {
                return false;
            }

            if (px + pw > itemW) pw = Math.Max(2, (itemW - px) & ~1);
            if (py + ph > itemH) ph = Math.Max(2, (itemH - py) & ~1);

            captureRect = new RectInt32(px, py, pw, ph);
            return true;
        }

        private void UpdateSelectionVisualFromCaptureRect(ScreenRecorder.App.ViewModels.MainViewModel vm, RectInt32 captureRect)
        {
            if (!TryComputePreviewRect(vm, out var previewX, out var previewY, out var previewW, out var previewH, out var itemW, out var itemH))
            {
                return;
            }

            var nx1 = captureRect.X / (double)itemW;
            var ny1 = captureRect.Y / (double)itemH;
            var nx2 = (captureRect.X + captureRect.Width) / (double)itemW;
            var ny2 = (captureRect.Y + captureRect.Height) / (double)itemH;

            var left = previewX + (nx1 * previewW);
            var top = previewY + (ny1 * previewH);
            var right = previewX + (nx2 * previewW);
            var bottom = previewY + (ny2 * previewH);

            Canvas.SetLeft(RegionSelectionRect, left);
            Canvas.SetTop(RegionSelectionRect, top);
            RegionSelectionRect.Width = Math.Max(0, right - left);
            RegionSelectionRect.Height = Math.Max(0, bottom - top);
            RegionSelectionRect.Visibility = Visibility.Visible;

            UpdateHandlesFromSelectionRect();
        }

        private void ApplyCurrentOverlayRectToViewModel(ScreenRecorder.App.ViewModels.MainViewModel vm)
        {
            var left = Canvas.GetLeft(RegionSelectionRect);
            var top = Canvas.GetTop(RegionSelectionRect);
            var w = RegionSelectionRect.Width;
            var h = RegionSelectionRect.Height;

            if (TryOverlayRectToCaptureRect(vm, left, top, w, h, out var r))
            {
                vm.SelectedRegion = r;
                vm.StatusMessage = $"Region: {r.Width}x{r.Height} @ ({r.X},{r.Y})";
            }
            else
            {
                vm.SelectedRegion = null;
                vm.StatusMessage = "Region too small.";
            }
        }

        private void OnHandlePointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (DataContext is not ScreenRecorder.App.ViewModels.MainViewModel vm)
            {
                return;
            }

            if (vm.RegionOverlayVisibility != Visibility.Visible)
            {
                return;
            }

            if (RegionSelectionRect.Visibility != Visibility.Visible)
            {
                return;
            }

            _regionDragMode = RegionDragMode.Resize;
            _isDraggingRegion = true;
            _dragStartPoint = e.GetCurrentPoint(RegionOverlay).Position;

            _dragStartLeft = Canvas.GetLeft(RegionSelectionRect);
            _dragStartTop = Canvas.GetTop(RegionSelectionRect);
            _dragStartWidth = RegionSelectionRect.Width;
            _dragStartHeight = RegionSelectionRect.Height;

            _resizeEdges = sender switch
            {
                var s when ReferenceEquals(s, HandleNW) => ResizeEdges.Left | ResizeEdges.Top,
                var s when ReferenceEquals(s, HandleN) => ResizeEdges.Top,
                var s when ReferenceEquals(s, HandleNE) => ResizeEdges.Right | ResizeEdges.Top,
                var s when ReferenceEquals(s, HandleE) => ResizeEdges.Right,
                var s when ReferenceEquals(s, HandleSE) => ResizeEdges.Right | ResizeEdges.Bottom,
                var s when ReferenceEquals(s, HandleS) => ResizeEdges.Bottom,
                var s when ReferenceEquals(s, HandleSW) => ResizeEdges.Left | ResizeEdges.Bottom,
                var s when ReferenceEquals(s, HandleW) => ResizeEdges.Left,
                _ => ResizeEdges.None
            };

            RegionOverlay.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnHandlePointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDraggingRegion)
            {
                return;
            }

            if (_regionDragMode != RegionDragMode.Resize)
            {
                return;
            }

            _isDraggingRegion = false;
            _regionDragMode = RegionDragMode.None;
            _resizeEdges = ResizeEdges.None;
            RegionOverlay.ReleasePointerCapture(e.Pointer);

            if (DataContext is ScreenRecorder.App.ViewModels.MainViewModel vm)
            {
                ApplyCurrentOverlayRectToViewModel(vm);
            }

            e.Handled = true;
        }

        private void OnRegionPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (DataContext is not ScreenRecorder.App.ViewModels.MainViewModel vm)
            {
                return;
            }

            if (vm.RegionOverlayVisibility != Visibility.Visible)
            {
                return;
            }

            var pt = e.GetCurrentPoint(RegionOverlay).Position;

            // If a selection exists and the user clicks inside it, move it.
            if (RegionSelectionRect.Visibility == Visibility.Visible)
            {
                var left = Canvas.GetLeft(RegionSelectionRect);
                var top = Canvas.GetTop(RegionSelectionRect);
                var w = RegionSelectionRect.Width;
                var h = RegionSelectionRect.Height;

                if (PointInRect(pt.X, pt.Y, left, top, w, h))
                {
                    _regionDragMode = RegionDragMode.Move;
                    _isDraggingRegion = true;
                    _dragStartPoint = pt;
                    _dragStartLeft = left;
                    _dragStartTop = top;
                    _dragStartWidth = w;
                    _dragStartHeight = h;
                    RegionOverlay.CapturePointer(e.Pointer);
                    e.Handled = true;
                    return;
                }
            }

            // Otherwise, start a new selection rectangle.
            _regionDragMode = RegionDragMode.NewSelection;
            _isDraggingRegion = true;
            _regionStart = pt;

            HideSelectionHandles();
            Canvas.SetLeft(RegionSelectionRect, _regionStart.X);
            Canvas.SetTop(RegionSelectionRect, _regionStart.Y);
            RegionSelectionRect.Width = 0;
            RegionSelectionRect.Height = 0;
            RegionSelectionRect.Visibility = Visibility.Visible;

            RegionOverlay.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnRegionPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDraggingRegion)
            {
                return;
            }

            if (DataContext is not ScreenRecorder.App.ViewModels.MainViewModel vm)
            {
                return;
            }

            if (!TryComputePreviewRect(vm, out var previewX, out var previewY, out var previewW, out var previewH, out _, out _))
            {
                return;
            }

            var pos = e.GetCurrentPoint(RegionOverlay).Position;
            pos = new Windows.Foundation.Point(
                Math.Clamp(pos.X, previewX, previewX + previewW),
                Math.Clamp(pos.Y, previewY, previewY + previewH));

            const double minPx = 6;

            if (_regionDragMode == RegionDragMode.NewSelection)
            {
                var x1 = Math.Min(_regionStart.X, pos.X);
                var y1 = Math.Min(_regionStart.Y, pos.Y);
                var x2 = Math.Max(_regionStart.X, pos.X);
                var y2 = Math.Max(_regionStart.Y, pos.Y);

                Canvas.SetLeft(RegionSelectionRect, x1);
                Canvas.SetTop(RegionSelectionRect, y1);
                RegionSelectionRect.Width = Math.Max(0, x2 - x1);
                RegionSelectionRect.Height = Math.Max(0, y2 - y1);

                UpdateHandlesFromSelectionRect();
                e.Handled = true;
                return;
            }

            if (_regionDragMode == RegionDragMode.Move)
            {
                var dx = pos.X - _dragStartPoint.X;
                var dy = pos.Y - _dragStartPoint.Y;

                var newLeft = _dragStartLeft + dx;
                var newTop = _dragStartTop + dy;

                newLeft = Math.Clamp(newLeft, previewX, (previewX + previewW) - _dragStartWidth);
                newTop = Math.Clamp(newTop, previewY, (previewY + previewH) - _dragStartHeight);

                Canvas.SetLeft(RegionSelectionRect, newLeft);
                Canvas.SetTop(RegionSelectionRect, newTop);
                RegionSelectionRect.Width = _dragStartWidth;
                RegionSelectionRect.Height = _dragStartHeight;

                UpdateHandlesFromSelectionRect();
                ApplyCurrentOverlayRectToViewModel(vm);
                e.Handled = true;
                return;
            }

            if (_regionDragMode == RegionDragMode.Resize)
            {
                var dx = pos.X - _dragStartPoint.X;
                var dy = pos.Y - _dragStartPoint.Y;

                var left = _dragStartLeft;
                var top = _dragStartTop;
                var right = _dragStartLeft + _dragStartWidth;
                var bottom = _dragStartTop + _dragStartHeight;

                if (_resizeEdges.HasFlag(ResizeEdges.Left))
                {
                    left = Math.Min(right - minPx, _dragStartLeft + dx);
                }
                if (_resizeEdges.HasFlag(ResizeEdges.Right))
                {
                    right = Math.Max(left + minPx, (_dragStartLeft + _dragStartWidth) + dx);
                }
                if (_resizeEdges.HasFlag(ResizeEdges.Top))
                {
                    top = Math.Min(bottom - minPx, _dragStartTop + dy);
                }
                if (_resizeEdges.HasFlag(ResizeEdges.Bottom))
                {
                    bottom = Math.Max(top + minPx, (_dragStartTop + _dragStartHeight) + dy);
                }

                left = Math.Clamp(left, previewX, previewX + previewW - minPx);
                top = Math.Clamp(top, previewY, previewY + previewH - minPx);
                right = Math.Clamp(right, previewX + minPx, previewX + previewW);
                bottom = Math.Clamp(bottom, previewY + minPx, previewY + previewH);

                Canvas.SetLeft(RegionSelectionRect, left);
                Canvas.SetTop(RegionSelectionRect, top);
                RegionSelectionRect.Width = Math.Max(minPx, right - left);
                RegionSelectionRect.Height = Math.Max(minPx, bottom - top);

                UpdateHandlesFromSelectionRect();
                ApplyCurrentOverlayRectToViewModel(vm);
                e.Handled = true;
                return;
            }
        }

        private void OnRegionPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDraggingRegion)
            {
                return;
            }

            var finishedMode = _regionDragMode;
            _isDraggingRegion = false;
            _regionDragMode = RegionDragMode.None;
            _resizeEdges = ResizeEdges.None;
            RegionOverlay.ReleasePointerCapture(e.Pointer);

            if (DataContext is not ScreenRecorder.App.ViewModels.MainViewModel vm)
            {
                return;
            }

            var left = Canvas.GetLeft(RegionSelectionRect);
            var top = Canvas.GetTop(RegionSelectionRect);
            var w = RegionSelectionRect.Width;
            var h = RegionSelectionRect.Height;

            if (w < 4 || h < 4)
            {
                vm.StatusMessage = "Region too small.";
                RegionSelectionRect.Visibility = Visibility.Collapsed;
                HideSelectionHandles();
                return;
            }

            if (TryOverlayRectToCaptureRect(vm, left, top, w, h, out var r))
            {
                vm.SelectedRegion = r;
                vm.StatusMessage = finishedMode == RegionDragMode.NewSelection
                    ? $"Region selected: {r.Width}x{r.Height} @ ({r.X},{r.Y})"
                    : $"Region: {r.Width}x{r.Height} @ ({r.X},{r.Y})";

                // Keep overlay visible so the user can adjust by dragging/using handles.
                vm.RegionOverlayVisibility = Visibility.Visible;
                UpdateSelectionVisualFromCaptureRect(vm, r);
            }
            else
            {
                vm.SelectedRegion = null;
                vm.StatusMessage = "Region too small.";
                RegionSelectionRect.Visibility = Visibility.Collapsed;
                HideSelectionHandles();
            }

            e.Handled = true;
        }

        private void OnCaptureItemChanged(object? sender, Windows.Graphics.Capture.GraphicsCaptureItem? item)
        {
            if (item is null)
            {
                _preview?.Stop();
                return;
            }

            try
            {
                Breadcrumbs.Write("MainPage: CaptureItemChanged -> start preview");
                _preview?.Start(item);
                Breadcrumbs.Write("MainPage: preview started");
            }
            catch (Exception ex)
            {
                Breadcrumbs.Write("MainPage: preview start failed BEGIN");
                Breadcrumbs.Write(ex.ToString());
                Breadcrumbs.Write("MainPage: preview start failed END");

                if (DataContext is ScreenRecorder.App.ViewModels.MainViewModel vm)
                {
                    vm.StatusMessage = ex.InnerException?.Message ?? ex.Message;
                }
            }
        }

        private void OnRecordingToggled(object? sender, bool isRecording)
        {
            if (DataContext is not ScreenRecorder.App.ViewModels.MainViewModel vm)
            {
                return;
            }

            if (isRecording)
            {
                try
                {
                    Breadcrumbs.Write("MainPage: Record ON");

                    Breadcrumbs.Write("MainPage: stopping preview");
                    _preview?.Stop();
                    Breadcrumbs.Write("MainPage: preview stopped");

                    if (_audio is not null && !_audio.IsRunning)
                    {
                        Breadcrumbs.Write("MainPage: starting audio");
                        _audio.StartDefault();
                        Breadcrumbs.Write("MainPage: audio started");
                    }
                    else
                    {
                        Breadcrumbs.Write($"MainPage: audio already running={_audio?.IsRunning == true}");
                    }

                    var item = vm.SelectedCaptureItem;
                    if (item is null)
                    {
                        Breadcrumbs.Write("MainPage: record aborted - no SelectedCaptureItem");
                        vm.StatusMessage = "No capture item selected.";
                        vm.IsRecording = false;
                        return;
                    }

                    if (!string.Equals(vm.SelectedCaptureTab, vm.SelectedTab, StringComparison.OrdinalIgnoreCase))
                    {
                        Breadcrumbs.Write($"MainPage: record aborted - selection tab mismatch selected={vm.SelectedCaptureTab} current={vm.SelectedTab}");
                        vm.StatusMessage = "Choose a source for the current tab.";
                        vm.IsRecording = false;
                        _preview?.Start(item);
                        return;
                    }

                    if (string.Equals(vm.SelectedTab, "Region", StringComparison.OrdinalIgnoreCase)
                        && vm.SelectedRegion is null)
                    {
                        Breadcrumbs.Write("MainPage: record aborted - Region tab but no SelectedRegion");
                        vm.StatusMessage = "Choose a region first (drag on preview).";
                        vm.IsRecording = false;
                        _preview?.Start(item);
                        return;
                    }

                    if (!ScreenRecorder.App.Services.Ffmpeg.FfmpegAvMuxer.IsFfmpegAvailable(out _, out var ffmpegMsg))
                    {
                        Breadcrumbs.Write($"MainPage: record aborted - ffmpeg not available ({ffmpegMsg})");
                        vm.StatusMessage = ffmpegMsg ?? "ffmpeg not available.";
                        vm.IsRecording = false;
                        if (vm.SelectedCaptureItem is not null)
                        {
                            _preview?.Start(vm.SelectedCaptureItem);
                        }
                        return;
                    }
                    Breadcrumbs.Write("MainPage: ffmpeg available");

                    var root = System.IO.Path.GetPathRoot(vm.OutputFolder);
                    if (!string.IsNullOrWhiteSpace(root))
                    {
                        var drive = new System.IO.DriveInfo(root);
                        if (drive.AvailableFreeSpace < 500L * 1024L * 1024L)
                        {
                            Breadcrumbs.Write($"MainPage: record aborted - low disk space free={drive.AvailableFreeSpace}");
                            vm.StatusMessage = "Not enough disk space (<500MB).";
                            vm.IsRecording = false;
                            return;
                        }
                    }

                    ApplyAudioSettingsFromViewModel(vm);

                    if (_annotations is not null)
                    {
                        var outW = vm.SelectedRegion?.Width ?? vm.SelectedCaptureItem?.Size.Width ?? 1;
                        var outH = vm.SelectedRegion?.Height ?? vm.SelectedCaptureItem?.Size.Height ?? 1;
                        _annotations.EnsureSize(outW, outH);
                        _annotations.Clear();
                    }

                    _drawUiPrimitives.Clear();
                    DrawOverlay.Children.Clear();
                    _activePolyline = null;
                    _activeArrowLine = null;
                    _activeArrowHead1 = null;
                    _activeArrowHead2 = null;

                    // Prefer drawing input over region selection while recording.
                    RegionOverlay.IsHitTestVisible = false;
                    DrawOverlay.IsHitTestVisible = false;

                    Breadcrumbs.Write($"MainPage: Starting recorder (hw={vm.UseHardwareEncoding})");
                    _recorder?.Start(item, vm.OutputFolder, fps: 30, useHardwareEncoding: vm.UseHardwareEncoding, crop: vm.SelectedRegion);

                    vm.StatusMessage = "Recording...";

                    // Tool strip should be independent of the fullscreen overlay window.
                    try
                    {
                        _toolStripWindow ??= new OverlayToolStripWindow();
                        _toolStripWindow.DrawModeChanged -= OnToolStripDrawModeChanged;
                        _toolStripWindow.BubbleChanged -= OnToolStripBubbleChanged;
                        _toolStripWindow.ToolChanged -= OnToolStripToolChanged;
                        _toolStripWindow.UndoRequested -= OnToolStripUndo;
                        _toolStripWindow.ClearRequested -= OnToolStripClear;
                        _toolStripWindow.BoundsChanged -= OnToolStripBoundsChanged;

                        _toolStripWindow.DrawModeChanged += OnToolStripDrawModeChanged;
                        _toolStripWindow.BubbleChanged += OnToolStripBubbleChanged;
                        _toolStripWindow.ToolChanged += OnToolStripToolChanged;
                        _toolStripWindow.UndoRequested += OnToolStripUndo;
                        _toolStripWindow.ClearRequested += OnToolStripClear;
                        _toolStripWindow.BoundsChanged += OnToolStripBoundsChanged;

                        _toolStripWindow.SetTool(AnnotationTool.Pen);
                        _toolStripWindow.SetDrawMode(false);
                        _toolStripWindow.SetBubbleMode(_bubbleEnabled);
                        _toolStripWindow.ShowAtTopLeftOfPrimaryMonitor();
                        Breadcrumbs.Write("MainPage: tool strip shown");
                    }
                    catch (Exception toolEx)
                    {
                        Breadcrumbs.Write("MainPage: tool strip failed to show");
                        Breadcrumbs.Write(toolEx);
                    }

                    // Ensure bubble state is applied at recording start.
                    _ = ToggleBubbleAsync(_bubbleEnabled);

                    // Fullscreen click-through overlay window for drawing (best-effort).
                    try
                    {
                        if (_overlayWindow is null && _annotations is not null)
                        {
                            _overlayWindow = new OverlayDrawHwndWindow(_annotations);
                        }

                        _overlayWindow?.ShowForCapture(item, vm.SelectedTab, vm.SelectedRegion);

                        // Start click-through; tool strip toggles draw mode.
                        _overlayWindow?.SetClickThrough(true);

                        // Ensure the tool strip stays above the fullscreen overlay.
                        _toolStripWindow?.BringToFront();
                        if (_toolStripWindow is not null)
                        {
                            _overlayWindow?.SetZOrderBelow(_toolStripWindow.Hwnd);

                            // Prevent drawing over the tool strip UI by making the overlay pass clicks through there.
                            if (_toolStripWindow.TryGetBounds(out var tb))
                            {
                                _overlayWindow?.SetInputPassthroughRect(tb);
                            }
                        }

                        vm.StatusMessage = "Recording... (use tool strip for Draw/Tools)";
                    }
                    catch (Exception overlayEx)
                    {
                        Breadcrumbs.Write("MainPage: Overlay start failed (continuing without overlay)");
                        Breadcrumbs.Write(overlayEx.ToString());
                    }
                }
                catch (Exception ex)
                {
                    Breadcrumbs.Write("MainPage: Record start exception BEGIN");
                    Breadcrumbs.Write(ex.ToString());
                    Breadcrumbs.Write("MainPage: Record start exception END");
                    vm.StatusMessage = ex.InnerException?.Message ?? ex.Message;
                    vm.IsRecording = false;
                    if (vm.SelectedCaptureItem is not null)
                    {
                        _preview?.Start(vm.SelectedCaptureItem);
                    }
                }
            }
            else
            {
                Breadcrumbs.Write("MainPage: Record OFF");
                _recorder?.Stop(finalize: true);

                var finalized = _recorder?.LastFinalFilePath;

                _isDrawing = false;
                _drawingPointerId = null;
                DrawOverlay.IsHitTestVisible = false;
                RegionOverlay.IsHitTestVisible = true;

                try { _overlayWindow?.Close(); } catch { }
                _overlayWindow = null;

                try { _toolStripWindow?.Close(); } catch { }
                _toolStripWindow = null;

                // If not monitoring, stop the shared audio source to avoid background capture.
                if (!vm.IsMonitoring)
                {
                    try { _audio?.Stop(); } catch { }
                }

                if (vm.SelectedCaptureItem is not null)
                {
                    _preview?.Start(vm.SelectedCaptureItem);
                }

                // Post-capture trimmer (lightweight) to quickly remove first/last seconds.
                try
                {
                    if (!string.IsNullOrWhiteSpace(finalized) && System.IO.File.Exists(finalized))
                    {
                        var w = new PostCaptureTrimmerWindow(finalized);
                        w.Activate();
                    }
                }
                catch (Exception ex)
                {
                    Breadcrumbs.Write("MainPage: post-capture trimmer failed to open");
                    Breadcrumbs.Write(ex);
                }
            }
        }

        private void OnToolStripDrawModeChanged(object? sender, bool drawOn)
        {
            Breadcrumbs.Write($"MainPage: tool strip draw mode={(drawOn ? "ON" : "OFF")}");

            try
            {
                if (!drawOn)
                {
                    // If the user turns Draw off mid-stroke, force a clean exit.
                    _overlayWindow?.CancelDrawing();
                }

                _overlayWindow?.SetClickThrough(!drawOn);
            }
            catch { }

            // Keep tool strip clickable even while overlay is interactive.
            try { _toolStripWindow?.BringToFront(); } catch { }

            try
            {
                if (_toolStripWindow is not null)
                {
                    _overlayWindow?.SetZOrderBelow(_toolStripWindow.Hwnd);
                }
            }
            catch { }
        }

        private void OnToolStripToolChanged(object? sender, AnnotationTool tool)
        {
            try
            {
                _annotations?.SetTool(tool);
                _overlayWindow?.SetTool(tool);
            }
            catch { }
        }

        private void OnToolStripUndo(object? sender, EventArgs e)
        {
            try { _overlayWindow?.Undo(); } catch { }
        }

        private void OnToolStripClear(object? sender, EventArgs e)
        {
            try { _overlayWindow?.Clear(); } catch { }
        }

        private void OnToolStripBoundsChanged(object? sender, RectInt32 bounds)
        {
            try { _overlayWindow?.SetInputPassthroughRect(bounds); } catch { }
        }

        private void OnToolStripBubbleChanged(object? sender, bool enabled)
        {
            _bubbleEnabled = enabled;
            _ = ToggleBubbleAsync(enabled);
        }

        private async Task ToggleBubbleAsync(bool enabled)
        {
            var recorder = _recorder;
            if (recorder is null)
            {
                return;
            }

            Breadcrumbs.Write($"MainPage: ToggleBubbleAsync enabled={enabled}");

            if (!enabled)
            {
                try { recorder.SetPersonaFrameSource(null); } catch { }

                try { _webcam?.Dispose(); } catch { }
                _webcam = null;
                return;
            }

            try
            {
                _webcam ??= new WebcamFrameSource();

                var owner = ScreenRecorder.App.App.MainWindow ?? (Microsoft.UI.Xaml.Window?)_toolStripWindow;
                if (owner is null)
                {
                    Breadcrumbs.Write("MainPage: Bubble enabled but no owner window available");
                    return;
                }

                var ok = await _webcam.StartAsync(owner, CancellationToken.None);
                if (!ok)
                {
                    Breadcrumbs.Write("MainPage: Bubble enabled but webcam start failed");
                    try { recorder.SetPersonaFrameSource(null); } catch { }
                    try { _webcam.Dispose(); } catch { }
                    _webcam = null;
                    return;
                }

                // Use accent-ish border color where available; fallback to white.
                try
                {
                    var ui = new Windows.UI.ViewManagement.UISettings();
                    var accent = ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
                    recorder.SetPersonaBorderColor(new Vortice.Mathematics.Color4(accent.R / 255f, accent.G / 255f, accent.B / 255f, 1f));
                }
                catch
                {
                    recorder.SetPersonaBorderColor(new Vortice.Mathematics.Color4(1f, 1f, 1f, 1f));
                }

                recorder.SetPersonaFrameSource(_webcam);
                Breadcrumbs.Write("MainPage: Bubble enabled (persona source set)");
            }
            catch (Exception ex)
            {
                Breadcrumbs.Write("MainPage: Bubble toggle failed");
                Breadcrumbs.Write(ex);
                try { recorder.SetPersonaFrameSource(null); } catch { }
            }
        }

        private void BeginUiPrimitive(Windows.Foundation.Point overlayPos)
        {
            var tool = DrawToolCombo.SelectedIndex switch
            {
                1 => AnnotationTool.Arrow,
                2 => AnnotationTool.Highlighter,
                _ => AnnotationTool.Pen
            };

            if (tool == AnnotationTool.Arrow)
            {
                var line = new Line
                {
                    X1 = overlayPos.X,
                    Y1 = overlayPos.Y,
                    X2 = overlayPos.X,
                    Y2 = overlayPos.Y,
                    Stroke = new SolidColorBrush(Colors.White),
                    StrokeThickness = 5,
                };

                var h1 = new Line
                {
                    X1 = overlayPos.X,
                    Y1 = overlayPos.Y,
                    X2 = overlayPos.X,
                    Y2 = overlayPos.Y,
                    Stroke = new SolidColorBrush(Colors.White),
                    StrokeThickness = 5,
                };

                var h2 = new Line
                {
                    X1 = overlayPos.X,
                    Y1 = overlayPos.Y,
                    X2 = overlayPos.X,
                    Y2 = overlayPos.Y,
                    Stroke = new SolidColorBrush(Colors.White),
                    StrokeThickness = 5,
                };

                DrawOverlay.Children.Add(line);
                DrawOverlay.Children.Add(h1);
                DrawOverlay.Children.Add(h2);

                _activeArrowLine = line;
                _activeArrowHead1 = h1;
                _activeArrowHead2 = h2;
                return;
            }

            var poly = new Polyline
            {
                Stroke = tool == AnnotationTool.Highlighter
                    ? new SolidColorBrush(Colors.Yellow) { Opacity = 0.55 }
                    : new SolidColorBrush(Colors.Red),
                StrokeThickness = tool == AnnotationTool.Highlighter ? 18 : 4,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            poly.Points.Add(overlayPos);
            DrawOverlay.Children.Add(poly);
            _activePolyline = poly;
        }

        private void UpdateUiPrimitive(Windows.Foundation.Point overlayPos)
        {
            if (_activePolyline is not null)
            {
                var pts = _activePolyline.Points;
                if (pts.Count == 0 || (pts[^1].X - overlayPos.X) * (pts[^1].X - overlayPos.X) + (pts[^1].Y - overlayPos.Y) * (pts[^1].Y - overlayPos.Y) > 0.25)
                {
                    pts.Add(overlayPos);
                }
                return;
            }

            if (_activeArrowLine is not null && _activeArrowHead1 is not null && _activeArrowHead2 is not null)
            {
                _activeArrowLine.X2 = overlayPos.X;
                _activeArrowLine.Y2 = overlayPos.Y;

                // Arrow head
                var sx = _activeArrowLine.X1;
                var sy = _activeArrowLine.Y1;
                var ex = _activeArrowLine.X2;
                var ey = _activeArrowLine.Y2;

                var dx = ex - sx;
                var dy = ey - sy;
                var len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 8)
                {
                    _activeArrowHead1.X1 = ex; _activeArrowHead1.Y1 = ey;
                    _activeArrowHead1.X2 = ex; _activeArrowHead1.Y2 = ey;
                    _activeArrowHead2.X1 = ex; _activeArrowHead2.Y1 = ey;
                    _activeArrowHead2.X2 = ex; _activeArrowHead2.Y2 = ey;
                    return;
                }

                dx /= len;
                dy /= len;
                var head = Math.Min(22.0, Math.Max(12.0, len * 0.18));
                var rx = -dy;
                var ry = dx;

                var p1x = ex - dx * head + rx * (head * 0.5);
                var p1y = ey - dy * head + ry * (head * 0.5);
                var p2x = ex - dx * head - rx * (head * 0.5);
                var p2y = ey - dy * head - ry * (head * 0.5);

                _activeArrowHead1.X1 = ex;
                _activeArrowHead1.Y1 = ey;
                _activeArrowHead1.X2 = p1x;
                _activeArrowHead1.Y2 = p1y;

                _activeArrowHead2.X1 = ex;
                _activeArrowHead2.Y1 = ey;
                _activeArrowHead2.X2 = p2x;
                _activeArrowHead2.Y2 = p2y;
            }
        }

        private void EndUiPrimitive(Windows.Foundation.Point _)
        {
            if (_activePolyline is not null)
            {
                _drawUiPrimitives.Add(new List<UIElement> { _activePolyline });
                _activePolyline = null;
                return;
            }

            if (_activeArrowLine is not null && _activeArrowHead1 is not null && _activeArrowHead2 is not null)
            {
                _drawUiPrimitives.Add(new List<UIElement> { _activeArrowLine, _activeArrowHead1, _activeArrowHead2 });
                _activeArrowLine = null;
                _activeArrowHead1 = null;
                _activeArrowHead2 = null;
            }
        }

        private void CancelActiveUiPrimitive()
        {
            if (_activePolyline is not null)
            {
                DrawOverlay.Children.Remove(_activePolyline);
                _activePolyline = null;
            }

            if (_activeArrowLine is not null)
            {
                DrawOverlay.Children.Remove(_activeArrowLine);
                _activeArrowLine = null;
            }
            if (_activeArrowHead1 is not null)
            {
                DrawOverlay.Children.Remove(_activeArrowHead1);
                _activeArrowHead1 = null;
            }
            if (_activeArrowHead2 is not null)
            {
                DrawOverlay.Children.Remove(_activeArrowHead2);
                _activeArrowHead2 = null;
            }
        }

        private bool TryMapPointToOutput(ScreenRecorder.App.ViewModels.MainViewModel vm, Windows.Foundation.Point overlayPoint, out Vector2 output)
        {
            output = default;

            if (!TryComputePreviewRect(vm, out var px, out var py, out var pw, out var ph, out var itemW, out var itemH))
            {
                return false;
            }

            var x = overlayPoint.X;
            var y = overlayPoint.Y;
            if (x < px || x > px + pw || y < py || y > py + ph)
            {
                return false;
            }

            var nx = (x - px) / pw;
            var ny = (y - py) / ph;

            nx = Math.Clamp(nx, 0, 1);
            ny = Math.Clamp(ny, 0, 1);

            var ix = nx * itemW;
            var iy = ny * itemH;

            if (vm.SelectedRegion is { } crop)
            {
                if (ix < crop.X || ix > crop.X + crop.Width || iy < crop.Y || iy > crop.Y + crop.Height)
                {
                    return false;
                }

                ix -= crop.X;
                iy -= crop.Y;

                var ow = Math.Max(1, crop.Width);
                var oh = Math.Max(1, crop.Height);
                ix = Math.Clamp(ix, 0, ow - 1);
                iy = Math.Clamp(iy, 0, oh - 1);
            }

            output = new Vector2((float)ix, (float)iy);
            return true;
        }

        private void EnsureMeterTimer(ScreenRecorder.App.ViewModels.MainViewModel vm)
        {
            if (_meterTimer is not null)
            {
                return;
            }

            _meterTimer = DispatcherQueue.CreateTimer();
            _meterTimer.Interval = TimeSpan.FromMilliseconds(50);
            _meterTimer.Tick += (_, __) =>
            {
                if (_audio is null)
                {
                    vm.MicRms = 0;
                    vm.SystemRms = 0;
                    return;
                }

                vm.MicRms = Math.Clamp(_audio.MicRms, 0f, 1f);
                vm.SystemRms = Math.Clamp(_audio.SystemRms, 0f, 1f);

                // Feed mic RMS into the recorder for the persona ring animation.
                try { _recorder?.SetPersonaAudioLevel((float)vm.MicRms); } catch { }
            };
            _meterTimer.Start();
        }

        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (DataContext is not ScreenRecorder.App.ViewModels.MainViewModel vm)
            {
                return;
            }

            if (e.PropertyName is nameof(ScreenRecorder.App.ViewModels.MainViewModel.MicGain)
                or nameof(ScreenRecorder.App.ViewModels.MainViewModel.SystemGain))
            {
                ApplyAudioSettingsFromViewModel(vm);
            }
            else if (e.PropertyName is nameof(ScreenRecorder.App.ViewModels.MainViewModel.IsMonitoring))
            {
                ApplyMonitoringFromViewModel(vm);
            }
        }

        private void ApplyAudioSettingsFromViewModel(ScreenRecorder.App.ViewModels.MainViewModel vm)
        {
            if (_audio is null)
            {
                return;
            }

            _audio.MicGain = (float)Math.Clamp(vm.MicGain, 0, 2);
            _audio.SystemGain = (float)Math.Clamp(vm.SystemGain, 0, 2);
        }

        private void ApplyMonitoringFromViewModel(ScreenRecorder.App.ViewModels.MainViewModel vm)
        {
            if (_audio is null || _monitor is null)
            {
                return;
            }

            if (vm.IsMonitoring)
            {
                if (!_audio.IsRunning)
                {
                    _audio.StartDefault();
                }
                _monitor.Start();
            }
            else
            {
                _monitor.Stop();
                if (!vm.IsRecording)
                {
                    try { _audio.Stop(); } catch { }
                }
            }
        }

        private void TryStartAudioForMeters(ScreenRecorder.App.ViewModels.MainViewModel vm)
        {
            if (_audio is null)
            {
                return;
            }

            // If mic permission is blocked, don't attempt to start WASAPI capture here.
            if (vm.CanOpenSettings)
            {
                return;
            }

            if (_audio.IsRunning)
            {
                return;
            }

            try
            {
                _audio.StartDefault();
            }
            catch (Exception ex)
            {
                vm.StatusMessage = ex.InnerException?.Message ?? ex.Message;
            }
        }

        private void OnRecorderStatus(object? sender, string message)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (DataContext is ScreenRecorder.App.ViewModels.MainViewModel vm)
                {
                    vm.StatusMessage = message;

                    if (message.StartsWith("Recording to ", StringComparison.OrdinalIgnoreCase))
                    {
                        vm.CurrentRecordingFileName = message.Substring("Recording to ".Length).Trim();
                    }
                    else if (message.StartsWith("Saved:", StringComparison.OrdinalIgnoreCase))
                    {
                        vm.LastSavedFileName = message.Substring("Saved:".Length).Trim();
                        vm.CurrentRecordingFileName = null;
                    }
                }
            });
        }

        private void OnSourceTabChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (DataContext is not ScreenRecorder.App.ViewModels.MainViewModel vm)
            {
                return;
            }

            if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            {
                vm.SelectedTab = tag;
            }
        }
    }
}
