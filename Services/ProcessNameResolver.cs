using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;

namespace VNotch.Services;

internal static class ProcessNameResolver
{
    private const uint QueryLimitedInformation = 0x1000;
    private const int InsufficientBuffer = 122;

    internal static string? TryGetName(uint processId)
    {
        if (processId == 0) return null;
        IntPtr process = OpenProcess(QueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return null;

        char[]? rented = null;
        try
        {
            Span<char> path = stackalloc char[1024];
            if (TryReadPath(process, path, out int length))
                return Path.GetFileNameWithoutExtension(path[..length]).ToString();
            if (Marshal.GetLastPInvokeError() != InsufficientBuffer) return null;

            rented = ArrayPool<char>.Shared.Rent(32768);
            return TryReadPath(process, rented.AsSpan(0, 32768), out length)
                ? Path.GetFileNameWithoutExtension(rented.AsSpan(0, length)).ToString()
                : null;
        }
        finally
        {
            if (rented != null) ArrayPool<char>.Shared.Return(rented);
            CloseHandle(process);
        }
    }

    private static unsafe bool TryReadPath(IntPtr process, Span<char> path, out int length)
    {
        uint size = (uint)path.Length;
        bool success;
        fixed (char* buffer = path)
            success = QueryFullProcessImageName(process, 0, buffer, ref size);
        length = success ? (int)size : 0;
        return success;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool QueryFullProcessImageName(IntPtr process, uint flags, char* path, ref uint size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
