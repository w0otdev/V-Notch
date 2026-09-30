using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace VNotch.Services;

internal sealed class FfmpegRecordingProbe
{
    private readonly Dictionary<(int Id, long Start), bool> _cache = new();

    internal bool IsRecording()
    {
        var processes = Process.GetProcessesByName("ffmpeg");
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
                    // An inaccessible process is unknown, never positive evidence.
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
        object? locator = null, services = null, process = null;
        try
        {
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (type == null) return null;
            locator = Activator.CreateInstance(type);
            services = ((dynamic)locator!).ConnectServer(".", @"root\cimv2");
            process = ((dynamic)services).Get($"Win32_Process.Handle='{processId}'");
            return (string?)((dynamic)process).CommandLine;
        }
        finally
        {
            if (process != null) Marshal.FinalReleaseComObject(process);
            if (services != null) Marshal.FinalReleaseComObject(services);
            if (locator != null) Marshal.FinalReleaseComObject(locator);
        }
    }

    internal static bool IsScreenCaptureCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        IntPtr argv = CommandLineToArgvW(command, out int count);
        if (argv == IntPtr.Zero) return false;
        try
        {
            var args = new string[count];
            for (int i = 0; i < count; i++) args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            string? format = null;
            bool capture = false;
            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg is "-h" or "-help" or "-version" or "-list_devices" or "-list_options") return false;
                if (i + 1 >= args.Length) continue;
                string value = args[i + 1];
                if ((arg is "-frames:v" or "-vframes") && value == "1") return false;
                if (arg == "-f") { format = value; i++; }
                else if (arg == "-i")
                {
                    capture |= format == "gdigrab" && (value == "desktop" || value.StartsWith("title=", StringComparison.Ordinal) || value.StartsWith("hwnd=", StringComparison.Ordinal));
                    capture |= format == "dshow" && value.StartsWith("video=screen-capture-recorder", StringComparison.OrdinalIgnoreCase);
                    capture |= format == "lavfi" && HasDesktopFilter(value);
                    format = null;
                    i++;
                }
                else if (arg is "-filter_complex" or "-lavfi")
                {
                    capture |= HasDesktopFilter(value);
                    i++;
                }
            }
            return capture;
        }
        finally { LocalFree(argv); }
    }

    private static bool HasDesktopFilter(string value)
        => Regex.IsMatch(value, @"(?:^|[;,\]])\s*(?:ddagrab|gfxcapture)(?:\s*[=,;\[]|\s*$)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
