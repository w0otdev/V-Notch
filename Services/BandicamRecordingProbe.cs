using System;
using System.Runtime.InteropServices;

namespace VNotch.Services;

internal static partial class BandicamRecordingProbe
{
    internal static unsafe bool IsSupportedOutputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        fixed (char* ptr = path)
        {
            vn_bandicam_is_supported_output_path(ptr, path.Length, out byte supported);
            return supported != 0;
        }
    }

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_bandicam_is_supported_output_path(
        char* path_ptr, int path_len, out byte out_supported);
}
