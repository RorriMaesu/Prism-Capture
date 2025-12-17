using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;

namespace ScreenRecorder.App.Services.Annotations;

public enum AnnotationTool
{
    Pen,
    Arrow,
    Highlighter
}

public sealed class AnnotationRenderer : IDisposable, IAnnotationOverlaySource
{
    private sealed record Stroke(AnnotationTool Tool, List<Vector2> Points);
    private sealed record Arrow(Vector2 Start, Vector2 End);

    private readonly object _gate = new();
    private readonly CanvasDevice _device;
    private CanvasRenderTarget? _rt;

    private readonly List<object> _primitives = new();

    private AnnotationTool _tool = AnnotationTool.Pen;
    private Stroke? _activeStroke;
    private Vector2? _activeArrowStart;
    private Vector2? _activeArrowEnd;

    private byte[]? _latest;
    private int _width;
    private int _height;
    private long _version;

    public AnnotationRenderer()
    {
        _device = new CanvasDevice();
    }

    public void EnsureSize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        lock (_gate)
        {
            if (_rt is not null && _width == width && _height == height)
            {
                return;
            }

            _rt?.Dispose();
            _rt = new CanvasRenderTarget(_device, width, height, 96);
            _width = width;
            _height = height;

            Render_NoLock();
        }
    }

    public void SetTool(AnnotationTool tool)
    {
        lock (_gate)
        {
            _tool = tool;
            _activeStroke = null;
            _activeArrowStart = null;
            _activeArrowEnd = null;
        }
    }

    public void Undo()
    {
        lock (_gate)
        {
            if (_primitives.Count > 0)
            {
                _primitives.RemoveAt(_primitives.Count - 1);
                Render_NoLock();
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _primitives.Clear();
            _activeStroke = null;
            _activeArrowStart = null;
            _activeArrowEnd = null;
            Render_NoLock();
        }
    }

    public void CancelActive()
    {
        lock (_gate)
        {
            if (_rt is null)
            {
                return;
            }

            if (_activeStroke is null && _activeArrowStart is null && _activeArrowEnd is null)
            {
                return;
            }

            _activeStroke = null;
            _activeArrowStart = null;
            _activeArrowEnd = null;
            Render_NoLock();
        }
    }

    public void PointerDown(Vector2 p)
    {
        lock (_gate)
        {
            if (_rt is null)
            {
                return;
            }

            p = ClampToBounds_NoLock(p);

            if (_tool == AnnotationTool.Arrow)
            {
                _activeArrowStart = p;
                _activeArrowEnd = p;
                Render_NoLock();
                return;
            }

            _activeStroke = new Stroke(_tool, new List<Vector2>(64) { p });
            Render_NoLock();
        }
    }

    public void PointerMove(Vector2 p)
    {
        lock (_gate)
        {
            if (_rt is null)
            {
                return;
            }

            p = ClampToBounds_NoLock(p);

            if (_tool == AnnotationTool.Arrow)
            {
                if (_activeArrowStart is null)
                {
                    return;
                }

                _activeArrowEnd = p;
                Render_NoLock();
                return;
            }

            if (_activeStroke is null)
            {
                return;
            }

            var pts = _activeStroke.Points;
            if (pts.Count == 0 || Vector2.DistanceSquared(pts[^1], p) > 0.5f)
            {
                pts.Add(p);
                Render_NoLock();
            }
        }
    }

    public void PointerUp(Vector2 p)
    {
        lock (_gate)
        {
            if (_rt is null)
            {
                return;
            }

            p = ClampToBounds_NoLock(p);

            if (_tool == AnnotationTool.Arrow)
            {
                if (_activeArrowStart is not null)
                {
                    var start = _activeArrowStart.Value;
                    var end = _activeArrowEnd ?? p;
                    _primitives.Add(new Arrow(start, end));
                    _activeArrowStart = null;
                    _activeArrowEnd = null;
                    Render_NoLock();
                }
                return;
            }

            if (_activeStroke is null)
            {
                return;
            }

            if (_activeStroke.Points.Count >= 2)
            {
                _primitives.Add(_activeStroke);
            }

            _activeStroke = null;
            Render_NoLock();
        }
    }

    public bool TryGetLatest(out byte[]? bgraPremul, out int width, out int height, out long version)
    {
        lock (_gate)
        {
            bgraPremul = _latest;
            width = _width;
            height = _height;
            version = _version;
            return bgraPremul is not null && width > 0 && height > 0;
        }
    }

    private Vector2 ClampToBounds_NoLock(Vector2 p)
    {
        var x = Math.Clamp(p.X, 0, Math.Max(0, _width - 1));
        var y = Math.Clamp(p.Y, 0, Math.Max(0, _height - 1));
        return new Vector2(x, y);
    }

    private void Render_NoLock()
    {
        if (_rt is null)
        {
            return;
        }

        using (var ds = _rt.CreateDrawingSession())
        {
            ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));

            foreach (var prim in _primitives)
            {
                DrawPrimitive_NoLock(ds, prim);
            }

            if (_tool == AnnotationTool.Arrow && _activeArrowStart is not null && _activeArrowEnd is not null)
            {
                DrawArrow_NoLock(ds, _activeArrowStart.Value, _activeArrowEnd.Value, isPreview: true);
            }
            else if (_activeStroke is not null)
            {
                DrawStroke_NoLock(ds, _activeStroke);
            }
        }

        _latest = _rt.GetPixelBytes();
        _version++;
    }

    private static void DrawPrimitive_NoLock(CanvasDrawingSession ds, object prim)
    {
        switch (prim)
        {
            case Stroke s:
                DrawStroke_NoLock(ds, s);
                break;
            case Arrow a:
                DrawArrow_NoLock(ds, a.Start, a.End, isPreview: false);
                break;
        }
    }

    private static void DrawStroke_NoLock(CanvasDrawingSession ds, Stroke stroke)
    {
        var pts = stroke.Points;
        if (pts.Count < 2)
        {
            return;
        }

        var (color, thickness) = stroke.Tool switch
        {
            AnnotationTool.Highlighter => (Windows.UI.Color.FromArgb(120, 255, 255, 0), 18f),
            _ => (Windows.UI.Color.FromArgb(220, 255, 64, 64), 4f),
        };

        // Draw as a single path geometry instead of per-segment lines.
        // This avoids opacity "stacking" at joints (highlighter gets darker/choppy on direction changes)
        // and produces smooth caps/joins.
        using var pb = new CanvasPathBuilder(ds.Device);
        pb.BeginFigure(pts[0]);
        for (var i = 1; i < pts.Count; i++)
        {
            pb.AddLine(pts[i]);
        }
        pb.EndFigure(CanvasFigureLoop.Open);

        using var geo = CanvasGeometry.CreatePath(pb);
        using var style = new CanvasStrokeStyle
        {
            StartCap = CanvasCapStyle.Round,
            EndCap = CanvasCapStyle.Round,
            DashCap = CanvasCapStyle.Round,
            LineJoin = CanvasLineJoin.Round,
        };

        ds.DrawGeometry(geo, color, thickness, style);
    }

    private static void DrawArrow_NoLock(CanvasDrawingSession ds, Vector2 start, Vector2 end, bool isPreview)
    {
        var color = isPreview
            ? Windows.UI.Color.FromArgb(160, 255, 255, 255)
            : Windows.UI.Color.FromArgb(220, 255, 255, 255);

        const float thickness = 5f;
        ds.DrawLine(start, end, color, thickness);

        var dir = end - start;
        var len = dir.Length();
        if (len < 8)
        {
            return;
        }

        dir /= len;
        var head = MathF.Min(22f, MathF.Max(12f, len * 0.18f));
        var right = new Vector2(-dir.Y, dir.X);
        var p1 = end - dir * head + right * (head * 0.5f);
        var p2 = end - dir * head - right * (head * 0.5f);

        ds.DrawLine(end, p1, color, thickness);
        ds.DrawLine(end, p2, color, thickness);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _rt?.Dispose();
            _rt = null;

            _device?.Dispose();
        }
    }
}
