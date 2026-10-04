using System.Globalization;
using System.Text;
using System.Text.Json;
using static VNotch.Services.SpotifyJson;

namespace VNotch.Services;

internal static class SpotifyTrackMatcher
{
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
                int? score = ScorePathfinderTrack(track, id, expectedTrack, expectedArtist);
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
        foreach (string artist in artists)
        {
            artistScore = Math.Max(artistScore, MatchScore(artist, expectedArtist, exact: 35, contains: 22));
        }

        if (artists.Count > 1)
        {
            artistScore = Math.Max(
                artistScore,
                MatchScore(string.Join(" ", artists), expectedArtist, exact: 35, contains: 22));
        }

        return artistScore;
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
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
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
            foreach (var candidate in candidates)
            {
                int? score = ScoreTrackCandidate(candidate, expectedTrack, expectedArtist, expectedDuration);
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
        double delta = Math.Abs((candidateDuration - expectedDuration).TotalSeconds);
        if (delta <= 4)
            return 12;
        if (delta <= 12)
            return 5;
        if (delta > 45)
            return -15;
        return 0;
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
            if (IsSpotifyId(value))
                return value;
        }

        return null;
    }

    private static string? ExtractTrackId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        const string uriPrefix = "spotify:track:";
        int uriIndex = value.IndexOf(uriPrefix, StringComparison.OrdinalIgnoreCase);
        if (uriIndex >= 0)
        {
            string possibleId = value[(uriIndex + uriPrefix.Length)..].Split('?', '/', '&')[0];
            if (IsSpotifyId(possibleId))
                return possibleId;
        }

        const string pathPrefix = "/track/";
        int pathIndex = value.IndexOf(pathPrefix, StringComparison.OrdinalIgnoreCase);
        if (pathIndex >= 0)
        {
            string possibleId = value[(pathIndex + pathPrefix.Length)..].Split('?', '/', '&')[0];
            if (IsSpotifyId(possibleId))
                return possibleId;
        }

        return IsSpotifyId(value) ? value : null;
    }

    private static bool IsSpotifyId(string? value) =>
        value is { Length: 22 } && value.All(ch =>
            ch is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z');

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

    private static int MatchScore(string? candidate, string expected, int exact, int contains)
    {
        string normalizedCandidate = NormalizeForMatch(candidate);
        string normalizedExpected = NormalizeForMatch(expected);
        if (normalizedCandidate.Length == 0 || normalizedExpected.Length == 0)
            return 0;
        if (normalizedCandidate == normalizedExpected)
            return exact;
        if (normalizedCandidate.Contains(normalizedExpected, StringComparison.Ordinal) ||
            normalizedExpected.Contains(normalizedCandidate, StringComparison.Ordinal))
            return contains;
        return 0;
    }

    internal static string NormalizeForMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        string decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        bool pendingSpace = false;
        foreach (char ch in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && builder.Length > 0)
                    builder.Append(' ');
                builder.Append(char.ToLowerInvariant(ch));
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }
        return builder.ToString();
    }

    private sealed record TrackCandidate(string Id, string Title, string Artist, TimeSpan Duration);
}
