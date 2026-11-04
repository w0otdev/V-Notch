using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace VNotch.Services;

internal static partial class NativeCatalogParser
{
    internal readonly record struct Selection(string? Text, string? Key, long Id, int ApiCode, int Score);
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_catalog_select_utf8(ReadOnlySpan<byte> bytes, int length, int kind,
        ReadOnlySpan<char> title, int titleLength, ReadOnlySpan<char> artist, int artistLength,
        ReadOnlySpan<char> album, int albumLength, long milliseconds, ReadOnlySpan<byte> context, int contextLength, long candidateId, Span<char> output, int capacity, out int written,
        Span<char> key, int keyCapacity, out int keyWritten, out long id, out int apiCode, out int score);
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_youtube_extract_api_key_utf8(ReadOnlySpan<byte> bytes, int length, Span<char> output, int capacity, out int written);

    internal static Selection Select(ReadOnlySpan<byte> bytes, int kind, string title, string artist, long milliseconds = 0, string? album = null,
        ReadOnlySpan<byte> context = default, long candidateId = 0)
    {
        if (bytes.IsEmpty) return default;
        Span<char> output = stackalloc char[512], key = stackalloc char[512];
        int status = vn_catalog_select_utf8(bytes, bytes.Length, kind, title, title.Length, artist, artist.Length,
            album.AsSpan(), album?.Length ?? 0, milliseconds, context, context.Length, candidateId, output, output.Length, out int length, key, key.Length,
            out int keyLength, out long id, out int code, out int score);
        if (status == -2 && length is >= 0 and <= YouTubeMetadataParsing.MaxJsonResponseBytes && keyLength is >= 0 and <= YouTubeMetadataParsing.MaxJsonResponseBytes)
        {
            char[]? outputBuffer = null, keyBuffer = null;
            try
            {
                outputBuffer = ArrayPool<char>.Shared.Rent(Math.Max(1, length)); keyBuffer = ArrayPool<char>.Shared.Rent(Math.Max(1, keyLength));
                status = vn_catalog_select_utf8(bytes, bytes.Length, kind, title, title.Length, artist, artist.Length,
                    album.AsSpan(), album?.Length ?? 0, milliseconds, context, context.Length, candidateId, outputBuffer, outputBuffer.Length, out length,
                    keyBuffer, keyBuffer.Length, out keyLength, out id, out code, out score);
                return status == 0 ? new(length == 0 ? null : new string(outputBuffer, 0, length), keyLength == 0 ? null : new string(keyBuffer, 0, keyLength), id, code, score) : default;
            }
            finally
            {
                if (outputBuffer != null) ArrayPool<char>.Shared.Return(outputBuffer);
                if (keyBuffer != null) ArrayPool<char>.Shared.Return(keyBuffer);
            }
        }
        if (status != 0) return default;
        return new(length == 0 ? null : new string(output[..length]), keyLength == 0 ? null : new string(key[..keyLength]), id, code, score);
    }

    // Compatibility for callers that already own UTF-16. HTTP hot paths pass pooled bytes.
    internal static Selection Select(string json, int kind, string title, string artist, long milliseconds = 0, string? album = null)
    {
        int size = Encoding.UTF8.GetByteCount(json);
        if (size > YouTubeMetadataParsing.MaxJsonResponseBytes) return default;
        byte[]? rented = size > 2048 ? ArrayPool<byte>.Shared.Rent(size) : null;
        Span<byte> bytes = rented == null ? stackalloc byte[size] : rented;
        try { int length = Encoding.UTF8.GetBytes(json, bytes); return Select(bytes[..length], kind, title, artist, milliseconds, album); }
        finally { if (rented != null) ArrayPool<byte>.Shared.Return(rented); }
    }

    internal static string? ExtractYouTubeApiKey(ReadOnlySpan<byte> html)
    {
        Span<char> key = stackalloc char[512];
        return vn_youtube_extract_api_key_utf8(html, html.Length, key, key.Length, out int written) == 0 && written > 0 ? new string(key[..written]) : null;
    }

    internal static bool ContainsPersistedQueryNotFound(ReadOnlySpan<byte> json)
    {
        ReadOnlySpan<byte> needle = "persistedquerynotfound"u8;
        for (int start = 0; start <= json.Length - needle.Length; ++start)
        {
            int at = 0;
            while (at < needle.Length && (json[start + at] | 0x20) == needle[at]) ++at;
            if (at == needle.Length) return true;
        }
        return false;
    }
}
