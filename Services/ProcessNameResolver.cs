using System;
using System.Buffers;
using System.Runtime.InteropServices;

namespace VNotch.Services;

internal static partial class ProcessNameResolver
{
    private const int ErrBufferTooSmall = -2;

    internal static string? TryGetName(uint processId)
    {
        var (name, _) = TryGetNameAndExecutable(processId);
        return name.Length == 0 ? null : name;
    }

    internal static unsafe (string Name, string Executable) TryGetNameAndExecutable(uint processId)
    {
        if (processId == 0) return ("", "");

        Span<char> nameStack = stackalloc char[260];
        Span<char> execStack = stackalloc char[1024];
        int nameLen, execLen;
        int rc;
        fixed (char* pName = nameStack)
        fixed (char* pExec = execStack)
        {
            rc = vn_process_resolver_get_name_and_exec(
                processId,
                pName, nameStack.Length, out nameLen,
                pExec, execStack.Length, out execLen);
        }

        if (rc == 0)
        {
            if (nameLen == 0 && execLen == 0) return ("", "");
            return (new string(nameStack[..nameLen]), new string(execStack[..execLen]));
        }

        if (rc != ErrBufferTooSmall || execLen <= 0) return ("", "");

        char[] rentedName = ArrayPool<char>.Shared.Rent(Math.Max(1, nameLen));
        char[] rentedExec = ArrayPool<char>.Shared.Rent(Math.Max(1, execLen));
        try
        {
            fixed (char* pName = rentedName)
            fixed (char* pExec = rentedExec)
            {
                rc = vn_process_resolver_get_name_and_exec(
                    processId,
                    pName, rentedName.Length, out nameLen,
                    pExec, rentedExec.Length, out execLen);
            }
            if (rc != 0 || (nameLen == 0 && execLen == 0)) return ("", "");
            return (new string(rentedName, 0, nameLen), new string(rentedExec, 0, execLen));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rentedName);
            ArrayPool<char>.Shared.Return(rentedExec);
        }
    }

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_process_resolver_get_name_and_exec(
        uint process_id,
        char* name_buf, int name_cap, out int out_name_len,
        char* exec_buf, int exec_cap, out int out_exec_len);
}
