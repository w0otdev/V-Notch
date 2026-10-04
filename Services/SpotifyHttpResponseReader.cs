using System.IO;
using System.Net.Http;
using System.Text;

namespace VNotch.Services;

internal static class SpotifyHttpResponseReader
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const string LogCategory = "SPOTIFY-CANVAS";
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
        if (contentLength.HasValue && contentLength.Value > maxResponseBytes)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory = new MemoryStream(contentLength.HasValue ? (int)Math.Min(contentLength.Value, maxResponseBytes) : 8192);
        var buffer = new byte[8192];
        int totalRead = 0;

        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
            if (read == 0) break;
            totalRead += read;
            if (totalRead > maxResponseBytes)
                return null;
            await memory.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }

        return memory.Length > 0 ? memory.ToArray() : null;
    }

}
