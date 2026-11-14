using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VNotch.Services;

// Process, registry, file and trust queries. Native side: Native/Services/SystemInterop.cpp.
internal static unsafe partial class Win32Interop
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct VnProcessEntry
    {
        public uint ProcessId;
        public uint ParentProcessId;
        public fixed char ExecutableFile[260];
    }

    [LibraryImport(NativeDll, EntryPoint = "vn_process_snapshot")]
    private static partial int NativeProcessSnapshot(VnProcessEntry* buffer, int capacity, out int written, out int total);

    [LibraryImport(NativeDll, EntryPoint = "vn_process_image_path_equals", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeProcessImagePathEquals(uint processId, string expected, int expectedLength, out byte equal);

    [LibraryImport(NativeDll, EntryPoint = "vn_process_app_user_model_id_equals", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeProcessAppUserModelIdEquals(long processHandle, string expected, int expectedLength, out byte equal);

    [LibraryImport(NativeDll, EntryPoint = "vn_file_create_delete_on_close", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeFileCreateDeleteOnClose(string path, int pathLength, out long handle, out int lastError);

    [LibraryImport(NativeDll, EntryPoint = "vn_update_verify_authenticode", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeVerifyAuthenticode(string path, int pathLength, out byte trusted);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_find_window", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeFindWindow(string className, int classNameLength, out long hwnd);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_send_message_timeout")]
    private static partial int NativeSendMessageTimeout(
        long hwnd, uint message, long wParam, long lParam, uint flags, uint timeoutMs, out long result, out int lastError);

    /// <summary>Captures every process (id 0 excluded). Returns the number of entries copied into <paramref name="buffer"/>; total is the live count.</summary>
    internal static int CaptureProcessSnapshot(VnProcessEntry[] buffer, out int total)
    {
        total = 0;
        fixed (VnProcessEntry* pinned = buffer)
            return NativeProcessSnapshot(pinned, buffer.Length, out int written, out total) == 0 ? written : 0;
    }

    /// <summary>True when the process image path equals <paramref name="expectedPath"/> (ordinal, ignore case).</summary>
    internal static bool ProcessImagePathEquals(uint processId, string expectedPath) =>
        NativeProcessImagePathEquals(processId, expectedPath, expectedPath.Length, out byte equal) == 0 && equal != 0;

    internal static bool ProcessAppUserModelIdEquals(IntPtr processHandle, string expected) =>
        NativeProcessAppUserModelIdEquals(processHandle, expected, expected.Length, out byte equal) == 0 && equal != 0;

    /// <summary>CREATE_NEW read handle that deletes the file once every handle is closed; null on failure (last error set).</summary>
    internal static SafeFileHandle? CreateDeleteOnCloseFile(string path)
    {
        if (NativeFileCreateDeleteOnClose(path, path.Length, out long handle, out int lastError) == 0)
            return new SafeFileHandle((IntPtr)handle, ownsHandle: true);
        Marshal.SetLastPInvokeError(lastError);
        return null;
    }

    internal static bool HasValidAuthenticodeSignature(string path) =>
        NativeVerifyAuthenticode(path, path.Length, out byte trusted) == 0 && trusted != 0;

    public static IntPtr FindWindow(string className) =>
        NativeFindWindow(className, className.Length, out long hwnd) == 0 ? (IntPtr)hwnd : IntPtr.Zero;

    /// <summary>SendMessageTimeoutW; returns non-zero when the message was delivered.</summary>
    public static IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result)
    {
        int status = NativeSendMessageTimeout(hwnd, message, wParam, lParam, flags, timeout, out long raw, out int lastError);
        result = (IntPtr)raw;
        if (status == 0) return (IntPtr)1;
        Marshal.SetLastPInvokeError(lastError);
        return IntPtr.Zero;
    }
}
