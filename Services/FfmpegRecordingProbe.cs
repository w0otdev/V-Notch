using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace VNotch.Services;

internal sealed partial class FfmpegRecordingProbe
{
    private readonly Dictionary<(int Id, long Start), bool> _cache = new();

    internal bool IsRecording(PrivacyProcessSnapshot snapshot)
    {
        var processes = snapshot.GetProcessesByExecutableName("ffmpeg.exe");
        var seen = new HashSet<(int, long)>();
        bool active = false;
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var start = process.StartTime.ToUniversalTime();
                    var key = (process.Id, start.ToFileTimeUtc());
                    seen.Add(key);
                    if (DateTime.UtcNow - start < TimeSpan.FromSeconds(2)) continue;
                    if (!_cache.TryGetValue(key, out bool capture))
                        _cache[key] = capture = IsScreenCaptureCommand(ReadCommandLine(process.Id));
                    active |= capture && !process.HasExited;
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or
                    System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                    // Ignore inaccessible process during WMI/process scan.
                }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
            foreach (var key in _cache.Keys.Where(key => !seen.Contains(key)).ToArray()) _cache.Remove(key);
        }
        return active;
    }

    internal static string? ReadCommandLine(int processId)
    {
        object? locator = null, services = null, process = null, properties = null, commandLine = null;
        try
        {
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (type == null) return null;
            locator = Activator.CreateInstance(type);
            services = ((dynamic)locator!).ConnectServer(".", @"root\cimv2");
            process = ((dynamic)services).Get($"Win32_Process.Handle='{processId}'");
            properties = ((dynamic)process).Properties_;
            commandLine = ((dynamic)properties).Item("CommandLine");
            return (string?)((dynamic)commandLine).Value;
        }
        finally
        {
            ReleaseComObject(commandLine);
            ReleaseComObject(properties);
            ReleaseComObject(process);
            ReleaseComObject(services);
            ReleaseComObject(locator);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value == null || !Marshal.IsComObject(value)) return;
        try { Marshal.FinalReleaseComObject(value); }
        catch (InvalidComObjectException) { /* Already released */ }
    }

    internal static unsafe bool IsScreenCaptureCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        fixed (char* ptr = command)
        {
            vn_ffmpeg_is_screen_capture_command(ptr, command.Length, out byte capture);
            return capture != 0;
        }
    }

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_ffmpeg_is_screen_capture_command(
        char* cmd_ptr, int cmd_len, out byte out_is_capture);
}
