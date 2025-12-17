using System;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;

namespace ScreenRecorder.Prototype;

[ComImport]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    void GetInterface([In] ref Guid iid, out IntPtr p);
}

internal static class D3D11SurfaceInterop
{
    public static IntPtr GetDxgiInterface(IDirect3DSurface surface, Guid iid)
    {
        if (surface is null)
        {
            throw new ArgumentNullException(nameof(surface));
        }

        var access = (IDirect3DDxgiInterfaceAccess)surface;
        access.GetInterface(ref iid, out var p);
        return p;
    }
}
