using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VNotch.TestSupport;

// Linked into test/benchmark executables only, never into the shipped application.
internal static class BackgroundTestDesktop
{
    internal static string Name { get; } = $"VNotchTests-{Environment.ProcessId}-{Guid.NewGuid():N}";
    private static readonly Lazy<DesktopHandle> Desktop = new(CreateDesktop);
    [ThreadStatic] private static bool _attached;

    internal static void AttachCurrentThread()
    {
        if (_attached) return;
        // Fresh managed threads already have a COM apartment. Tear it down before
        // moving the thread, then initialize STA on the private desktop.
        Thread.CurrentThread.SetApartmentState(ApartmentState.Unknown);
        // Must precede Dispatcher/Application creation. There is deliberately no
        // SwitchDesktop permission or fallback to the user's input desktop.
        if (!SetThreadDesktop(Desktop.Value))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Cannot isolate the test UI from the user's desktop (Win32 {error}).");
        }
        Thread.CurrentThread.SetApartmentState(ApartmentState.STA);
        _attached = true;
    }

    private static DesktopHandle CreateDesktop()
    {
        const uint access = 0x0001 | 0x0002 | 0x0004 | 0x0008 | 0x0040 | 0x0080;
        var desktop = CreateDesktopW(Name, IntPtr.Zero, IntPtr.Zero, 0, access, IntPtr.Zero);
        if (desktop.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            desktop.Dispose();
            throw new Win32Exception(error, "Cannot create a background test desktop.");
        }
        // Keep the handle until process exit: CloseDesktop cannot close a desktop
        // still assigned to the persistent STA thread. Windows releases it on exit.
        return desktop;
    }

    private sealed class DesktopHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public DesktopHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => CloseDesktop(handle);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern DesktopHandle CreateDesktopW(string name, IntPtr device, IntPtr deviceMode,
        uint flags, uint access, IntPtr securityAttributes);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(DesktopHandle desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
}
