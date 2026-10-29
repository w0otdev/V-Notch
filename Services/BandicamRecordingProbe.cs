using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VNotch.Services;

internal static partial class BandicamRecordingProbe
{
    internal static bool IsRecording(PrivacyProcessSnapshot snapshot)
    {
        var processes = snapshot.GetProcessesByExecutableName("bdcam.exe");
        try
        {
            if (processes.Length == 0) return false;
            using var options = Registry.CurrentUser.OpenSubKey(@"Software\BANDISOFT\BANDICAM\OPTION");
            if (options?.GetValue("sLatestRecordingFile") is not string path ||
                !IsSupportedOutputPath(path) || !File.Exists(path)) return false;

            foreach (var process in processes)
            {
                try
                {
                    if (RecordingFileOwnership.IsFileOwnedBy(path, process)) return true;
                }
                catch (InvalidOperationException) { /* Process exited */ }
                catch (System.ComponentModel.Win32Exception) { /* Access denied */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Ignore inaccessible directory.
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        return false;
    }

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
