using System;
using System.Runtime.InteropServices;

namespace ScreenRecorder.App.Helpers;

internal static class MonitorInterop
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    public static IntPtr GetPrimaryMonitor()
    {
        // Use the desktop window as a stable reference for the primary monitor.
        var desktop = GetDesktopWindow();
        return MonitorFromWindow(desktop, MONITOR_DEFAULTTOPRIMARY);
    }

    public static IntPtr GetMonitorUnderCursor()
    {
        if (!GetCursorPos(out var pt))
        {
            return IntPtr.Zero;
        }

        return MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
}
