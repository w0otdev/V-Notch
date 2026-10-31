using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace VNotch.Services;

internal sealed partial class ObsRecordingProbe
{
    private readonly Dictionary<string, LogState> _logs = new(StringComparer.OrdinalIgnoreCase);

    internal bool IsRecording(PrivacyProcessSnapshot snapshot)
    {
        var processes = snapshot.GetProcessesByExecutableName("obs64.exe", "obs32.exe");
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
                        var root = Directory.GetParent(executable)?.Parent?.Parent;
                        if (root != null) folders.Add(Path.Combine(root.FullName, "config", "obs-studio", "logs"));
                    }
                    foreach (string folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (!Directory.Exists(folder)) continue;
                        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.txt")
                            .OrderByDescending(f => f.LastWriteTimeUtc).Take(4))
                        {
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
                    // Ignore inaccessible process or log file.
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
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private long _position;
        private string _partialLine = "";
        private bool _recording, _streaming, _replay;
        internal bool Active => _recording || _streaming || _replay;
        internal string PartialLine => _partialLine;

        internal void Read(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int maxRead = 1024 * 1024;
            long length = stream.Length;
            if (length < _position || length - _position > maxRead)
            {
                _recording = _streaming = _replay = false;
                _partialLine = "";
                _decoder.Reset();
                _position = Math.Max(0, length - maxRead);
                if (_position > 0)
                {
                    stream.Position = _position;
                    while (stream.Position < length && stream.ReadByte() != '\n') { /* Advance to next line */ }
                    _position = stream.Position;
                }
            }
            if (length == _position) return;
            stream.Position = _position;
            int toRead = (int)(length - _position);
            byte[] bytes = ArrayPool<byte>.Shared.Rent(toRead);
            char[] chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(toRead) + 4);
            try
            {
                int read = 0, count;
                while (read < toRead && (count = stream.Read(bytes, read, toRead - read)) > 0) read += count;
                _position += read;
                if (read > 0)
                {
                    int charCount = _decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                    if (charCount > 0)
                    {
                        Feed(chars.AsSpan(0, charCount));
                    }
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(chars);
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }

        internal void Feed(string text) => Feed(text.AsSpan());

        internal unsafe void Feed(ReadOnlySpan<char> text)
        {
            if (text.Length == 0) return;

            byte rec = _recording ? (byte)1 : (byte)0;
            byte str = _streaming ? (byte)1 : (byte)0;
            byte rep = _replay ? (byte)1 : (byte)0;
            Span<char> outPartial = stackalloc char[4096];
            int outLen;

            fixed (char* pPartial = _partialLine)
            fixed (char* pText = text)
            fixed (char* pOut = outPartial)
            {
                vn_obs_feed_log_chunk(
                    pPartial, _partialLine.Length,
                    pText, text.Length,
                    ref rec, ref str, ref rep,
                    pOut, outPartial.Length, out outLen);
            }

            _recording = rec != 0;
            _streaming = str != 0;
            _replay = rep != 0;
            _partialLine = outLen > 0 ? new string(outPartial[..outLen]) : "";
        }
    }

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_obs_feed_log_chunk(
        char* partial_ptr, int partial_len,
        char* text_ptr, int text_len,
        ref byte io_recording, ref byte io_streaming, ref byte io_replay,
        char* out_partial_buf, int out_partial_cap, out int out_partial_len);
}
