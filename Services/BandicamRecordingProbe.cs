using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace VNotch.Services;

// Bandicam's legacy capture path need not appear in Windows ConsentStore.
// Its last output path alone is historical: require a live recorder holding that file.
internal static class BandicamRecordingProbe
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
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Missing/inaccessible app settings or output are not recording evidence.
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        return false;
    }

    internal static bool IsSupportedOutputPath(string path)
    {
        // Avoid network filesystem probes in the privacy worker.
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3 ||
            !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return false;
        string extension = Path.GetExtension(path);
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".avi", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase);
    }

}
