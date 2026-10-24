using System.Runtime.InteropServices;

namespace VNotch.Services;

internal static partial class LocalPathPolicy
{
    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_local_path_is_unc(char* pathPtr, int pathLen, out byte outIsUnc);

    internal static unsafe bool IsUncPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        fixed (char* ptr = path)
        {
            _ = vn_local_path_is_unc(ptr, path.Length, out byte isUnc);
            if (isUnc != 0)
                return true;
        }

        try
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsUnc) return true;
        }
        catch (Exception)
        {
            // Malformed path strings that fail URI creation cannot be valid UNC paths.
        }
        return false;
    }
}
