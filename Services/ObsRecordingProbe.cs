using System;
using System.IO;
using System.Runtime.InteropServices;

namespace VNotch.Services;

internal sealed partial class ObsRecordingProbe
{
    internal sealed class LogState
    {
        private NativeLogState _readState;
        private string _partialLine = "";
        private bool _recording, _streaming, _replay;
        internal bool Active => _recording || _streaming || _replay;
        internal string PartialLine => _partialLine;

        internal void Read(string path)
        {
            Span<char> partial = stackalloc char[4096];
            if (vn_obs_read_log(path, path.Length, ref _readState, partial, partial.Length, out int written) != 0)
                throw new IOException("Could not read recorder log.");
            _recording = _readState.Recording != 0;
            _streaming = _readState.Streaming != 0;
            _replay = _readState.Replay != 0;
            _partialLine = written == 0 ? string.Empty : new string(partial[..written]);
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

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct NativeLogState
    {
        public ulong Position, FileId;
        public byte Recording, Streaming, Replay;
        public int PartialLength;
        public fixed byte Partial[4096];
    }
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_obs_read_log(ReadOnlySpan<char> path, int pathLength, ref NativeLogState state,
        Span<char> partial, int capacity, out int written);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_obs_feed_log_chunk(
        char* partial_ptr, int partial_len,
        char* text_ptr, int text_len,
        ref byte io_recording, ref byte io_streaming, ref byte io_replay,
        char* out_partial_buf, int out_partial_cap, out int out_partial_len);
}
