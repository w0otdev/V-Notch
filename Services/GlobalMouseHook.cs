using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VNotch.Services;

public static class InputMonitorService
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    private static LowLevelMouseProc? _proc;
    private static IntPtr _hookID = IntPtr.Zero;

    public static event EventHandler<POINT>? MouseActionTriggered;

    public static void Start()
    {
        if (_hookID != IntPtr.Zero) return;
        _proc = HookCallback;
        _hookID = SetHook(_proc);
    }

    public static void Stop()
    {
        if (_hookID == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hookID);
        _hookID = IntPtr.Zero;
    }

    private static IntPtr SetHook(LowLevelMouseProc proc)
    {
        var hModule = GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName!);
        return SetWindowsHookEx(WH_MOUSE_LL, proc, hModule, 0);
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_LBUTTONDOWN)
        {
            unsafe
            {
                var p = (MSLLHOOKSTRUCT*)lParam;
                MouseActionTriggered?.Invoke(null, p->pt);
            }
        }
        return CallNextHookEx(_hookID, nCode, wParam, lParam);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
