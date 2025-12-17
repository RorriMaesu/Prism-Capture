using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace ScreenRecorder.App.Helpers;

internal static class WindowInterop
{
    public static IntPtr GetWindowHandle(Window window)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        return WinRT.Interop.WindowNative.GetWindowHandle(window);
    }

    public static void InitializeWithWindow(object picker, Window window)
    {
        if (picker is null)
        {
            throw new ArgumentNullException(nameof(picker));
        }

        var hwnd = GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

    public static bool TryExcludeWindowFromCapture(Window window)
    {
        try
        {
            var hwnd = GetWindowHandle(window);
            // Windows 10 2004+; excludes the window from most capture APIs.
            return SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        }
        catch
        {
            return false;
        }
    }

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
