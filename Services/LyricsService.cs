#pragma warning disable S1075 // Public lyrics API endpoints
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services;

internal sealed partial class LyricsService : IDisposable
{
    private const string LogTag = "LYRICS";
    private static readonly string[] GenericPlatformNames = { "YouTube", "Browser", "Google Chrome", "Microsoft Edge" };
    private static readonly string[] Dashes = { " - ", " – ", " — ", " // " };
    private static readonly string[] ArtistSeparators = { " feat.", " ft.", " featuring", " & ", ", ", " x " };

    [GeneratedRegex(@"[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex BracketedExtrasRegex();

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex TitleExtrasRegex();

    private const string VideoSuffixPattern = @"\s*\|\s*(?:Official|OFFICIAL|MV|mv|Music Video|Visualizer|Lyric Video|Audio|Track\s*No\.\d+).*";
    private const string RecordingSuffixPattern = @"\s*-\s*(?:Remaster(?:ed)?|Live|Acoustic|Radio Edit|Bonus Track|Single Version|Instrumental|Deluxe|Mono|Stereo).*";

    private static readonly HttpClient _lrclibHttp = new(NetworkPrivacy.Handler(NetworkFeature.Lyrics))
    {
        BaseAddress = new Uri("https://lrclib.net"),
        Timeout = TimeSpan.FromSeconds(8)
    };

    private static readonly HttpClient _lrcMuxHttp = new(NetworkPrivacy.Handler(NetworkFeature.Lyrics))
    {
        BaseAddress = new Uri("https://api.lrcmux.dev"),
        Timeout = TimeSpan.FromSeconds(12)
    };

    static LyricsService()
    {
        const string userAgent = "V-Notch/1.8.0 (https://github.com/rainaku/V-Notch)";
        _lrclibHttp.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _lrcMuxHttp.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    private readonly HttpClient _lrclibClient;
    private readonly HttpClient _lrcMuxClient;

    public LyricsService() : this(_lrclibHttp, _lrcMuxHttp) { }

    internal LyricsService(HttpClient lrclibClient, HttpClient lrcMuxClient, int cacheCapacity = 64)
    {
        if (cacheCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(cacheCapacity));
        _lrclibClient = lrclibClient;
        _lrcMuxClient = lrcMuxClient;
        _cacheCapacity = cacheCapacity;
    }

    private readonly object _fetchLock = new();
    private readonly int _cacheCapacity;
    private readonly Dictionary<FetchKey, (LyricsResult Result, long LastAccess)> _cache = new();
    private long _cacheAccess;
    private FetchOperation? _activeFetch;
    private bool _disposed;

    private readonly record struct FetchKey(string Track, string Artist, int DurationSeconds);

    private sealed class FetchOperation(FetchKey key)
    {
        public FetchKey Key { get; } = key;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<LyricsResult?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CancellationTask { get; set; } = Task.CompletedTask;
    }

    public Task<LyricsResult?> FetchSyncedLyricsAsync(string trackName, string artistName, int durationSeconds)
    {
        var key = new FetchKey(trackName, artistName, durationSeconds);
        FetchOperation operation;
        lock (_fetchLock)
        {
            if (_disposed) return Task.FromResult<LyricsResult?>(null);
            if (_activeFetch?.Key == key) return _activeFetch.Completion.Task;

            CancelActiveFetchLocked();
            if (_cache.TryGetValue(key, out var cached))
            {
                _cache[key] = (cached.Result, ++_cacheAccess);
                return Task.FromResult<LyricsResult?>(cached.Result);
            }

            operation = new FetchOperation(key);
            _activeFetch = operation;
        }

        _ = CompleteFetchAsync(operation);
        return operation.Completion.Task;
    }

    private async Task CompleteFetchAsync(FetchOperation operation)
    {
        LyricsResult? result = null;
        try
        {
            result = await FetchUncachedLyricsAsync(operation.Key, operation.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RuntimeLog.Log(LogTag, $"Error: {ex.Message}");
        }

        Task cancellationTask;
        lock (_fetchLock)
        {
            if (_disposed || operation.Cancellation.IsCancellationRequested)
                result = null;
            else if (result is { Lines.Count: > 0 })
            {
                if (_cache.Count >= _cacheCapacity)
                    _cache.Remove(_cache.MinBy(static entry => entry.Value.LastAccess).Key);
                _cache[operation.Key] = (result, ++_cacheAccess);
            }

            if (ReferenceEquals(_activeFetch, operation)) _activeFetch = null;
            cancellationTask = operation.CancellationTask;
        }

        // The fetch owns its CTS until both HTTP work and cancellation callbacks finish.
        try { await cancellationTask.ConfigureAwait(false); }
        catch (Exception ex)
        {
            RuntimeLog.Log(LogTag, $"Cancellation error: {ex.Message}");
        }
        finally
        {
            operation.Cancellation.Dispose();
            operation.Completion.TrySetResult(result);
        }
    }

    private void CancelActiveFetchLocked()
    {
        if (_activeFetch == null) return;
        _activeFetch.CancellationTask = _activeFetch.Cancellation.CancelAsync();
        _activeFetch = null;
    }

    private async Task<LyricsResult?> FetchUncachedLyricsAsync(FetchKey key, CancellationToken token)
    {
        var candidates = GenerateSearchCandidates(key.Track, key.Artist);
        foreach (var (candTrack, candArtist) in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(candArtist))
            {
                var exact = await TryGetExactAsync(candTrack, candArtist, key.DurationSeconds, token).ConfigureAwait(false);
                if (exact is { Count: > 0 }) return new LyricsResult(exact, "LRCLIB");
            }

            var searched = await TrySearchAsync(candTrack, candArtist, key.DurationSeconds, token).ConfigureAwait(false);
            if (searched is { Count: > 0 }) return new LyricsResult(searched, "LRCLIB");
        }

        foreach (var (candTrack, candArtist) in candidates)
        {
            token.ThrowIfCancellationRequested();
            var aggregated = await TryLrcMuxAsync(candTrack, candArtist, key.DurationSeconds, token).ConfigureAwait(false);
            if (aggregated != null) return aggregated;
        }
        return null;
    }

    public static List<(string Track, string Artist)> GenerateSearchCandidates(string trackName, string artistName)
    {
        var candidates = new List<(string Track, string Artist)>();

        void AddCandidate(string t, string a)
        {
            string cleanT = CleanTitle(t);
            string cleanA = CleanArtist(a);
            if (string.IsNullOrWhiteSpace(cleanT)) return;

            // Strip browser/generic platform names from artist
            foreach (string platform in GenericPlatformNames)
            {
                if (!cleanA.Equals(platform, StringComparison.OrdinalIgnoreCase)) continue;
                cleanA = "";
                break;
            }

            foreach (var candidate in candidates)
            {
                if (candidate.Track.Equals(cleanT, StringComparison.OrdinalIgnoreCase) &&
                    candidate.Artist.Equals(cleanA, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            candidates.Add((cleanT, cleanA));
        }

        // 1. Raw inputs
        AddCandidate(trackName, artistName);

        // 2. Cleaned inputs
        string cTrack = CleanTitle(trackName);
        string cArtist = CleanArtist(artistName);
        AddCandidate(cTrack, cArtist);

        // 3. Decompose pipe '|' (common in YouTube music video titles: "Artist | Title" or "Artist - Nick | Title")
        DecomposePipes(cTrack, AddCandidate);

        // 4. Decompose standard dashes " - ", " – ", " — "
        DecomposeDashes(cTrack, AddCandidate);

        // 5. Track name only if artist is empty or generic
        if (!string.IsNullOrEmpty(cTrack))
        {
            AddCandidate(cTrack, "");
        }

        return candidates;
    }

    private static void DecomposePipes(string cTrack, Action<string, string> addCandidate)
    {
        if (!cTrack.Contains('|')) return;

        var parts = cTrack.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            addCandidate(parts[1], parts[0]);
            addCandidate(parts[0], parts[1]);
        }
        else if (parts.Length > 2)
        {
            addCandidate(parts[1], parts[0]);
            addCandidate(parts[0], parts[1]);
            addCandidate(string.Join(" ", parts.Skip(1)), parts[0]);
        }
    }

    private static void DecomposeDashes(string cTrack, Action<string, string> addCandidate)
    {
        foreach (var dash in Dashes)
        {
            if (!cTrack.Contains(dash)) continue;
            var parts = cTrack.Split(dash, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                addCandidate(parts[1], parts[0]);
                addCandidate(parts[0], parts[1]);
            }
        }
    }

    private async Task<List<LyricLine>?> TryGetExactAsync(string trackName, string artistName, int durationSeconds, CancellationToken token)
    {
        string url = $"/api/get?track_name={Uri.EscapeDataString(trackName)}" +
                     $"&artist_name={Uri.EscapeDataString(artistName)}&duration={durationSeconds}";

        RuntimeLog.Log(LogTag, $"Fetching (exact): {trackName} - {artistName} ({durationSeconds}s)");

        using var response = await _lrclibClient.GetAsync(url, token);
        if (!response.IsSuccessStatusCode)
        {
            RuntimeLog.Log(LogTag, $"Exact HTTP {(int)response.StatusCode} for '{trackName}'");
            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var lines = ExtractSyncedLines(doc.RootElement);
        if (lines is { Count: > 0 })
            RuntimeLog.Log(LogTag, $"Got {lines.Count} synced lines (exact) for '{trackName}'");
        return lines;
    }

    private async Task<List<LyricLine>?> TrySearchAsync(string trackName, string artistName, int durationSeconds, CancellationToken token)
    {
        string url = $"/api/search?track_name={Uri.EscapeDataString(trackName)}" +
                     $"&artist_name={Uri.EscapeDataString(artistName)}";

        RuntimeLog.Log(LogTag, $"Fetching (search): {trackName} - {artistName}");

        using var response = await _lrclibClient.GetAsync(url, token);
        if (!response.IsSuccessStatusCode)
        {
            RuntimeLog.Log(LogTag, $"Search HTTP {(int)response.StatusCode} for '{trackName}'");
            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        string targetTrackNorm = NormalizeForMatching(trackName);
        var (best, bestDelta, found) = FindBestCandidate(doc.RootElement, targetTrackNorm, durationSeconds);
        if (!found) return null;

        var lines = ExtractSyncedLines(best);
        if (lines is { Count: > 0 })
            RuntimeLog.Log(LogTag, $"Got {lines.Count} synced lines (search, Δ{bestDelta}s) for '{trackName}'");
        return lines;
    }

    private static (JsonElement Best, int BestDelta, bool Found) FindBestCandidate(JsonElement root, string targetTrackNorm, int durationSeconds)
    {
        JsonElement best = default;
        bool found = false;
        int bestDelta = int.MaxValue;

        foreach (var item in root.EnumerateArray())
        {
            if (!HasValidSyncedLyrics(item))
                continue;

            if (!IsTrackTitleMatch(item, targetTrackNorm))
                continue;

            int dur = item.TryGetProperty("duration", out var dp) && dp.ValueKind == JsonValueKind.Number
                ? (int)Math.Round(dp.GetDouble())
                : 0;
            int delta = dur > 0 ? Math.Abs(dur - durationSeconds) : 0;

            if (!found || delta < bestDelta)
            {
                best = item;
                bestDelta = delta;
                found = true;
            }
        }

        return (best, bestDelta, found);
    }

    private static bool HasValidSyncedLyrics(JsonElement item)
    {
        return item.TryGetProperty("syncedLyrics", out var sp) &&
               sp.ValueKind != JsonValueKind.Null &&
               !string.IsNullOrWhiteSpace(sp.GetString());
    }

    internal static bool IsTrackTitleMatch(JsonElement item, string targetTrackNorm)
    {
        if (string.IsNullOrEmpty(targetTrackNorm)) return true;

        string itemTrack = item.TryGetProperty("trackName", out var tp) ? tp.GetString() ?? "" : "";
        string itemTrackNorm = NormalizeForMatching(itemTrack);
        if (string.IsNullOrEmpty(itemTrackNorm)) return true;

        return itemTrackNorm.Equals(targetTrackNorm, StringComparison.OrdinalIgnoreCase) ||
               ContainsWholePhrase(itemTrackNorm.AsSpan(), targetTrackNorm.AsSpan()) ||
               ContainsWholePhrase(targetTrackNorm.AsSpan(), itemTrackNorm.AsSpan());
    }

    private static bool ContainsWholePhrase(ReadOnlySpan<char> text, ReadOnlySpan<char> phrase)
    {
        if (phrase.Length < 3) return false;
        int start = 0;
        while (start <= text.Length - phrase.Length)
        {
            int relativeIndex = text[start..].IndexOf(phrase, StringComparison.Ordinal);
            if (relativeIndex < 0) return false;
            int index = start + relativeIndex;
            int end = index + phrase.Length;
            if ((index == 0 || text[index - 1] == ' ') && (end == text.Length || text[end] == ' '))
                return true;
            start = index + 1;
        }
        return false;
    }

    internal static string NormalizeForMatching(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string decomposed = BracketedExtrasRegex().Replace(text, "").Normalize(NormalizationForm.FormD);
        char[]? rented = null;
        Span<char> buffer = decomposed.Length <= 512
            ? stackalloc char[decomposed.Length]
            : (rented = ArrayPool<char>.Shared.Rent(decomposed.Length)).AsSpan(0, decomposed.Length);
        try
        {
            int length = 0;
            bool needsRecomposition = false;
            foreach (char original in decomposed)
            {
                if (original > 127 && CharUnicodeInfo.GetUnicodeCategory(original) == UnicodeCategory.NonSpacingMark)
                    continue;
                char c = original is 'đ' or 'Đ' ? 'd' : original;
                buffer[length++] = c;
                needsRecomposition |= c > 127;
            }

            // Preserve NFC behavior for non-ASCII text; Latin titles can stay in the buffer.
            ReadOnlySpan<char> characters = buffer[..length];
            if (needsRecomposition)
                characters = new string(characters).Normalize(NormalizationForm.FormC);

            int written = 0;
            bool pendingSpace = false;
            foreach (char original in characters)
            {
                char c = original is >= 'A' and <= 'Z' ? (char)(original + ('a' - 'A')) : original;
                if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                {
                    if (pendingSpace) buffer[written++] = ' ';
                    buffer[written++] = c;
                    pendingSpace = false;
                }
                else
                {
                    pendingSpace = written > 0;
                }
            }
            return new string(buffer[..written]);
        }
        finally
        {
            if (rented != null) ArrayPool<char>.Shared.Return(rented);
        }
    }

    private async Task<LyricsResult?> TryLrcMuxAsync(
        string trackName,
        string artistName,
        int durationSeconds,
        CancellationToken token)
    {
        string url = $"/get?title={Uri.EscapeDataString(trackName)}" +
                     $"&artist={Uri.EscapeDataString(artistName)}" +
                     $"&duration={durationSeconds}" +
                     "&level=word&format=json&sources=%21lrclib";

        RuntimeLog.Log(LogTag, $"Fetching (lrc mux): {trackName} - {artistName}");

        using var response = await _lrcMuxClient.GetAsync(url, token);
        if (!response.IsSuccessStatusCode)
        {
            RuntimeLog.Log(LogTag, $"lrc mux HTTP {(int)response.StatusCode} for '{trackName}'");
            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var result = ParseLrcMuxResult(doc.RootElement);
        if (result is { Lines.Count: > 0 })
            RuntimeLog.Log(LogTag, $"Got {result.Lines.Count} synced lines from {result.Provider} for '{trackName}'");
        return result;
    }

    internal static LyricsResult? ParseLrcMuxResult(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        using var doc = JsonDocument.Parse(json);
        return ParseLrcMuxResult(doc.RootElement);
    }

    private static LyricsResult? ParseLrcMuxResult(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (!root.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
            return null;

        if (!IsValidSyncLevel(meta))
            return null;

        string provider = ResolveProviderName(meta);

        if (!root.TryGetProperty("lines", out var linesProp) || linesProp.ValueKind != JsonValueKind.Array)
            return null;

        var lines = ParseLrcMuxLines(linesProp);
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines.Count > 0 ? new LyricsResult(lines, provider) : null;
    }

    private static bool IsValidSyncLevel(JsonElement meta)
    {
        string syncLevel = meta.TryGetProperty("level", out var levelProp)
            ? levelProp.GetString() ?? ""
            : "";
        return syncLevel.Equals("word", StringComparison.OrdinalIgnoreCase) ||
               syncLevel.Equals("line", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveProviderName(JsonElement meta)
    {
        if (meta.TryGetProperty("source", out var source) &&
            source.ValueKind == JsonValueKind.Object &&
            source.TryGetProperty("name", out var nameProp) &&
            !string.IsNullOrWhiteSpace(nameProp.GetString()))
        {
            return $"{nameProp.GetString()!.Trim()} via lrc mux";
        }
        return "lrc mux";
    }

    private static List<LyricLine> ParseLrcMuxLines(JsonElement linesProp)
    {
        var lines = new List<LyricLine>();
        foreach (var item in linesProp.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("text", out var textProp) ||
                !item.TryGetProperty("start", out var startProp) ||
                startProp.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            string text = textProp.GetString()?.Trim() ?? "";
            if (text.Length == 0 ||
                !startProp.TryGetInt64(out long startMilliseconds) ||
                startMilliseconds < 0)
            {
                continue;
            }

            lines.Add(new LyricLine(TimeSpan.FromMilliseconds(startMilliseconds), text));
        }
        return lines;
    }

    private static List<LyricLine>? ExtractSyncedLines(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("syncedLyrics", out var syncedProp) || syncedProp.ValueKind == JsonValueKind.Null)
            return null;

        string syncedLyrics = syncedProp.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(syncedLyrics)) return null;

        var lines = ParseLrc(syncedLyrics);
        return lines.Count > 0 ? lines : null;
    }

    private static string CleanTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;

        // Drop bracketed/parenthesised extras like (Official Music Video), (Lyric Video), [MV], etc.
        string s = TitleExtrasRegex().Replace(title, "");

        // Remove YouTube visualizer/MV suffix patterns
        // Runtime regex preserves CurrentCulture casing (notably Turkish/Azeri I).
        s = Regex.Replace(s, VideoSuffixPattern, "", RegexOptions.IgnoreCase);

        // Strip standard remaster/live suffixes
        s = Regex.Replace(s, RecordingSuffixPattern, "", RegexOptions.IgnoreCase);

        s = s.Trim();
        return s.Length == 0 ? title.Trim() : s;
    }

    private static string CleanArtist(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return artist;

        // Use only the primary artist (before "feat.", "&", "," , "x").
        string s = artist;
        foreach (var sep in ArtistSeparators)
        {
            int idx = s.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (idx > 0) s = s[..idx];
        }
        s = s.Trim();
        return s.Length == 0 ? artist.Trim() : s;
    }

    public void Reset()
    {
        lock (_fetchLock) CancelActiveFetchLocked();
    }

    internal static List<LyricLine> ParseLrc(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var rawLine in lrc.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim();
            if (line.Length < 10 || line[0] != '[') continue;

            int closeBracket = line.IndexOf(']');
            if (closeBracket < 5) continue;

            ReadOnlySpan<char> timestamp = line[1..closeBracket];
            ReadOnlySpan<char> text = line[(closeBracket + 1)..].Trim();

            if (text.IsEmpty) continue;

            if (TryParseTimestamp(timestamp, out var time))
            {
                lines.Add(new LyricLine(time, new string(text)));
            }
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    private static bool TryParseTimestamp(ReadOnlySpan<char> ts, out TimeSpan result)
    {
        result = TimeSpan.Zero;

        int colonIdx = ts.IndexOf(':');
        int dotIdx = ts.IndexOf('.');
        if (colonIdx < 1 || dotIdx < colonIdx) return false;

        if (!int.TryParse(ts[..colonIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes))
            return false;
        if (!int.TryParse(ts[(colonIdx + 1)..dotIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
            return false;

        ReadOnlySpan<char> fracStr = ts[(dotIdx + 1)..];
        if (fracStr.Length > 3)
            fracStr = fracStr[..3];

        if (!int.TryParse(fracStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int frac))
            return false;

        int ms = fracStr.Length switch
        {
            1 => frac * 100,
            2 => frac * 10,
            _ => frac
        };

        result = new TimeSpan(0, 0, minutes, seconds, ms);
        return true;
    }

    public void Dispose()
    {
        lock (_fetchLock)
        {
            if (_disposed) return;
            _disposed = true;
            CancelActiveFetchLocked();
            _cache.Clear();
        }
    }
}

internal readonly record struct LyricLine(TimeSpan Time, string Text);

internal sealed record LyricsResult(List<LyricLine> Lines, string Provider);
