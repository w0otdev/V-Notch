using System;
using System.Runtime.InteropServices;

namespace VNotch.Services.Translation;

// Tracks the outer JSON object across chunks without reparsing every string brace.
internal sealed partial class TranslationJsonBoundary
{
    private const string NativeDll = "VNotch.Native.dll";

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnJsonBoundaryState
    {
        public byte Quoted;
        public byte Escaped;
        public byte Started;
        public int Depth;
    }

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_json_boundary_append(
        ref VnJsonBoundaryState state,
        ReadOnlySpan<char> chunkPtr,
        int chunkLen,
        out byte outCompleted);

    private VnJsonBoundaryState _state;

    internal bool Append(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return false;
        ReadOnlySpan<char> span = chunk.AsSpan();
        vn_json_boundary_append(ref _state, span, span.Length, out byte completed);
        return completed != 0;
    }
}
