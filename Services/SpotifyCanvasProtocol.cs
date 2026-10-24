using System;
using System.Runtime.InteropServices;
using System.Text;

namespace VNotch.Services;

internal static partial class SpotifyCanvasProtocol
{
    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_spotify_canvas_build_request(
        byte* trackIdUtf8, int trackIdLen, byte* outBuf, int outCap, out int outLen);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_spotify_canvas_extract_next_candidate(
        byte* protobufPtr, int protobufLen, ref int ioOuterOffset, out int outUrlOffset, out int outUrlLen, out byte outFound);

    internal static unsafe byte[] BuildCanvasRequest(string trackId)
    {
        ReadOnlySpan<char> chars = (trackId ?? string.Empty).AsSpan();
        int utf8Len = Encoding.UTF8.GetByteCount(chars);
        Span<byte> utf8 = utf8Len <= 512 ? stackalloc byte[utf8Len] : new byte[utf8Len];
        if (utf8Len > 0)
        {
            Encoding.UTF8.GetBytes(chars, utf8);
        }

        fixed (byte* pIn = utf8)
        {
            _ = vn_spotify_canvas_build_request(pIn, utf8Len, null, 0, out int outLen);
            if (outLen <= 0)
            {
                return Array.Empty<byte>();
            }
            byte[] res = GC.AllocateUninitializedArray<byte>(outLen);
            fixed (byte* pOut = res)
            {
                _ = vn_spotify_canvas_build_request(pIn, utf8Len, pOut, outLen, out _);
            }
            return res;
        }
    }

    internal static unsafe Uri? ParseCanvasResponse(ReadOnlySpan<byte> protobuf)
    {
        if (protobuf.IsEmpty)
            return null;

        fixed (byte* ptr = protobuf)
        {
            int outerOffset = 0;
            while (outerOffset < protobuf.Length)
            {
                int status = vn_spotify_canvas_extract_next_candidate(
                    ptr, protobuf.Length, ref outerOffset, out int urlOffset, out int urlLen, out byte found);
                if (status != 0 || found == 0)
                    break;

                string candidateStr = Encoding.UTF8.GetString(protobuf.Slice(urlOffset, urlLen));
                if (TryCreateCanvasUri(candidateStr, out var uri))
                    return uri;
            }
        }
        return null;
    }

    internal static bool TryCreateCanvasUri(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) || candidate.Scheme != Uri.UriSchemeHttps)
            return false;

        bool trustedHost = candidate.Host.Equals("scdn.co", StringComparison.OrdinalIgnoreCase) ||
                           candidate.Host.EndsWith(".scdn.co", StringComparison.OrdinalIgnoreCase);
        bool isMp4 = candidate.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
        if (!trustedHost || !isMp4)
            return false;

        uri = candidate;
        return true;
    }
}
