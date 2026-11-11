using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services;

internal sealed partial class LyricsService
{
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_lyrics_is_credit_header_line(
        ReadOnlySpan<char> text,
        int textLen,
        long timeTicks,
        out byte outIsCredit);

    [LibraryImport(NativeDll)]
    private static partial int vn_lyrics_decode_krc_payload(
        ReadOnlySpan<byte> rawBytes,
        int rawLen,
        Span<byte> outZlibBytes,
        out int outZlibLen);

    private static readonly HttpClient NetEaseHttp = CreateRefererClient("https://music.163.com", "https://music.163.com/", 2200);
    private static readonly HttpClient KugouHttp = CreateClient("https://lyrics.kugou.com", 2000);
    private static readonly HttpClient QqMusicHttp = CreateRefererClient("https://c.y.qq.com", "https://y.qq.com/", 2200);
    private long _netEaseRetryUntil, _kugouRetryUntil, _qqMusicRetryUntil;

    private static HttpClient CreateRefererClient(string address, string referer, int timeoutMs)
    {
        var client = CreateClient(address, timeoutMs);
        client.DefaultRequestHeaders.Referrer = new Uri(referer);
        return client;
    }

    private async Task<LyricsResult?> TryNetEaseAsync(
        string trackName,
        string artistName,
        int durationSeconds,
        CancellationToken token)
    {
        if (DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _netEaseRetryUntil)) return null;
        string cleanTrack = CleanTitle(trackName);
        string cleanArtist = CleanArtist(artistName);
        string query = string.IsNullOrWhiteSpace(cleanArtist) ? cleanTrack : $"{cleanArtist} {cleanTrack}";
        string searchUrl = $"/api/cloudsearch/pc?s={Uri.EscapeDataString(query)}&type=1&offset=0&limit=8";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_netEaseClient.Timeout);

        try
        {
            using var searchResponse = await _netEaseClient.GetAsync(searchUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)searchResponse.StatusCode >= 500 || searchResponse.StatusCode == HttpStatusCode.TooManyRequests)
            {
                MarkCooldown(ref _netEaseRetryUntil, TimeSpan.FromMinutes(2));
                return null;
            }
            if (!searchResponse.IsSuccessStatusCode) return null;

            using var searchBody = await PooledUtf8Response.ReadAsync(searchResponse, MaxLyricsResponseBytes, timeout.Token).ConfigureAwait(false);
            var selection = NativeCatalogParser.Select(searchBody.Span, 3, trackName, artistName, durationSeconds * 1000L);
            if (selection.ApiCode == -460)
            {
                MarkCooldown(ref _netEaseRetryUntil, TimeSpan.FromMinutes(2));
                return null;
            }
            long songId = selection.Id;
            if (songId <= 0) return null;

            string lyricUrl = $"/api/song/lyric?id={songId}&lv=-1&yv=-1&tv=-1";
            using var lyricResponse = await _netEaseClient.GetAsync(lyricUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)lyricResponse.StatusCode >= 500 || lyricResponse.StatusCode == HttpStatusCode.TooManyRequests)
            {
                MarkCooldown(ref _netEaseRetryUntil, TimeSpan.FromMinutes(2));
                return null;
            }
            if (!lyricResponse.IsSuccessStatusCode) return null;

            using var lyricBody = await PooledUtf8Response.ReadAsync(lyricResponse, MaxLyricsResponseBytes, timeout.Token).ConfigureAwait(false);
            var lines = NativeLyricParser.Parse(lyricBody.Span, 7);
            return lines.Count == 0 ? null : new(lines, "NetEase");
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
        {
            MarkCooldown(ref _netEaseRetryUntil, TimeSpan.FromMinutes(2));
            throw;
        }
    }

    private async Task<LyricsResult?> TryKugouAsync(
        string trackName,
        string artistName,
        int durationSeconds,
        CancellationToken token)
    {
        if (DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _kugouRetryUntil)) return null;
        string cleanTrack = CleanTitle(trackName);
        string cleanArtist = CleanArtist(artistName);
        string keyword = string.IsNullOrWhiteSpace(cleanArtist) ? cleanTrack : $"{cleanArtist} - {cleanTrack}";
        int durationMs = durationSeconds > 0 ? durationSeconds * 1000 : 0;
        string searchUrl = $"/search?ver=1&man=yes&client=pc&keyword={Uri.EscapeDataString(keyword)}&duration={durationMs}&hash=";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_kugouClient.Timeout);

        try
        {
            using var searchResponse = await _kugouClient.GetAsync(searchUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)searchResponse.StatusCode >= 500 || searchResponse.StatusCode == HttpStatusCode.TooManyRequests)
            {
                MarkCooldown(ref _kugouRetryUntil, TimeSpan.FromMinutes(2));
                return null;
            }
            if (!searchResponse.IsSuccessStatusCode) return null;

            using var searchBody = await PooledUtf8Response.ReadAsync(searchResponse, MaxLyricsResponseBytes, timeout.Token).ConfigureAwait(false);
            var selection = NativeCatalogParser.Select(searchBody.Span, 4, trackName, artistName, durationSeconds * 1000L);
            var (id, accessKey) = (selection.Text, selection.Key);
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(accessKey)) return null;

            string downloadUrl = $"/download?ver=1&client=pc&id={Uri.EscapeDataString(id)}&accesskey={Uri.EscapeDataString(accessKey)}&fmt=krc&charset=utf8";
            using var downloadResponse = await _kugouClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)downloadResponse.StatusCode >= 500 || downloadResponse.StatusCode == HttpStatusCode.TooManyRequests)
            {
                MarkCooldown(ref _kugouRetryUntil, TimeSpan.FromMinutes(2));
                return null;
            }
            if (!downloadResponse.IsSuccessStatusCode) return null;

            using var downloadBody = await PooledUtf8Response.ReadAsync(downloadResponse, MaxLyricsResponseBytes, timeout.Token).ConfigureAwait(false);
            var fields = YouTubeMetadataParsing.ParseUtf8(downloadBody.Span, 10).Items;
            return fields.Count == 0 ? null : ParseKugouContent(fields[0].First);
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
        {
            MarkCooldown(ref _kugouRetryUntil, TimeSpan.FromMinutes(2));
            throw;
        }
    }

    private async Task<LyricsResult?> TryQqMusicAsync(
        string trackName,
        string artistName,
        int durationSeconds,
        CancellationToken token)
    {
        if (DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _qqMusicRetryUntil)) return null;
        string cleanTrack = CleanTitle(trackName);
        string cleanArtist = CleanArtist(artistName);
        string query = string.IsNullOrWhiteSpace(cleanArtist) ? cleanTrack : $"{cleanArtist} {cleanTrack}";
        string searchUrl = $"/soso/fcgi-bin/client_search_cp?w={Uri.EscapeDataString(query)}&p=1&n=8&format=json";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_qqMusicClient.Timeout);

        try
        {
            using var searchResponse = await _qqMusicClient.GetAsync(searchUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)searchResponse.StatusCode >= 500 || searchResponse.StatusCode == HttpStatusCode.TooManyRequests)
            {
                MarkCooldown(ref _qqMusicRetryUntil, TimeSpan.FromMinutes(2));
                return null;
            }
            if (!searchResponse.IsSuccessStatusCode) return null;

            using var searchBody = await PooledUtf8Response.ReadAsync(searchResponse, MaxLyricsResponseBytes, timeout.Token).ConfigureAwait(false);
            string? songMid = NativeCatalogParser.Select(searchBody.Span, 5, trackName, artistName, durationSeconds * 1000L).Text;
            if (string.IsNullOrEmpty(songMid)) return null;

            string lyricUrl = $"/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={Uri.EscapeDataString(songMid)}&g_tk=5381&format=json&inCharset=utf8&outCharset=utf-8&nobase64=1";
            using var lyricResponse = await _qqMusicClient.GetAsync(lyricUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)lyricResponse.StatusCode >= 500 || lyricResponse.StatusCode == HttpStatusCode.TooManyRequests)
            {
                MarkCooldown(ref _qqMusicRetryUntil, TimeSpan.FromMinutes(2));
                return null;
            }
            if (!lyricResponse.IsSuccessStatusCode) return null;

            using var lyricBody = await PooledUtf8Response.ReadAsync(lyricResponse, MaxLyricsResponseBytes, timeout.Token).ConfigureAwait(false);
            var fields = YouTubeMetadataParsing.ParseUtf8(lyricBody.Span, 9).Items;
            return fields.Count == 0 ? null : ParseQqMusicLyrics(fields[0].First);
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
        {
            MarkCooldown(ref _qqMusicRetryUntil, TimeSpan.FromMinutes(2));
            throw;
        }
    }

    internal static long SelectBestNetEaseSongId(JsonElement root, string targetTrack, string targetArtist, int targetDurationSeconds) =>
        NativeCatalogParser.Select(root.GetRawText(), 3, targetTrack, targetArtist, targetDurationSeconds * 1000L).Id;

    internal static (string? Id, string? AccessKey) SelectBestKugouCandidate(JsonElement root, string targetTrack, string targetArtist, int targetDurationSeconds)
    {
        var selected = NativeCatalogParser.Select(root.GetRawText(), 4, targetTrack, targetArtist, targetDurationSeconds * 1000L);
        return (selected.Text, selected.Key);
    }

    internal static string? SelectBestQqMusicSongMid(JsonElement root, string targetTrack, string targetArtist, int targetDurationSeconds) =>
        NativeCatalogParser.Select(root.GetRawText(), 5, targetTrack, targetArtist, targetDurationSeconds * 1000L).Text;

    internal static LyricsResult? ParseNetEaseLyricResult(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var lines = NativeLyricParser.Parse(Encoding.UTF8.GetBytes(root.GetRawText()), 7);
        return lines.Count == 0 ? null : new(lines, "NetEase");
    }

    private static LyricsResult? ParseQqMusicLyrics(string lyric)
    {
        string text = WebUtility.HtmlDecode(lyric);
        var filtered = FilterCreditLines(ParseLrc(text));
        return filtered.Count > 0 ? new LyricsResult(filtered, "QQ Music") : null;
    }

    internal static LyricsResult? ParseKugouDownloadResult(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("content", out var contentProp) || contentProp.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(contentProp.GetString()))
        {
            return null;
        }

        return ParseKugouContent(contentProp.GetString()!);
    }

    private static LyricsResult? ParseKugouContent(string content)
    {
        byte[] raw;
        try { raw = Convert.FromBase64String(content); }
        catch (FormatException) { return null; }

        if (raw.Length <= 4 || raw.Length > MaxLyricsResponseBytes) return null;

        byte[] rented = ArrayPool<byte>.Shared.Rent(raw.Length);
        try
        {
            if (vn_lyrics_decode_krc_payload(raw, raw.Length, rented, out int zlibLen) != 0 || zlibLen <= 0)
                return null;

            using var input = new MemoryStream(rented, 0, zlibLen, writable: false);
            using var zstream = new ZLibStream(input, CompressionMode.Decompress);
            using var plain = new Clipboard.NativeBufferStream();
            Clipboard.NativeClipboardStreamCopy.Copy(zstream, plain, MaxLyricsResponseBytes);
            var lines = plain.ReadBuffered(static bytes => NativeLyricParser.Parse(bytes, 5));
            return lines.Count > 0 ? new LyricsResult(lines, "Kugou") : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static List<LyricLine> FilterCreditLines(List<LyricLine> rawLines)
    {
        var filtered = new List<LyricLine>(rawLines.Count);
        foreach (var line in rawLines)
        {
            if (vn_lyrics_is_credit_header_line(line.Text.AsSpan(), line.Text.Length, line.Time.Ticks, out byte isCredit) == 0 && isCredit != 0)
                continue;
            filtered.Add(line);
        }
        return filtered;
    }
}
