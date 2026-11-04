#pragma warning disable S1075 // Public lyrics API endpoints
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services;

internal sealed partial class LyricsService : IDisposable
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_lyrics_try_parse_lrc_line(
        ReadOnlySpan<char> rawLine,
        int rawLineLen,
        out long outTicks,
        out int outTextStart,
        out int outTextLength,
        out byte outOk);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_lyrics_contains_whole_phrase(
        ReadOnlySpan<char> text,
        int textLen,
        ReadOnlySpan<char> phrase,
        int phraseLen,
        out byte outContains);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_lyrics_is_generic_platform_name(
        ReadOnlySpan<char> artist,
        int artistLen,
        out byte outIsGeneric);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_lyrics_is_valid_sync_level(
        ReadOnlySpan<char> syncLevel,
        int syncLevelLen,
        out byte outIsValid);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_lyrics_clean_artist_span(
        ReadOnlySpan<char> artist,
        int artistLen,
        out int outStart,
        out int outLength);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_lyrics_normalize_for_matching(
        ReadOnlySpan<char> text_ptr,
        int text_len,
        Span<char> out_buf,
        int out_cap,
        out int out_written);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_lyrics_strip_title_extras(
        ReadOnlySpan<char> title_ptr,
        int title_len,
        byte is_turkic_culture,
        Span<char> out_buf,
        int out_cap,
        out int out_written);

    private const string LogTag = "LYRICS";
    private static readonly string[] Dashes = { " - ", " – ", " — ", " // " };
    private const int MaxLyricsResponseBytes = 2 * 1024 * 1024;

    private static readonly HttpClient _lrclibHttp = CreateClient("https://lrclib.net", 2500);
    private static readonly HttpClient _lrcMuxHttp = CreateClient("https://api.lrcmux.dev", 2000);
    private long _lrcMuxRetryUntil;

    private static HttpClient CreateClient(string address, int timeoutMs)
    {
        var transport = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromMilliseconds(1500),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        var client = new HttpClient(NetworkPrivacy.Handler(NetworkFeature.Lyrics, transport))
        { BaseAddress = new Uri(address), Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppVersion.UserAgent);
        return client;
    }

    private readonly HttpClient _lrclibClient;
    private readonly HttpClient _lrcMuxClient;
    private readonly HttpClient _netEaseClient;
    private readonly HttpClient _kugouClient;
    private readonly HttpClient _qqMusicClient;
    private readonly HttpClient _amllClient;
    private readonly SpotifyLocalMetadataReader? _spotifyLocalMetadata;

    public LyricsService() : this(_lrclibHttp, _lrcMuxHttp,
        spotifyLocalMetadata: new SpotifyLocalMetadataReader(includeTrackId: true))
    { }

    internal LyricsService(HttpClient lrclibClient, HttpClient lrcMuxClient, int cacheCapacity = 64, HttpClient? netEaseClient = null,
        HttpClient? amllClient = null, SpotifyLocalMetadataReader? spotifyLocalMetadata = null,
        HttpClient? kugouClient = null, HttpClient? qqMusicClient = null)
    {
        if (cacheCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(cacheCapacity));
        _lrclibClient = lrclibClient;
        _lrcMuxClient = lrcMuxClient;
        _netEaseClient = netEaseClient ?? NetEaseHttp;
        _kugouClient = kugouClient ?? KugouHttp;
        _qqMusicClient = qqMusicClient ?? QqMusicHttp;
        _amllClient = amllClient ?? AmllHttp;
        _spotifyLocalMetadata = spotifyLocalMetadata;
        _cacheCapacity = cacheCapacity;
    }

    private readonly object _fetchLock = new();
    private readonly int _cacheCapacity;
    private readonly Dictionary<FetchKey, (LyricsResult Result, long LastAccess)> _cache = new();
    private long _cacheAccess;
    private FetchOperation? _activeFetch;
    private bool _disposed;

    private readonly record struct FetchKey(string Track, string Artist, int DurationSeconds, string Providers, string SpotifyTrackId, bool FindWordTiming);

    private sealed class FetchOperation(FetchKey key)
    {
        public FetchKey Key { get; } = key;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<LyricsResult?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CancellationTask { get; set; } = Task.CompletedTask;
        public Action<LyricsResult>? OnLyricsAvailable { get; set; }
        public LyricsResult? AvailableResult { get; set; }
    }

    public Task<LyricsResult?> FetchSyncedLyricsAsync(string trackName, string artistName, int durationSeconds, string? providerOrder = null,
        string? spotifyTrackId = null, Action<LyricsResult>? onLyricsAvailable = null)
    {
        string order = providerOrder == null ? $"{LyricsProviderOrder.Amll},{LyricsProviderOrder.Legacy}" : string.Join(',', LyricsProviderOrder.Normalize(providerOrder));
        var key = new FetchKey(trackName, artistName, durationSeconds, order, NormalizeSpotifyTrackId(spotifyTrackId) ?? "", providerOrder != null);
        FetchOperation operation;
        lock (_fetchLock)
        {
            if (_disposed) return Task.FromResult<LyricsResult?>(null);
            if (_activeFetch?.Key == key)
            {
                _activeFetch.OnLyricsAvailable += onLyricsAvailable;
                // A collapsed prefetch may already have found line lyrics while
                // searching for words. Replay them when the lyrics view joins.
                if (_activeFetch.AvailableResult is { } available)
                    NotifyLyricsAvailable(onLyricsAvailable, available);
                return _activeFetch.Completion.Task;
            }

            CancelActiveFetchLocked();
            if (_cache.TryGetValue(key, out var cached))
            {
                _cache[key] = (cached.Result, ++_cacheAccess);
                return Task.FromResult<LyricsResult?>(cached.Result);
            }

            operation = new FetchOperation(key) { OnLyricsAvailable = onLyricsAvailable };
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
            result = await FetchUncachedLyricsAsync(operation.Key, operation.Cancellation.Token,
                available => PublishAvailableLyrics(operation, available)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* Canceled */ }
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
                if (_cache.Count >= _cacheCapacity && !_cache.ContainsKey(operation.Key))
                {
                    FetchKey oldestKey = default;
                    long oldestAccess = long.MaxValue;
                    bool hasOldest = false;
                    foreach (var kv in _cache)
                    {
                        if (!hasOldest || kv.Value.LastAccess < oldestAccess)
                        {
                            oldestKey = kv.Key;
                            oldestAccess = kv.Value.LastAccess;
                            hasOldest = true;
                        }
                    }
                    if (hasOldest) _cache.Remove(oldestKey);
                }
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

    private void PublishAvailableLyrics(FetchOperation operation, LyricsResult result)
    {
        Action<LyricsResult>? callback;
        lock (_fetchLock)
        {
            if (_disposed || _activeFetch != operation || operation.Cancellation.IsCancellationRequested) return;
            operation.AvailableResult = result;
            callback = operation.OnLyricsAvailable;
        }
        NotifyLyricsAvailable(callback, result);
    }

    private static void NotifyLyricsAvailable(Action<LyricsResult>? callback, LyricsResult result)
    {
        if (callback == null) return;
        foreach (Action<LyricsResult> listener in callback.GetInvocationList())
        {
            try { listener(result); }
            catch (Exception ex) { RuntimeLog.Warn(LogTag, $"Lyrics presentation callback failed: {ex.Message}"); }
        }
    }

    private async Task<LyricsResult?> FetchUncachedLyricsAsync(FetchKey key, CancellationToken token,
        Action<LyricsResult> onLyricsAvailable)
    {
        LyricsResult? lineFallback = null;
        foreach (string provider in key.Providers.Split(','))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var result = await FetchProviderLyricsAsync(key, provider, token).ConfigureAwait(false);
                if (result is not { Lines.Count: > 0 }) continue;
                if (!key.FindWordTiming || result.Lines.Any(line => line.Words is { Length: > 0 })) return result;
                if (lineFallback == null)
                {
                    lineFallback = result;
                    onLyricsAvailable(result);
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested &&
                ex is HttpRequestException or OperationCanceledException or System.IO.IOException or JsonException or System.Xml.XmlException or InvalidOperationException or KeyNotFoundException)
            { RuntimeLog.Warn(LogTag, $"{provider} unavailable; trying the next lyrics provider."); }
        }
        // Exhaustion ends the word search and keeps the first usable line source.
        return lineFallback;
    }

    private async Task<LyricsResult?> FetchProviderLyricsAsync(FetchKey key, string provider, CancellationToken token)
    {
        if (provider == LyricsProviderOrder.Amll)
        {
            string? id = key.SpotifyTrackId;
            if (string.IsNullOrEmpty(id) && _spotifyLocalMetadata != null)
                id = await _spotifyLocalMetadata.TryGetTrackIdAsync(key.Track, key.Artist, token).ConfigureAwait(false);
            return await TryAmllAsync(id, token).ConfigureAwait(false);
        }
        if (provider == "NetEase")
            return await TryNetEaseAsync(key.Track, key.Artist, key.DurationSeconds, token).ConfigureAwait(false);
        if (provider == "Kugou")
            return await TryKugouAsync(key.Track, key.Artist, key.DurationSeconds, token).ConfigureAwait(false);
        if (provider == "QQ Music")
            return await TryQqMusicAsync(key.Track, key.Artist, key.DurationSeconds, token).ConfigureAwait(false);
        foreach (var (track, artist) in GenerateSearchCandidates(key.Track, key.Artist))
        {
            token.ThrowIfCancellationRequested();
            if (provider == "lrc mux")
            {
                var result = await TryLrcMuxAsync(track, artist, key.DurationSeconds, token).ConfigureAwait(false);
                if (result != null) return result;
                continue;
            }
            if (provider != "LRCLIB") return null;
            if (!string.IsNullOrEmpty(artist))
            {
                var exact = await TryGetExactAsync(track, artist, key.DurationSeconds, token).ConfigureAwait(false);
                if (exact is { Count: > 0 }) return new LyricsResult(exact, "LRCLIB");
            }
            var searched = await TrySearchAsync(track, artist, key.DurationSeconds, token).ConfigureAwait(false);
            if (searched is { Count: > 0 }) return new LyricsResult(searched, "LRCLIB");
        }
        return null;
    }

    private static void MarkCooldown(ref long retryUntilField, TimeSpan duration) =>
        Interlocked.Exchange(ref retryUntilField, DateTimeOffset.UtcNow.Add(duration).UtcTicks);

    public static List<(string Track, string Artist)> GenerateSearchCandidates(string trackName, string artistName)
    {
        var candidates = new List<(string Track, string Artist)>();

        void AddCandidate(string t, string a)
        {
            string cleanT = CleanTitle(t);
            string cleanA = CleanArtist(a);
            if (string.IsNullOrWhiteSpace(cleanT)) return;

            // Strip browser/generic platform names from artist
            if (vn_lyrics_is_generic_platform_name(cleanA.AsSpan(), cleanA.Length, out byte isGeneric) == 0 && isGeneric != 0)
            {
                cleanA = "";
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

        // 4. Decompose standard dashes " - ", " – ", " — " on raw track before suffix stripping
        DecomposeDashes(trackName, AddCandidate);

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
            addCandidate(string.Join(' ', parts, 1, parts.Length - 1), parts[0]);
        }
    }

    private static void DecomposeDashes(string rawTrack, Action<string, string> addCandidate)
    {
        foreach (var dash in Dashes)
        {
            int idx = rawTrack.IndexOf(dash, StringComparison.Ordinal);
            if (idx <= 0) continue;
            string left = CleanTitle(rawTrack[..idx]);
            string right = CleanTitle(rawTrack[(idx + dash.Length)..]);
            if (!string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right))
            {
                addCandidate(right, left);
                addCandidate(left, right);
            }
        }
    }

    private async Task<List<LyricLine>?> TryGetExactAsync(string trackName, string artistName, int durationSeconds, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_lrclibClient.Timeout);
        token = timeout.Token;
        string url = $"/api/get?track_name={Uri.EscapeDataString(trackName)}" +
                     $"&artist_name={Uri.EscapeDataString(artistName)}&duration={durationSeconds}";

        RuntimeLog.Log(LogTag, $"Fetching (exact): {trackName} - {artistName} ({durationSeconds}s)");

        using var response = await _lrclibClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            RuntimeLog.Log(LogTag, $"Exact HTTP {(int)response.StatusCode} for '{trackName}'");
            return null;
        }

        using var doc = await ReadBoundedJsonAsync(response.Content, token);
        if (doc == null) return null;
        var lines = ExtractSyncedLines(doc.RootElement);
        if (lines is { Count: > 0 })
            RuntimeLog.Log(LogTag, $"Got {lines.Count} synced lines (exact) for '{trackName}'");
        return lines;
    }

    private async Task<List<LyricLine>?> TrySearchAsync(string trackName, string artistName, int durationSeconds, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_lrclibClient.Timeout);
        token = timeout.Token;
        string url = $"/api/search?track_name={Uri.EscapeDataString(trackName)}" +
                     $"&artist_name={Uri.EscapeDataString(artistName)}";

        RuntimeLog.Log(LogTag, $"Fetching (search): {trackName} - {artistName}");

        using var response = await _lrclibClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            RuntimeLog.Log(LogTag, $"Search HTTP {(int)response.StatusCode} for '{trackName}'");
            return null;
        }

        using var doc = await ReadBoundedJsonAsync(response.Content, token);
        if (doc == null) return null;
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
            int delta = (dur > 0 && durationSeconds > 0) ? Math.Abs(dur - durationSeconds) : int.MaxValue / 2;

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
        return vn_lyrics_contains_whole_phrase(text, text.Length, phrase, phrase.Length, out byte contains) == 0 && contains != 0;
    }

    internal static string NormalizeForMatching(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        int cap = Math.Max(text.Length, 64);
        char[]? rented = cap <= 512 ? null : ArrayPool<char>.Shared.Rent(cap);
        Span<char> buffer = rented is null ? stackalloc char[cap] : rented.AsSpan(0, cap);
        try
        {
            int status = vn_lyrics_normalize_for_matching(text.AsSpan(), text.Length, buffer, buffer.Length, out int written);
            if (status == -2 && written > buffer.Length)
            {
                if (rented != null) ArrayPool<char>.Shared.Return(rented);
                rented = ArrayPool<char>.Shared.Rent(written);
                buffer = rented.AsSpan(0, written);
                status = vn_lyrics_normalize_for_matching(text.AsSpan(), text.Length, buffer, buffer.Length, out written);
            }
            return status == 0 && written > 0 && written <= buffer.Length
                ? new string(buffer[..written])
                : "";
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
        if (DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _lrcMuxRetryUntil)) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_lrcMuxClient.Timeout);
        string url = $"/get?title={Uri.EscapeDataString(trackName)}" +
                     $"&artist={Uri.EscapeDataString(artistName)}" +
                     $"&duration={durationSeconds}" +
                     "&level=word&format=json&sources=%21lrclib";

        RuntimeLog.Log(LogTag, $"Fetching (lrc mux): {trackName} - {artistName}");

        try
        {
            using var response = await _lrcMuxClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                MarkCooldown(ref _lrcMuxRetryUntil, TimeSpan.FromMinutes(2));
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                RuntimeLog.Log(LogTag, $"lrc mux HTTP {(int)response.StatusCode} for '{trackName}'");
                return null;
            }

            using var doc = await ReadBoundedJsonAsync(response.Content, timeout.Token);
            if (doc == null) return null;
            var result = ParseLrcMuxResult(doc.RootElement);
            if (result is { Lines.Count: > 0 })
                RuntimeLog.Log(LogTag, $"Got {result.Lines.Count} synced lines from {result.Provider} for '{trackName}'");
            return result;
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
        {
            MarkCooldown(ref _lrcMuxRetryUntil, TimeSpan.FromMinutes(2));
            throw;
        }
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

        var lines = LyricTiming.ParseTimedLines(linesProp);
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines.Count > 0 ? new LyricsResult(lines, provider) : null;
    }

    private static bool IsValidSyncLevel(JsonElement meta)
    {
        string syncLevel = meta.TryGetProperty("level", out var levelProp)
            ? levelProp.GetString() ?? ""
            : "";
        return vn_lyrics_is_valid_sync_level(syncLevel.AsSpan(), syncLevel.Length, out byte valid) == 0 && valid != 0;
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

        int cap = title.Length;
        char[]? rented = cap <= 512 ? null : ArrayPool<char>.Shared.Rent(cap);
        Span<char> buffer = rented is null ? stackalloc char[cap] : rented.AsSpan(0, cap);
        try
        {
            byte isTurkic = System.Globalization.CultureInfo.CurrentCulture.TwoLetterISOLanguageName is "tr" or "az" ? (byte)1 : (byte)0;
            int status = vn_lyrics_strip_title_extras(title.AsSpan(), title.Length, isTurkic, buffer, buffer.Length, out int written);
            if (status == 0 && written > 0 && written <= buffer.Length)
            {
                ReadOnlySpan<char> slice = buffer[..written];
                return slice.SequenceEqual(title.AsSpan()) ? title : new string(slice);
            }
        }
        finally
        {
            if (rented != null) ArrayPool<char>.Shared.Return(rented);
        }

        return title.Trim();
    }

    private static string CleanArtist(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return artist;

        if (vn_lyrics_clean_artist_span(artist.AsSpan(), artist.Length, out int start, out int length) == 0 &&
            start >= 0 && start + length <= artist.Length)
        {
            return artist.Substring(start, length);
        }
        return artist.Trim();
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
            if (vn_lyrics_try_parse_lrc_line(rawLine, rawLine.Length, out long ticks, out int textStart, out int textLength, out byte ok) == 0 &&
                ok != 0 && textStart >= 0 && textLength <= 4096 && textStart + textLength <= rawLine.Length)
            {
                lines.Add(LyricTiming.ParseEnhanced(TimeSpan.FromTicks(ticks), new string(rawLine.Slice(textStart, textLength))));
            }
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    private static async Task<JsonDocument?> ReadBoundedJsonAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength is > MaxLyricsResponseBytes)
            return null;
        await using var stream = await content.ReadAsStreamAsync(token);
        using var ms = new System.IO.MemoryStream();
        byte[] buf = new byte[8192];
        int total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buf, token);
            if (read <= 0) break;
            total += read;
            if (total > MaxLyricsResponseBytes) return null;
            await ms.WriteAsync(buf.AsMemory(0, read), token).ConfigureAwait(false);
        }
        ms.Position = 0;
        return await JsonDocument.ParseAsync(ms, cancellationToken: token);
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

internal readonly record struct LyricLine(TimeSpan Time, string Text, LyricWord[]? Words = null);

internal sealed record LyricsResult(List<LyricLine> Lines, string Provider);
