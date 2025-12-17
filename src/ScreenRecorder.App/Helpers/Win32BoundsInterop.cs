using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics;

namespace ScreenRecorder.App.Helpers;

internal static class Win32BoundsInterop
{
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    public static bool TryGetPrimaryMonitorBounds(out RectInt32 bounds)
    {
        bounds = default;
        try
        {
            var desktop = GetDesktopWindow();
            var hMon = MonitorFromWindow(desktop, MONITOR_DEFAULTTOPRIMARY);
            if (hMon == IntPtr.Zero)
            {
                return false;
            }

            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf<MONITORINFO>();
            if (!GetMonitorInfo(hMon, ref mi))
            {
                return false;
            }

            bounds = new RectInt32(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top);
            return bounds.Width > 0 && bounds.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetMonitorBounds(IntPtr hMonitor, out RectInt32 bounds)
    {
        bounds = default;
        if (hMonitor == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf<MONITORINFO>();
            if (!GetMonitorInfo(hMonitor, ref mi))
            {
                return false;
            }

            bounds = new RectInt32(
                mi.rcMonitor.Left,
                mi.rcMonitor.Top,
                mi.rcMonitor.Right - mi.rcMonitor.Left,
                mi.rcMonitor.Bottom - mi.rcMonitor.Top);

            return bounds.Width > 0 && bounds.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetMonitorBoundsBySize(int pixelWidth, int pixelHeight, out RectInt32 bounds)
    {
        bounds = default;
        pixelWidth = Math.Max(1, pixelWidth);
        pixelHeight = Math.Max(1, pixelHeight);

        var preferred = MonitorFromPoint(GetCursorPoint(), MONITOR_DEFAULTTONEAREST);

        RectInt32? preferredMatch = null;
        RectInt32? firstMatch = null;

        try
        {
            _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, __, ___) =>
            {
                if (!TryGetMonitorBounds(hMon, out var b))
                {
                    return true;
                }

                if (b.Width != pixelWidth || b.Height != pixelHeight)
                {
                    return true;
                }

                if (firstMatch is null)
                {
                    firstMatch = b;
                }

                if (preferred != IntPtr.Zero && hMon == preferred)
                {
                    preferredMatch = b;
                    return false; // stop enumeration
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // ignore
        }

        if (preferredMatch is not null)
        {
            bounds = preferredMatch.Value;
            return true;
        }

        if (firstMatch is not null)
        {
            bounds = firstMatch.Value;
            return true;
        }

        return false;
    }

    public static bool TryFindTopLevelWindowByTitle(string title, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var target = title.Trim();
        var matches = new List<IntPtr>();

        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h))
            {
                return true;
            }

            var len = GetWindowTextLengthW(h);
            if (len <= 0 || len > 1024)
            {
                return true;
            }

            var sb = new StringBuilder(len + 1);
            _ = GetWindowTextW(h, sb, sb.Capacity);
            var t = sb.ToString();
            if (string.IsNullOrWhiteSpace(t))
            {
                return true;
            }

            // Exact match first; otherwise allow substring match as a fallback.
            if (string.Equals(t, target, StringComparison.OrdinalIgnoreCase))
            {
                matches.Clear();
                matches.Add(h);
                return false;
            }

            if (t.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0 || target.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                matches.Add(h);
            }

            return true;
        }, IntPtr.Zero);

        if (matches.Count == 0)
        {
            return false;
        }

        hwnd = matches[0];
        return hwnd != IntPtr.Zero;
    }

    public static bool TryGetWindowExtendedFrameBounds(IntPtr hwnd, out RectInt32 bounds)
    {
        bounds = default;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            // Extended frame bounds generally match what WGC captures better than GetWindowRect (no shadow).
            var rect = new RECT();
            var hr = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<RECT>());
            if (hr == 0)
            {
                bounds = new RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                return bounds.Width > 0 && bounds.Height > 0;
            }
        }
        catch
        {
            // Ignore and fall back below.
        }

        if (!GetWindowRect(hwnd, out var wr))
        {
            return false;
        }

        bounds = new RectInt32(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private static POINT GetCursorPoint()
    {
        if (!GetCursorPos(out var pt))
        {
            pt = default;
        }
        return pt;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
}
