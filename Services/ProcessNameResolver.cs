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
        var (name, _) = TryGetNameAndExecutable(processId);
        return name.Length == 0 ? null : name;
    }

    internal static (string Name, string Executable) TryGetNameAndExecutable(uint processId)
    {
        if (processId == 0) return ("", "");
        IntPtr process = OpenProcess(QueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return ("", "");

        char[]? rented = null;
        try
        {
            Span<char> path = stackalloc char[1024];
            if (TryReadPath(process, path, out int length))
            {
                ReadOnlySpan<char> slice = path[..length];
                return (Path.GetFileNameWithoutExtension(slice).ToString(), slice.ToString());
            }
            if (Marshal.GetLastPInvokeError() != InsufficientBuffer) return ("", "");

            rented = ArrayPool<char>.Shared.Rent(32768);
            if (!TryReadPath(process, rented.AsSpan(0, 32768), out length)) return ("", "");
            ReadOnlySpan<char> rentedSlice = rented.AsSpan(0, length);
            return (Path.GetFileNameWithoutExtension(rentedSlice).ToString(), rentedSlice.ToString());
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
