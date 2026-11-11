using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

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

    internal static async Task<string> ReadBoundedStringAsync(
        HttpClient client,
        string url,
        int maxResponseBytes = MaxResponseBytes,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var (bytes, exceeded) = await ReadBoundedBytesCoreAsync(response, maxResponseBytes, token).ConfigureAwait(false);
        if (exceeded || bytes == null)
            throw new InvalidOperationException($"Response body exceeded limit of {maxResponseBytes} bytes.");
        return bytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes);
    }

    internal static async Task<JsonDocument?> ReadLimitedJsonAsync(
        HttpResponseMessage response,
        int maxResponseBytes,
        CancellationToken token,
        JsonDocumentOptions options = default)
    {
        byte[]? bytes = await ReadLimitedBytesAsync(response, token, maxResponseBytes).ConfigureAwait(false);
        return bytes == null ? null : JsonDocument.Parse(bytes, options);
    }

    internal static async Task<byte[]?> ReadLimitedBytesAsync(
        HttpResponseMessage response,
        CancellationToken token,
        int maxResponseBytes = MaxResponseBytes)
    {
        var (bytes, exceeded) = await ReadBoundedBytesCoreAsync(response, maxResponseBytes, token).ConfigureAwait(false);
        return exceeded || bytes is not { Length: > 0 } ? null : bytes;
    }

    private static async Task<(byte[]? Bytes, bool ExceededLimit)> ReadBoundedBytesCoreAsync(
        HttpResponseMessage response,
        int maxResponseBytes,
        CancellationToken token)
    {
        long? contentLength = response.Content.Headers.ContentLength;
        if (NativeResolveInitialCapacity(
                contentLength.HasValue ? (byte)1 : (byte)0,
                contentLength ?? 0L,
                maxResponseBytes,
                out int initialCapacity,
                out byte exceedsLimit) != 0)
        {
            if (maxResponseBytes < 0 || (contentLength.HasValue && (contentLength.Value < 0 || contentLength.Value > maxResponseBytes)))
                return (null, true);
            initialCapacity = contentLength is > 0 ? (int)Math.Min(contentLength.Value, 8192) : 4096;
        }
        else if (exceedsLimit != 0)
        {
            return (null, true);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory = new MemoryStream(initialCapacity);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int totalRead = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int readLimit = (int)Math.Min(buffer.Length, Math.Max(1L, (long)maxResponseBytes - totalRead + 1));
                int read = await stream.ReadAsync(buffer.AsMemory(0, readLimit), token).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
                bool within = NativeIsTotalWithinLimit(totalRead, maxResponseBytes, out byte withinLimit) == 0
                    ? withinLimit != 0
                    : totalRead <= maxResponseBytes;
                if (!within)
                    return (null, true);
                await memory.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }

            return (totalRead > 0 ? memory.ToArray() : Array.Empty<byte>(), false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
