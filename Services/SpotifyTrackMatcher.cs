using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using static VNotch.Services.SpotifyJson;

namespace VNotch.Services;

internal static partial class SpotifyTrackMatcher
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotify_normalize_for_match(
        ReadOnlySpan<char> valPtr,
        int valLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotify_match_normalized_score(
        ReadOnlySpan<char> candPtr,
        int candLen,
        ReadOnlySpan<char> expPtr,
        int expLen,
        int exact,
        int contains,
        out int outScore);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotify_extract_track_id(
        ReadOnlySpan<char> valPtr,
        int valLen,
        Span<char> outBuf,
        int outCap,
        out int outLen,
        out byte outFound);

    [LibraryImport(NativeDll)]
    private static partial int vn_spotify_calculate_duration_score_delta(
        long candTicks,
        long expTicks,
        out int outDelta);

    internal static string? ParsePathfinderTrackId(
        string json,
        string expectedTrack,
        string expectedArtist)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            if (!TryGetProperty(document.RootElement, "data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(data, "searchV2", out var search) ||
                search.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(search, "tracksV2", out var tracks) ||
                tracks.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(tracks, "items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? bestId = null;
            int bestScore = int.MinValue;
            string normalizedTrack = NormalizeForMatch(expectedTrack);
            string normalizedArtist = NormalizeForMatch(expectedArtist);
            foreach (var result in items.EnumerateArray())
            {
                if (result.ValueKind != JsonValueKind.Object ||
                    !TryGetProperty(result, "item", out var item) ||
                    item.ValueKind != JsonValueKind.Object ||
                    !TryGetProperty(item, "data", out var track) ||
                    track.ValueKind != JsonValueKind.Object ||
                    !IsTrackUnionType(track))
                {
                    continue;
                }

                string? id = GetTrackId(track);
                int? score = ScorePathfinderTrack(track, id, normalizedTrack, normalizedArtist);
                if (score.HasValue && score.Value > bestScore)
                {
                    bestId = id;
                    bestScore = score.Value;
                }
            }

            return bestId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsTrackUnionType(JsonElement track)
    {
        string? typeName = GetDirectString(track, "__typename");
        return string.IsNullOrEmpty(typeName) || typeName.Equals("Track", StringComparison.OrdinalIgnoreCase);
    }

    private static int? ScorePathfinderTrack(
        JsonElement track,
        string? id,
        string expectedTrack,
        string expectedArtist)
    {
        string? title = GetDirectString(track, "name");
        int titleScore = MatchScore(title, expectedTrack, exact: 100, contains: 72);
        if (id == null || titleScore < 72)
            return null;

        int artistScore = 0;
        if (!string.IsNullOrWhiteSpace(expectedArtist))
        {
            artistScore = ScorePathfinderArtists(track, expectedArtist);
            if (artistScore == 0)
                return null;
        }

        return titleScore + artistScore;
    }

    private static int ScorePathfinderArtists(JsonElement track, string expectedArtist)
    {
        IReadOnlyList<string> artists = GetPathfinderArtistNames(track);
        int artistScore = 0;
        int combinedLength = Math.Max(0, artists.Count - 1);
        foreach (string artist in artists)
        {
            artistScore = Math.Max(artistScore, MatchNormalizedScore(artist, expectedArtist, exact: 35, contains: 22));
            if (artistScore == 35) return artistScore;
            combinedLength += artist.Length;
        }

        if (artists.Count <= 1) return artistScore;

        char[]? rented = combinedLength <= 512 ? null : ArrayPool<char>.Shared.Rent(combinedLength);
        Span<char> combined = rented is null
            ? stackalloc char[combinedLength]
            : rented.AsSpan(0, combinedLength);
        try
        {
            int offset = 0;
            foreach (string artist in artists)
            {
                if (offset > 0) combined[offset++] = ' ';
                artist.AsSpan().CopyTo(combined[offset..]);
                offset += artist.Length;
            }
            return Math.Max(artistScore, MatchNormalizedScore(combined, expectedArtist, exact: 35, contains: 22));
        }
        finally
        {
            if (rented != null) ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static IReadOnlyList<string> GetPathfinderArtistNames(JsonElement track)
    {
        var names = new List<string>();
        if (!TryGetProperty(track, "artists", out var artists) ||
            artists.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(artists, "items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return names;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(item, "profile", out var profile) ||
                profile.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? name = GetDirectString(profile, "name");
            string normalized = NormalizeForMatch(name);
            if (normalized.Length > 0)
                names.Add(normalized);
        }

        return names;
    }

    internal static string? ParseTrackId(
        string json,
        string expectedTrack,
        string expectedArtist,
        TimeSpan expectedDuration)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var candidates = new List<TrackCandidate>();
            CollectTrackCandidates(document.RootElement, candidates, depth: 0);

            TrackCandidate? best = null;
            int bestScore = int.MinValue;
            string normalizedTrack = NormalizeForMatch(expectedTrack);
            string normalizedArtist = NormalizeForMatch(expectedArtist);
            foreach (var candidate in candidates)
            {
                int? score = ScoreTrackCandidate(candidate, normalizedTrack, normalizedArtist, expectedDuration);
                if (score.HasValue && score.Value > bestScore)
                {
                    best = candidate;
                    bestScore = score.Value;
                }
            }

            return best?.Id;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? ScoreTrackCandidate(
        TrackCandidate candidate,
        string expectedTrack,
        string expectedArtist,
        TimeSpan expectedDuration)
    {
        int titleScore = MatchScore(candidate.Title, expectedTrack, exact: 100, contains: 72);
        if (titleScore < 72)
            return null;

        int score = titleScore;
        if (!string.IsNullOrWhiteSpace(expectedArtist))
            score += MatchScore(candidate.Artist, expectedArtist, exact: 35, contains: 22);

        if (candidate.Duration > TimeSpan.Zero && expectedDuration > TimeSpan.Zero)
            score += CalculateDurationScoreDelta(candidate.Duration, expectedDuration);

        return score;
    }

    private static int CalculateDurationScoreDelta(TimeSpan candidateDuration, TimeSpan expectedDuration)
    {
        vn_spotify_calculate_duration_score_delta(candidateDuration.Ticks, expectedDuration.Ticks, out int delta);
        return delta;
    }

    private static void CollectTrackCandidates(JsonElement element, List<TrackCandidate> candidates, int depth)
    {
        if (depth > 24)
            return;

        if (element.ValueKind == JsonValueKind.Object)
        {
            string? id = GetTrackId(element);
            string? title = GetDirectString(element, "trackName", "track_name", "title", "name");
            if (id != null && !string.IsNullOrWhiteSpace(title))
            {
                candidates.Add(new TrackCandidate(
                    id,
                    title,
                    GetArtistName(element) ?? "",
                    GetDuration(element)));
            }

            foreach (var property in element.EnumerateObject())
                CollectTrackCandidates(property.Value, candidates, depth + 1);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectTrackCandidates(item, candidates, depth + 1);
        }
    }

    private static string? GetTrackId(JsonElement element)
    {
        foreach (string propertyName in new[] { "trackUri", "track_uri", "uri", "url", "externalUrl" })
        {
            string? parsed = ExtractTrackId(GetDirectString(element, propertyName));
            if (parsed != null)
                return parsed;
        }

        foreach (string propertyName in new[]
                 {
                     "track_spotify_id", "trackId", "track_id", "spotifyId", "spotify_id", "id"
                 })
        {
            string? value = GetDirectString(element, propertyName);
            string? parsed = ExtractTrackId(value);
            if (parsed != null && parsed.Length == value?.Length)
                return parsed;
        }

        return null;
    }

    private static string? ExtractTrackId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        ReadOnlySpan<char> span = value.AsSpan();
        Span<char> buf = stackalloc char[32];
        vn_spotify_extract_track_id(span, span.Length, buf, buf.Length, out int len, out byte found);
        return found != 0 && len > 0 ? new string(buf[..len]) : null;
    }

    private static string? GetArtistName(JsonElement element)
    {
        string? direct = GetDirectString(element, "artistName", "artist_name", "author", "subtitle");
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        if (TryGetProperty(element, "artist", out var artist))
        {
            string? artistName = ExtractArtistNameFromProperty(artist);
            if (!string.IsNullOrWhiteSpace(artistName))
                return artistName;
        }

        if (TryGetProperty(element, "artists", out var artists))
            return ExtractArtistNameFromArtistsProperty(artists);

        return null;
    }

    private static string? ExtractArtistNameFromProperty(JsonElement artist)
    {
        if (artist.ValueKind == JsonValueKind.String)
            return artist.GetString();
        if (artist.ValueKind == JsonValueKind.Object)
            return GetDirectString(artist, "name", "artistName", "artist_name");
        return null;
    }

    private static string? ExtractArtistNameFromArtistsProperty(JsonElement artists)
    {
        if (artists.ValueKind == JsonValueKind.Object)
            return FindStringProperty(artists, "name", depth: 0);

        if (artists.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in artists.EnumerateArray())
            {
                string? name = ExtractArtistNameFromProperty(item);
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }

        return null;
    }

    private static TimeSpan GetDuration(JsonElement element)
    {
        foreach (string name in new[] { "durationMs", "duration_ms", "durationMillis", "duration_millis" })
        {
            if (TryGetNumber(element, name, out double milliseconds) && milliseconds > 0)
                return TimeSpan.FromMilliseconds(milliseconds);
        }

        foreach (string name in new[] { "duration", "durationSeconds", "duration_seconds", "track_length" })
        {
            if (TryGetNumber(element, name, out double seconds) && seconds > 0)
                return seconds > 10_000 ? TimeSpan.FromMilliseconds(seconds) : TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.Zero;
    }

    private static bool TryGetNumber(JsonElement element, string name, out double value)
    {
        value = 0;
        if (!TryGetProperty(element, name, out var property))
            return false;
        if (property.ValueKind == JsonValueKind.Number)
            return property.TryGetDouble(out value);
        return property.ValueKind == JsonValueKind.String &&
               double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static int MatchScore(string? candidate, string normalizedExpected, int exact, int contains)
        => MatchNormalizedScore(NormalizeForMatch(candidate), normalizedExpected, exact, contains);

    private static int MatchNormalizedScore(ReadOnlySpan<char> normalizedCandidate, ReadOnlySpan<char> normalizedExpected, int exact, int contains)
    {
        if (normalizedCandidate.Length == 0 || normalizedExpected.Length == 0)
            return 0;

        vn_spotify_match_normalized_score(
            normalizedCandidate,
            normalizedCandidate.Length,
            normalizedExpected,
            normalizedExpected.Length,
            exact,
            contains,
            out int score);
        return score;
    }

    internal static string NormalizeForMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        ReadOnlySpan<char> span = value.AsSpan();
        int cap = span.Length * 2 + 16;
        Span<char> buf = cap <= 512 ? stackalloc char[cap] : new char[cap];
        int rc = vn_spotify_normalize_for_match(span, span.Length, buf, buf.Length, out int len);
        if (rc == -2 && len > buf.Length) { char[] larger = new char[len]; rc = vn_spotify_normalize_for_match(span, span.Length, larger, larger.Length, out len); return rc == 0 && len > 0 ? new string(larger, 0, len) : ""; }
        if (rc != 0) len = 0;
        return len > 0 ? new string(buf[..len]) : "";
    }

    private sealed record TrackCandidate(string Id, string Title, string Artist, TimeSpan Duration);
}
