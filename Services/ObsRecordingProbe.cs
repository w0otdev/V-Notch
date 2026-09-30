using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace VNotch.Services;

internal sealed class ObsRecordingProbe
{
    private readonly Dictionary<string, LogState> _logs = new(StringComparer.OrdinalIgnoreCase);

    internal bool IsRecording()
    {
        var processes = Process.GetProcessesByName("obs64").Concat(Process.GetProcessesByName("obs32")).ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool active = false;
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var folders = new List<string>
                    {
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", "logs")
                    };
                    string? executable = process.MainModule?.FileName;
                    if (executable != null)
                    {
                        // OBS portable layout: bin/64bit/obs64.exe + config/obs-studio/logs.
                        var root = Directory.GetParent(executable)?.Parent?.Parent;
                        if (root != null) folders.Add(Path.Combine(root.FullName, "config", "obs-studio", "logs"));
                    }
                    foreach (string folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (!Directory.Exists(folder)) continue;
                        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.txt")
                            .OrderByDescending(f => f.LastWriteTimeUtc).Take(4))
                        {
                            // Historical/crashed sessions must not light the indicator.
                            if (!RecordingFileOwnership.IsFileOwnedBy(file.FullName, process)) continue;
                            string key = $"{process.Id}:{process.StartTime.ToFileTimeUtc()}:{file.FullName}";
                            seen.Add(key);
                            if (!_logs.TryGetValue(key, out var state)) _logs[key] = state = new LogState();
                            state.Read(file.FullName);
                            active |= state.Active;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException)
                {
                    // A failed provider must not prevent other recording or sensor probes.
                }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
            foreach (string key in _logs.Keys.Where(key => !seen.Contains(key)).ToArray()) _logs.Remove(key);
        }
        return active;
    }

    internal sealed class LogState
    {
        private long _position;
        private string _partialLine = "";
        private bool _recording, _streaming, _replay;
        internal bool Active => _recording || _streaming || _replay;

        internal void Read(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int maxRead = 1024 * 1024;
            long length = stream.Length;
            if (length < _position || length - _position > maxRead)
            {
                // Bounded initial read/backlog. Unknown earlier activity stays false.
                _recording = _streaming = _replay = false;
                _partialLine = "";
                _position = Math.Max(0, length - maxRead);
                if (_position > 0)
                {
                    stream.Position = _position;
                    while (stream.Position < length && stream.ReadByte() != '\n') { }
                    _position = stream.Position;
                }
            }
            stream.Position = _position;
            var bytes = new byte[(int)(length - _position)];
            int read = 0, count;
            while (read < bytes.Length && (count = stream.Read(bytes, read, bytes.Length - read)) > 0) read += count;
            _position += read;
            Feed(Encoding.UTF8.GetString(bytes, 0, read));
        }

        internal void Feed(string text)
        {
            string[] lines = (_partialLine + text).Split('\n');
            _partialLine = lines[^1].Length <= 4096 ? lines[^1] : "";
            foreach (string line in lines.Take(lines.Length - 1))
            {
                Apply(line, "Recording", ref _recording);
                Apply(line, "Streaming", ref _streaming);
                Apply(line, "Replay Buffer", ref _replay);
            }
        }

        private static void Apply(string line, string activity, ref bool active)
        {
            // Exact logger marker, not arbitrary mentions of recording in configuration.
            if (line.Contains($": ==== {activity} Start ===", StringComparison.Ordinal)) active = true;
            if (line.Contains($": ==== {activity} Stop ===", StringComparison.Ordinal)) active = false;
        }
    }
}
