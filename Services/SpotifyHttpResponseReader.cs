using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;

namespace VNotch.Services;

internal static partial class SpotifyHttpResponseReader
{
    private const string NativeDll = "VNotch.Native.dll";
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const string LogCategory = "SPOTIFY-CANVAS";

    [LibraryImport(NativeDll, EntryPoint = "vn_spotify_http_response_reader_resolve_initial_capacity")]
    private static partial int NativeResolveInitialCapacity(
        byte hasContentLength,
        long contentLength,
        int maxResponseBytes,
        out int outInitialCapacity,
        out byte outExceedsLimit);

    [LibraryImport(NativeDll, EntryPoint = "vn_spotify_http_response_reader_is_total_within_limit")]
    private static partial int NativeIsTotalWithinLimit(
        int totalRead,
        int maxResponseBytes,
        out byte outWithinLimit);

    internal static async Task<string?> SendForStringAsync(
        HttpClient http,
        HttpRequestMessage request,
        CancellationToken token,
        int maxResponseBytes = MaxResponseBytes)
    {
        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string retryAfter = response.Headers.RetryAfter?.ToString() ?? "none";
            RuntimeLog.Debug(LogCategory, () =>
                $"Remote service returned HTTP {(int)response.StatusCode} for {request.RequestUri?.AbsolutePath}; " +
                $"retryAfter={retryAfter}");
            return null;
        }

        byte[]? bytes = await ReadLimitedBytesAsync(response, token, maxResponseBytes).ConfigureAwait(false);
        return bytes == null ? null : Encoding.UTF8.GetString(bytes);
    }

    internal static async Task<byte[]?> ReadLimitedBytesAsync(
        HttpResponseMessage response,
        CancellationToken token,
        int maxResponseBytes = MaxResponseBytes)
    {
        long? contentLength = response.Content.Headers.ContentLength;
        NativeResolveInitialCapacity(
            contentLength.HasValue ? (byte)1 : (byte)0,
            contentLength ?? 0L,
            maxResponseBytes,
            out int initialCapacity,
            out byte exceedsLimit);
        if (exceedsLimit != 0)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory = new MemoryStream(initialCapacity);
        var buffer = new byte[8192];
        int totalRead = 0;

        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
            if (read == 0) break;
            totalRead += read;
            NativeIsTotalWithinLimit(totalRead, maxResponseBytes, out byte withinLimit);
            if (withinLimit == 0)
                return null;
            await memory.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }

        return memory.Length > 0 ? memory.ToArray() : null;
    }

}
