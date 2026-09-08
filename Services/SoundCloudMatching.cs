using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VNotch.Services;

internal static class SoundCloudMatching
{
    private static readonly HashSet<string> ReservedUsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "search", "charts", "discover", "stream", "you", "terms", "privacy", "mobile", "upload", "signin",
        "login", "settings", "jobs", "blog", "developers", "pages", "playlists", "stations", "likes", "messages",
        "pro", "for-artists", "popular", "tracks", "explore", "featured"
    };

    private static readonly HashSet<string> ReservedTrackSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        "sets", "tracks", "likes", "reposts", "spotlight", "albums", "following", "followers"
    };

    public static string? ExtractTrackUrl(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        var match = Regex.Match(
            rawText,
            @"https?://(?:www\.)?soundcloud\.com/(?<user>[^/\s?#]+)/(?<slug>[^/\s?#]+)",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return null;
        }

        return $"https://soundcloud.com/{match.Groups["user"].Value}/{match.Groups["slug"].Value}";
    }

    public static string SanitizeSearchText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value.Replace('\u2013', '-').Replace('\u2014', '-').Trim();
        normalized = normalized.Trim('[', ']', '(', ')', '"', '\'');
        normalized = Regex.Replace(normalized, @"\s+", " ");
        return normalized.Trim();
    }

    public static List<(string Url, int Score)> ExtractTrackUrlsFromSearchHtml(string html, string title, string artist)
    {
        var result = new List<(string Url, int Score)>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return result;
        }

        string normalizedTitle = PlatformDetector.NormalizeForLooseMatch(title.ToLowerInvariant());
        string normalizedArtist = PlatformDetector.NormalizeForLooseMatch((artist ?? "").ToLowerInvariant());
        var scoreMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var pathMatches = Regex.Matches(html, @"href=""/(?<path>[^""?#]+)", RegexOptions.IgnoreCase);
        foreach (Match match in pathMatches)
        {
            string path = match.Groups["path"].Value.Replace("\\/", "/").Trim('/');
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 2)
            {
                continue;
            }

            AddCandidate(scoreMap, segments[0], segments[1], normalizedTitle, normalizedArtist);
        }

        var absoluteMatches = Regex.Matches(
            html,
            @"https?://(?:www\.)?soundcloud\.com/(?<user>[^/\s""'?#]+)/(?<slug>[^/\s""'?#]+)",
            RegexOptions.IgnoreCase);
        foreach (var groups in absoluteMatches.Select(match => match.Groups))
        {
            AddCandidate(scoreMap, groups["user"].Value, groups["slug"].Value, normalizedTitle, normalizedArtist);
        }

        var sorted = new List<KeyValuePair<string, int>>(scoreMap);
        sorted.Sort((a, b) => b.Value.CompareTo(a.Value));

        foreach (var item in sorted)
        {
            result.Add((item.Key, item.Value));
        }

        return result;
    }

    private static void AddCandidate(
        Dictionary<string, int> scoreMap,
        string user,
        string slug,
        string normalizedTitle,
        string normalizedArtist)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(slug))
        {
            return;
        }

        if (user.Contains('.') || slug.Length < 2)
        {
            return;
        }

        if (ReservedUsers.Contains(user) || ReservedTrackSlugs.Contains(slug))
        {
            return;
        }

        string url = $"https://soundcloud.com/{user}/{slug}";
        int score = ScoreCandidate(user, slug, normalizedTitle, normalizedArtist);
        if (scoreMap.TryGetValue(url, out int existing))
        {
            if (score > existing)
            {
                scoreMap[url] = score;
            }
        }
        else
        {
            scoreMap[url] = score;
        }
    }

    public static int ScoreCandidate(string user, string slug, string normalizedTitle, string normalizedArtist)
    {
        string normalizedSlug = PlatformDetector.NormalizeForLooseMatch(slug.ToLowerInvariant());
        string normalizedUser = PlatformDetector.NormalizeForLooseMatch(user.ToLowerInvariant());

        return ScoreTitleMatch(normalizedTitle, normalizedSlug) + ScoreArtistMatch(normalizedArtist, normalizedUser);
    }

    private static int ScoreTitleMatch(string normalizedTitle, string normalizedSlug)
    {
        if (string.IsNullOrEmpty(normalizedTitle)) return 0;

        if (normalizedSlug.Contains(normalizedTitle, StringComparison.Ordinal) ||
            normalizedTitle.Contains(normalizedSlug, StringComparison.Ordinal))
        {
            return 5;
        }

        int score = 0;
        var titleTokens = normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in titleTokens)
        {
            if (token.Length >= 3 && normalizedSlug.Contains(token, StringComparison.Ordinal))
            {
                score += 1;
            }
        }

        return score;
    }

    private static int ScoreArtistMatch(string normalizedArtist, string normalizedUser)
    {
        if (string.IsNullOrEmpty(normalizedArtist) ||
            normalizedArtist == "soundcloud" ||
            normalizedArtist == "browser")
        {
            return 0;
        }

        if (normalizedUser.Contains(normalizedArtist, StringComparison.Ordinal) ||
            normalizedArtist.Contains(normalizedUser, StringComparison.Ordinal))
        {
            return 3;
        }

        return 0;
    }

    public static bool IsOEmbedMatch(string expectedTitle, string expectedArtist, string? candidateTitle, string? candidateAuthor, int candidateScore, bool strictMode = false)
    {
        string normalizedExpectedTitle = PlatformDetector.NormalizeForLooseMatch(expectedTitle.ToLowerInvariant());
        string normalizedExpectedArtist = PlatformDetector.NormalizeForLooseMatch((expectedArtist ?? "").ToLowerInvariant());
        string normalizedCandidateTitle = PlatformDetector.NormalizeForLooseMatch((candidateTitle ?? "").ToLowerInvariant());
        string normalizedCandidateAuthor = PlatformDetector.NormalizeForLooseMatch((candidateAuthor ?? "").ToLowerInvariant());
        int titleOverlap = CountTokenOverlap(normalizedExpectedTitle, normalizedCandidateTitle);

        bool titleMatches = CheckTitleMatch(normalizedExpectedTitle, normalizedCandidateTitle);
        bool artistMatches = CheckArtistMatch(normalizedExpectedArtist, normalizedCandidateAuthor);

        if (titleMatches && (artistMatches || titleOverlap >= 1))
        {
            return true;
        }

        if (strictMode)
        {
            return titleOverlap >= 2 && (artistMatches || candidateScore >= 2);
        }

        if (artistMatches && (titleOverlap >= 1 || candidateScore >= 2))
        {
            return true;
        }

        return candidateScore >= 3 && titleOverlap >= 1;
    }

    private static bool CheckTitleMatch(string expectedTitle, string candidateTitle)
    {
        return !string.IsNullOrEmpty(expectedTitle) &&
               !string.IsNullOrEmpty(candidateTitle) &&
               (candidateTitle.Contains(expectedTitle, StringComparison.Ordinal) ||
                expectedTitle.Contains(candidateTitle, StringComparison.Ordinal));
    }

    private static bool CheckArtistMatch(string expectedArtist, string candidateAuthor)
    {
        return string.IsNullOrEmpty(expectedArtist) ||
               expectedArtist == "soundcloud" ||
               expectedArtist == "browser" ||
               (!string.IsNullOrEmpty(candidateAuthor) &&
                (candidateAuthor.Contains(expectedArtist, StringComparison.Ordinal) ||
                 expectedArtist.Contains(candidateAuthor, StringComparison.Ordinal)));
    }

    public static int CountTokenOverlap(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0;
        }

        var rightTokens = new HashSet<string>(
            right.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);

        int overlap = 0;
        foreach (var token in left.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 2)
            {
                continue;
            }

            if (rightTokens.Contains(token))
            {
                overlap++;
            }
        }

        return overlap;
    }

    public static string NormalizeArtworkUrl(string url)
    {
        string normalized = url.Replace("\\u0026", "&").Replace("\\/", "/");
        if (MediaHeuristics.IsLikelySoundCloudPlaceholderArtworkUrl(normalized))
        {
            return normalized;
        }

        return Regex.Replace(normalized, @"-(?:large|t\d+x\d+)\.", "-t500x500.", RegexOptions.IgnoreCase);
    }
}
