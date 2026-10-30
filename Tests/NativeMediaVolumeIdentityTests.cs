using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class NativeMediaVolumeIdentityTests
{
    [Fact]
    public void OpaqueWindowAppIdsResolveTheOwningExecutable() => SharedStaTestRunner.RunAsync(ct =>
    {
        var window = new Window { ShowInTaskbar = false, ShowActivated = false };
        nint hwnd = new WindowInteropHelper(window).EnsureHandle();
        Guid iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(hwnd, ref iid, out var store));
        var key = new PropertyKey { Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
        const string source = "VNotch.VolumeTest.7594D76B";
        var value = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(source) };
        try
        {
            Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
            Assert.Contains((uint)Environment.ProcessId, MediaSessionVolumeService.GetProcessIds(source));
        }
        finally
        {
            Marshal.FreeCoTaskMem(value.Pointer);
            value = default;
            store.SetValue(ref key, ref value);
            Marshal.ReleaseComObject(store);
            window.Close();
        }
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    });

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetPropertyStoreForWindow(nint hwnd, ref Guid iid, out IPropertyStore store);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey { public Guid Format; public uint Id; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
}
