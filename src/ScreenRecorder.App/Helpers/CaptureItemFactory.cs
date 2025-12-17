using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace ScreenRecorder.App.Helpers;

internal static class CaptureItemFactory
{
    // IGraphicsCaptureItemInterop
    // https://learn.microsoft.com/windows/win32/api/windows.graphics.capture.interop/
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        void CreateForWindow(IntPtr window, [In] ref Guid iid, out IntPtr result);
        void CreateForMonitor(IntPtr monitor, [In] ref Guid iid, out IntPtr result);
    }

    public static GraphicsCaptureItem? TryCreateForMonitor(IntPtr hMonitor)
    {
        if (hMonitor == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");

            var interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
            var hr = Marshal.QueryInterface(factory.ThisPtr, ref interopIid, out var interopPtr);
            if (hr != 0 || interopPtr == IntPtr.Zero)
            {
                Breadcrumbs.Write($"CaptureItemFactory.TryCreateForMonitor: QI IGraphicsCaptureItemInterop failed hr=0x{hr:X8} ptr=0x{interopPtr.ToInt64():X}");
                return null;
            }

            IGraphicsCaptureItemInterop interop;
            try
            {
                interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(interopPtr);
            }
            finally
            {
                Marshal.Release(interopPtr);
            }

            var iid = typeof(GraphicsCaptureItem).GUID;
            interop.CreateForMonitor(hMonitor, ref iid, out var ptr);
            Breadcrumbs.Write($"CaptureItemFactory.TryCreateForMonitor: hMonitor=0x{hMonitor.ToInt64():X} ptr=0x{ptr.ToInt64():X}");
            if (ptr == IntPtr.Zero)
            {
                Breadcrumbs.Write("CaptureItemFactory.TryCreateForMonitor: CreateForMonitor returned NULL");
                return null;
            }

            try
            {
                return (GraphicsCaptureItem)Marshal.GetObjectForIUnknown(ptr);
            }
            finally
            {
                Marshal.Release(ptr);
            }
        }
        catch (Exception ex)
        {
            Breadcrumbs.Write($"CaptureItemFactory.TryCreateForMonitor: failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public static GraphicsCaptureItem? TryCreateForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");

            var interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
            var hr = Marshal.QueryInterface(factory.ThisPtr, ref interopIid, out var interopPtr);
            if (hr != 0 || interopPtr == IntPtr.Zero)
            {
                Breadcrumbs.Write($"CaptureItemFactory.TryCreateForWindow: QI IGraphicsCaptureItemInterop failed hr=0x{hr:X8} ptr=0x{interopPtr.ToInt64():X}");
                return null;
            }

            IGraphicsCaptureItemInterop interop;
            try
            {
                interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(interopPtr);
            }
            finally
            {
                Marshal.Release(interopPtr);
            }

            var iid = typeof(GraphicsCaptureItem).GUID;
            interop.CreateForWindow(hwnd, ref iid, out var ptr);
            Breadcrumbs.Write($"CaptureItemFactory.TryCreateForWindow: hwnd=0x{hwnd.ToInt64():X} ptr=0x{ptr.ToInt64():X}");
            if (ptr == IntPtr.Zero)
            {
                Breadcrumbs.Write("CaptureItemFactory.TryCreateForWindow: CreateForWindow returned NULL");
                return null;
            }

            try
            {
                return (GraphicsCaptureItem)Marshal.GetObjectForIUnknown(ptr);
            }
            finally
            {
                Marshal.Release(ptr);
            }
        }
        catch (Exception ex)
        {
            Breadcrumbs.Write($"CaptureItemFactory.TryCreateForWindow: failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
