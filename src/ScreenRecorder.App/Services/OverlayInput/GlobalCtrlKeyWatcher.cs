using System;
using System.Runtime.InteropServices;
using ScreenRecorder.App.Helpers;

namespace ScreenRecorder.App.Services.OverlayInput;

internal sealed class GlobalCtrlKeyWatcher : IDisposable
{
    private IntPtr _hook;
    private LowLevelKeyboardProc? _proc;

    public event EventHandler<bool>? CtrlChanged;

    private bool _isCtrlDown;

    public void Start()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _proc = HookCallback;
        // For WH_KEYBOARD_LL, passing a module handle is more reliable than IntPtr.Zero.
        var hMod = GetModuleHandle(null);
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);
        if (_hook == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            Breadcrumbs.Write($"GlobalCtrlKeyWatcher: SetWindowsHookEx failed err={err}");
        }
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero)
        {
            return;
        }

        try { UnhookWindowsHookEx(_hook); } catch { }
        _hook = IntPtr.Zero;
        _proc = null;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = (int)wParam;
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN || msg == WM_KEYUP || msg == WM_SYSKEYUP)
            {
                var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (kbd.vkCode == VK_LCONTROL || kbd.vkCode == VK_RCONTROL)
                {
                    var down = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
                    if (down != _isCtrlDown)
                    {
                        _isCtrlDown = down;
                        CtrlChanged?.Invoke(this, down);
                    }
                }
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Stop();
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const uint VK_LCONTROL = 0xA2;
    private const uint VK_RCONTROL = 0xA3;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
