using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace VNotch.Services;

internal sealed partial class FfmpegRecordingProbe
{
    private readonly Dictionary<(int Id, long Start), bool> _cache = new();
    private readonly Func<int, string?> _commandLineReader;

    internal FfmpegRecordingProbe(Func<int, string?>? commandLineReader = null)
    {
        _commandLineReader = commandLineReader ?? ReadCommandLine;
    }

    internal Func<int, string?> CommandLineReader
    {
        get => _commandLineReader;
        init => _commandLineReader = value ?? ReadCommandLine;
    }

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
                    bool capture = CheckProcess(key.Id, key.Item2);
                    active |= capture && !process.HasExited;
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or
                    System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                    // Ignore inaccessible process during process scan.
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

    internal bool CheckProcess(int pid, long startFileTimeUtc)
    {
        var key = (pid, startFileTimeUtc);
        if (_cache.TryGetValue(key, out bool capture))
            return capture;

        string? cmd = _commandLineReader(pid);
        if (cmd is null)
            return false;

        capture = IsScreenCaptureCommand(cmd);
        _cache[key] = capture;
        return capture;
    }

    internal static unsafe string? ReadCommandLine(int processId)
    {
        if (processId <= 0) return null;

        Span<char> stackBuf = stackalloc char[1024];
        int status;
        int written;
        fixed (char* ptr = stackBuf)
        {
            status = vn_ffmpeg_read_process_command_line((uint)processId, ptr, stackBuf.Length, out written);
        }
        if (status == 0 && written > 0 && written <= stackBuf.Length)
            return new string(stackBuf[..written]);
        if (status != -2 || written <= 0)
            return null;

        char[] rented = ArrayPool<char>.Shared.Rent(written);
        try
        {
            fixed (char* ptr = rented)
            {
                status = vn_ffmpeg_read_process_command_line((uint)processId, ptr, rented.Length, out written);
            }
            return status == 0 && written > 0 && written <= rented.Length
                ? new string(rented, 0, written)
                : null;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
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

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_ffmpeg_read_process_command_line(
        uint process_id, char* out_buf, int out_cap, out int out_written);
}
