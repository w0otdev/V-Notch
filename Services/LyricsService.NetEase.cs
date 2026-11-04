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
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnNetEaseWordSpan
    {
        public long StartTicks;
        public long EndTicks;
        public int TextOffset;
        public int TextLength;
    }

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_lyrics_score_netease_candidate(
        ReadOnlySpan<char> targetTitle,
        int targetTitleLen,
        ReadOnlySpan<char> targetArtist,
        int targetArtistLen,
        int targetDurationSec,
        ReadOnlySpan<char> candTitle,
        int candTitleLen,
        ReadOnlySpan<char> candArtists,
        int candArtistsLen,
        int candDurationMs,
        out int outScore,
        out byte outAccepted);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_lyrics_is_credit_header_line(
        ReadOnlySpan<char> text,
        int textLen,
        long timeTicks,
        out byte outIsCredit);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_lyrics_try_parse_yrc_line(
        ReadOnlySpan<char> rawLine,
        int rawLineLen,
        out long outLineTicks,
        Span<char> outTextBuf,
        int outTextCap,
        out int outTextLen,
        Span<VnNetEaseWordSpan> outWords,
        int outWordsCap,
        out int outWordsCount,
        out byte outOk);

    [LibraryImport(NativeDll)]
    private static partial int vn_lyrics_decode_krc_payload(
        ReadOnlySpan<byte> rawBytes,
        int rawLen,
        Span<byte> outZlibBytes,
        out int outZlibLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_lyrics_try_parse_krc_line(
        ReadOnlySpan<char> rawLine,
        int rawLineLen,
        out long outLineTicks,
        Span<char> outTextBuf,
        int outTextCap,
        out int outTextLen,
        Span<VnNetEaseWordSpan> outWords,
        int outWordsCap,
        out int outWordsCount,
        out byte outOk);

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

            using var searchDoc = await ReadBoundedJsonAsync(searchResponse.Content, timeout.Token).ConfigureAwait(false);
            if (searchDoc == null) return null;
            if (searchDoc.RootElement.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.Number &&
                codeProp.TryGetInt32(out int apiCode) && apiCode == -460)
            {
                MarkCooldown(ref _netEaseRetryUntil, TimeSpan.FromMinutes(2));
                return null;
            }

            long songId = SelectBestNetEaseSongId(searchDoc.RootElement, trackName, artistName, durationSeconds);
            if (songId <= 0) return null;

            string lyricUrl = $"/api/song/lyric?id={songId}&lv=-1&yv=-1&tv=-1";
            using var lyricResponse = await _netEaseClient.GetAsync(lyricUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)lyricResponse.StatusCode >= 500 || lyricResponse.StatusCode == HttpStatusCode.TooManyRequests)
            {
                MarkCooldown(ref _netEaseRetryUntil, TimeSpan.FromMinutes(2));
                return null;
            }
            if (!lyricResponse.IsSuccessStatusCode) return null;

            using var lyricDoc = await ReadBoundedJsonAsync(lyricResponse.Content, timeout.Token).ConfigureAwait(false);
            return lyricDoc == null ? null : ParseNetEaseLyricResult(lyricDoc.RootElement);
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

            using var searchDoc = await ReadBoundedJsonAsync(searchResponse.Content, timeout.Token).ConfigureAwait(false);
            if (searchDoc == null) return null;

            var (id, accessKey) = SelectBestKugouCandidate(searchDoc.RootElement, trackName, artistName, durationSeconds);
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(accessKey)) return null;

            string downloadUrl = $"/download?ver=1&client=pc&id={Uri.EscapeDataString(id)}&accesskey={Uri.EscapeDataString(accessKey)}&fmt=krc&charset=utf8";
            using var downloadResponse = await _kugouClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)downloadResponse.StatusCode >= 500 || downloadResponse.StatusCode == HttpStatusCode.TooManyRequests)
            {
                MarkCooldown(ref _kugouRetryUntil, TimeSpan.FromMinutes(2));
                return null;
            }
            if (!downloadResponse.IsSuccessStatusCode) return null;

            using var downloadDoc = await ReadBoundedJsonAsync(downloadResponse.Content, timeout.Token).ConfigureAwait(false);
            return downloadDoc == null ? null : ParseKugouDownloadResult(downloadDoc.RootElement);
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

            using var searchDoc = await ReadBoundedJsonAsync(searchResponse.Content, timeout.Token).ConfigureAwait(false);
            if (searchDoc == null) return null;

            string? songMid = SelectBestQqMusicSongMid(searchDoc.RootElement, trackName, artistName, durationSeconds);
            if (string.IsNullOrEmpty(songMid)) return null;

            string lyricUrl = $"/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={Uri.EscapeDataString(songMid)}&g_tk=5381&format=json&inCharset=utf8&outCharset=utf-8&nobase64=1";
            using var lyricResponse = await _qqMusicClient.GetAsync(lyricUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)lyricResponse.StatusCode >= 500 || lyricResponse.StatusCode == HttpStatusCode.TooManyRequests)
            {
                MarkCooldown(ref _qqMusicRetryUntil, TimeSpan.FromMinutes(2));
                return null;
            }
            if (!lyricResponse.IsSuccessStatusCode) return null;

            using var lyricDoc = await ReadBoundedJsonAsync(lyricResponse.Content, timeout.Token).ConfigureAwait(false);
            return lyricDoc == null ? null : ParseQqMusicLyricResult(lyricDoc.RootElement);
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
        {
            MarkCooldown(ref _qqMusicRetryUntil, TimeSpan.FromMinutes(2));
            throw;
        }
    }

    internal static long SelectBestNetEaseSongId(JsonElement root, string targetTrack, string targetArtist, int targetDurationSeconds)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        long bestId = 0;
        int bestScore = int.MinValue;
        var artistBuilder = new StringBuilder(128);

        foreach (var song in songs.EnumerateArray())
        {
            if (song.ValueKind != JsonValueKind.Object ||
                !song.TryGetProperty("id", out var idProp) || !idProp.TryGetInt64(out long id) || id <= 0 ||
                !song.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string candTitle = nameProp.GetString() ?? "";
            int candDurationMs = 0;
            if (song.TryGetProperty("dt", out var dtProp) && dtProp.ValueKind == JsonValueKind.Number)
                dtProp.TryGetInt32(out candDurationMs);
            else if (song.TryGetProperty("duration", out var durProp) && durProp.ValueKind == JsonValueKind.Number)
                durProp.TryGetInt32(out candDurationMs);

            artistBuilder.Clear();
            AppendNetEaseArtists(song, "ar", artistBuilder);
            AppendNetEaseArtists(song, "artists", artistBuilder);
            string candArtists = artistBuilder.ToString();

            if (vn_lyrics_score_netease_candidate(
                    targetTrack.AsSpan(), targetTrack.Length,
                    targetArtist.AsSpan(), targetArtist.Length,
                    targetDurationSeconds,
                    candTitle.AsSpan(), candTitle.Length,
                    candArtists.AsSpan(), candArtists.Length,
                    candDurationMs,
                    out int score,
                    out byte accepted) == 0 &&
                accepted != 0 && score > bestScore)
            {
                bestScore = score;
                bestId = id;
            }
        }

        return bestId;
    }

    internal static (string? Id, string? AccessKey) SelectBestKugouCandidate(JsonElement root, string targetTrack, string targetArtist, int targetDurationSeconds)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        string? bestId = null;
        string? bestKey = null;
        int bestScore = int.MinValue;

        foreach (var cand in candidates.EnumerateArray())
        {
            if (cand.ValueKind != JsonValueKind.Object ||
                !cand.TryGetProperty("id", out var idProp) ||
                !cand.TryGetProperty("accesskey", out var keyProp) || keyProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string id = idProp.ValueKind == JsonValueKind.String ? idProp.GetString() ?? "" : idProp.ToString();
            string accessKey = keyProp.GetString() ?? "";
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(accessKey)) continue;

            string candTitle = cand.TryGetProperty("song", out var songProp) && songProp.ValueKind == JsonValueKind.String
                ? songProp.GetString() ?? "" : "";
            string candArtists = cand.TryGetProperty("singer", out var singerProp) && singerProp.ValueKind == JsonValueKind.String
                ? (singerProp.GetString() ?? "").Replace('、', '|').Replace('/', '|') : "";
            int candDurationMs = cand.TryGetProperty("duration", out var durProp) && durProp.ValueKind == JsonValueKind.Number && durProp.TryGetInt32(out int d) ? d : 0;

            if (vn_lyrics_score_netease_candidate(
                    targetTrack.AsSpan(), targetTrack.Length,
                    targetArtist.AsSpan(), targetArtist.Length,
                    targetDurationSeconds,
                    candTitle.AsSpan(), candTitle.Length,
                    candArtists.AsSpan(), candArtists.Length,
                    candDurationMs,
                    out int score,
                    out byte accepted) == 0 &&
                accepted != 0 && score > bestScore)
            {
                bestScore = score;
                bestId = id;
                bestKey = accessKey;
            }
        }

        return (bestId, bestKey);
    }

    internal static string? SelectBestQqMusicSongMid(JsonElement root, string targetTrack, string targetArtist, int targetDurationSeconds)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("song", out var songObj) || songObj.ValueKind != JsonValueKind.Object ||
            !songObj.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? bestMid = null;
        int bestScore = int.MinValue;
        var artistBuilder = new StringBuilder(128);

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("songmid", out var midProp) || midProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string mid = midProp.GetString() ?? "";
            if (string.IsNullOrEmpty(mid)) continue;

            string candTitle = item.TryGetProperty("songname", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                ? nameProp.GetString() ?? ""
                : item.TryGetProperty("name", out nameProp) && nameProp.ValueKind == JsonValueKind.String ? nameProp.GetString() ?? "" : "";
            int intervalSec = item.TryGetProperty("interval", out var intProp) && intProp.ValueKind == JsonValueKind.Number && intProp.TryGetInt32(out int sec) ? sec : 0;

            artistBuilder.Clear();
            AppendNetEaseArtists(item, "singer", artistBuilder);
            string candArtists = artistBuilder.ToString();

            if (vn_lyrics_score_netease_candidate(
                    targetTrack.AsSpan(), targetTrack.Length,
                    targetArtist.AsSpan(), targetArtist.Length,
                    targetDurationSeconds,
                    candTitle.AsSpan(), candTitle.Length,
                    candArtists.AsSpan(), candArtists.Length,
                    intervalSec * 1000,
                    out int score,
                    out byte accepted) == 0 &&
                accepted != 0 && score > bestScore)
            {
                bestScore = score;
                bestMid = mid;
            }
        }

        return bestMid;
    }

    private static void AppendNetEaseArtists(JsonElement song, string propName, StringBuilder sb)
    {
        if (!song.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var ar in arr.EnumerateArray())
        {
            if (ar.ValueKind != JsonValueKind.Object) continue;
            if (ar.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()))
            {
                if (sb.Length > 0) sb.Append('|');
                sb.Append(n.GetString());
            }
            if (ar.TryGetProperty("alias", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
            {
                foreach (var al in aliases.EnumerateArray())
                {
                    if (al.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(al.GetString()))
                    {
                        if (sb.Length > 0) sb.Append('|');
                        sb.Append(al.GetString());
                    }
                }
            }
        }
    }

    internal static LyricsResult? ParseNetEaseLyricResult(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("nolyric", out var noLyric) && noLyric.ValueKind == JsonValueKind.True) return null;

        if (root.TryGetProperty("yrc", out var yrcObj) && yrcObj.ValueKind == JsonValueKind.Object &&
            yrcObj.TryGetProperty("lyric", out var yrcProp) && yrcProp.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(yrcProp.GetString()))
        {
            var yrcLines = ParseNetEaseYrc(yrcProp.GetString()!);
            if (yrcLines.Count > 0) return new LyricsResult(yrcLines, "NetEase");
        }

        if (root.TryGetProperty("lrc", out var lrcObj) && lrcObj.ValueKind == JsonValueKind.Object &&
            lrcObj.TryGetProperty("lyric", out var lrcProp) && lrcProp.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(lrcProp.GetString()))
        {
            var filtered = FilterCreditLines(ParseLrc(lrcProp.GetString()!));
            if (filtered.Count > 0) return new LyricsResult(filtered, "NetEase");
        }

        return null;
    }

    internal static LyricsResult? ParseQqMusicLyricResult(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("lyric", out var lyricProp) || lyricProp.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(lyricProp.GetString()))
        {
            return null;
        }

        string text = WebUtility.HtmlDecode(lyricProp.GetString()!);
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

        byte[] raw;
        try { raw = Convert.FromBase64String(contentProp.GetString()!); }
        catch (FormatException) { return null; }

        if (raw.Length <= 4 || raw.Length > MaxLyricsResponseBytes) return null;

        byte[] rented = ArrayPool<byte>.Shared.Rent(raw.Length);
        try
        {
            if (vn_lyrics_decode_krc_payload(raw, raw.Length, rented, out int zlibLen) != 0 || zlibLen <= 0)
                return null;

            using var input = new MemoryStream(rented, 0, zlibLen, writable: false);
            using var zstream = new ZLibStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(zstream, Encoding.UTF8);
            string krcText = reader.ReadToEnd();
            var lines = ParseKugouKrc(krcText);
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

    internal static List<LyricLine> ParseNetEaseYrc(string yrc)
    {
        var lines = new List<LyricLine>();
        Span<char> textBuf = stackalloc char[1024];
        Span<VnNetEaseWordSpan> wordBuf = stackalloc VnNetEaseWordSpan[128];

        foreach (var rawLine in yrc.AsSpan().EnumerateLines())
        {
            if (vn_lyrics_try_parse_yrc_line(
                    rawLine, rawLine.Length,
                    out long lineTicks,
                    textBuf, textBuf.Length, out int textLen,
                    wordBuf, wordBuf.Length, out int wordCount,
                    out byte ok) != 0 || ok == 0 || textLen <= 0)
            {
                continue;
            }

            string text = new(textBuf[..textLen]);
            var lineTime = TimeSpan.FromTicks(lineTicks);
            var words = new List<LyricWord>(wordCount);
            for (int i = 0; i < wordCount; i++)
            {
                var w = wordBuf[i];
                words.Add(new LyricWord(TimeSpan.FromTicks(w.StartTicks), TimeSpan.FromTicks(w.EndTicks), w.TextOffset, w.TextLength));
            }
            lines.Add(new LyricLine(lineTime, text, LyricTiming.ValidateWords(lineTime, text, words)));
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    internal static List<LyricLine> ParseKugouKrc(string krc)
    {
        var lines = new List<LyricLine>();
        Span<char> textBuf = stackalloc char[1024];
        Span<VnNetEaseWordSpan> wordBuf = stackalloc VnNetEaseWordSpan[128];

        foreach (var rawLine in krc.AsSpan().EnumerateLines())
        {
            if (vn_lyrics_try_parse_krc_line(
                    rawLine, rawLine.Length,
                    out long lineTicks,
                    textBuf, textBuf.Length, out int textLen,
                    wordBuf, wordBuf.Length, out int wordCount,
                    out byte ok) != 0 || ok == 0 || textLen <= 0)
            {
                continue;
            }

            string text = new(textBuf[..textLen]);
            var lineTime = TimeSpan.FromTicks(lineTicks);
            var words = new List<LyricWord>(wordCount);
            for (int i = 0; i < wordCount; i++)
            {
                var w = wordBuf[i];
                words.Add(new LyricWord(TimeSpan.FromTicks(w.StartTicks), TimeSpan.FromTicks(w.EndTicks), w.TextOffset, w.TextLength));
            }
            lines.Add(new LyricLine(lineTime, text, LyricTiming.ValidateWords(lineTime, text, words)));
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }
}
