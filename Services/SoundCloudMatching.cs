using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

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
        "sets", "tracks", "likes", "reposts", "spotlight", "albums", "following", "followers",
        "popular-tracks", "recommended", "comments", "related", "stats", "stations"
    };

    public static bool IsValidTrackCandidate(string user, string slug)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(slug))
        {
            return false;
        }

        if (user.Contains('.') || slug.Length < 2)
        {
            return false;
        }

        return !ReservedUsers.Contains(user) && !ReservedTrackSlugs.Contains(slug);
    }

    public static string? ExtractTrackUrl(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        var match = Regex.Match(
            rawText,
            @"https?://(?:(?:www|m)\.)?soundcloud\.com/(?<user>[^/\s?#]+)/(?<slug>[^/\s?#]+)(?:/?(?:[?#\s]|$))",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return null;
        }

        string user = match.Groups["user"].Value;
        string slug = match.Groups["slug"].Value;
        if (!IsValidTrackCandidate(user, slug))
        {
            return null;
        }

        return $"https://soundcloud.com/{user}/{slug}";
    }

    public static string SanitizeSearchText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value.Replace('\u2013', '-').Replace('\u2014', '-').Trim();
        normalized = Regex.Replace(
            normalized,
            @"[\(\[]\s*(?:official\s+(?:audio|video|music\s+video|lyric\s+video)|prod\.?\s+by\s+[^\)\]]+|prod\.?\s+[^\)\]]+|free\s+(?:dl|download))\s*[\)\]]",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"[\[\]\(\)""']", " ");
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

        string normalizedTitle = PlatformDetector.NormalizeForLooseMatch(SanitizeSearchText(title));
        string normalizedArtist = PlatformDetector.NormalizeForLooseMatch((artist ?? "").ToLowerInvariant());
        var scoreMap = new Dictionary<string, (int Score, int FirstIndex)>(StringComparer.OrdinalIgnoreCase);
        int orderIndex = 0;

        using var document = new HtmlParser().ParseDocument(html);
        var origin = new Uri("https://soundcloud.com/");
        foreach (var link in document.QuerySelectorAll("a[href]"))
        {
            string? href = link.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(origin, href, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
                (uri.Host != "soundcloud.com" && uri.Host != "www.soundcloud.com" && uri.Host != "m.soundcloud.com"))
                continue;

            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (segments.Length != 2) continue;

            AddCandidate(scoreMap, segments[0], segments[1], normalizedTitle, normalizedArtist, orderIndex++);
        }

        var sorted = new List<KeyValuePair<string, (int Score, int FirstIndex)>>(scoreMap);
        sorted.Sort(static (a, b) =>
        {
            int cmp = b.Value.Score.CompareTo(a.Value.Score);
            return cmp != 0 ? cmp : a.Value.FirstIndex.CompareTo(b.Value.FirstIndex);
        });

        foreach (var item in sorted)
        {
            result.Add((item.Key, item.Value.Score));
        }

        return result;
    }

    private static void AddCandidate(
        Dictionary<string, (int Score, int FirstIndex)> scoreMap,
        string user,
        string slug,
        string normalizedTitle,
        string normalizedArtist,
        int orderIndex)
    {
        if (!IsValidTrackCandidate(user, slug))
        {
            return;
        }

        string url = $"https://soundcloud.com/{user}/{slug}";
        int score = ScoreCandidate(user, slug, normalizedTitle, normalizedArtist);
        if (scoreMap.TryGetValue(url, out var existing))
        {
            if (score > existing.Score)
            {
                scoreMap[url] = (score, existing.FirstIndex);
            }
        }
        else
        {
            scoreMap[url] = (score, orderIndex);
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
        if (string.IsNullOrEmpty(normalizedTitle) || string.IsNullOrEmpty(normalizedSlug))
        {
            return 0;
        }

        if (normalizedSlug.Equals(normalizedTitle, StringComparison.Ordinal))
            return 8;

        if ((normalizedTitle.Length >= 3 && normalizedSlug.Contains(normalizedTitle, StringComparison.Ordinal)) ||
            (normalizedSlug.Length >= 4 &&
             (normalizedSlug.Contains(' ') || normalizedSlug.Length * 2 >= normalizedTitle.Length) &&
             normalizedTitle.Contains(normalizedSlug, StringComparison.Ordinal)))
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
        if (!HasMeaningfulArtist(normalizedArtist) || string.IsNullOrEmpty(normalizedUser) || normalizedUser.Length < 2)
        {
            return 0;
        }

        if (normalizedUser.Equals(normalizedArtist, StringComparison.Ordinal) ||
            (normalizedArtist.Length >= 2 && normalizedUser.Contains(normalizedArtist, StringComparison.Ordinal)) ||
            (normalizedUser.Length >= 3 && normalizedArtist.Contains(normalizedUser, StringComparison.Ordinal)))
        {
            return 3;
        }

        return 0;
    }

    public static bool IsOEmbedMatch(string expectedTitle, string expectedArtist, string? candidateTitle, string? candidateAuthor, int candidateScore, bool strictMode = false)
    {
        string normalizedExpectedTitle = PlatformDetector.NormalizeForLooseMatch(SanitizeSearchText(expectedTitle ?? ""));
        string normalizedExpectedArtist = PlatformDetector.NormalizeForLooseMatch((expectedArtist ?? "").ToLowerInvariant());
        string normalizedCandidateTitle = PlatformDetector.NormalizeForLooseMatch(SanitizeSearchText(candidateTitle ?? ""));
        string normalizedCandidateAuthor = PlatformDetector.NormalizeForLooseMatch((candidateAuthor ?? "").ToLowerInvariant());

        if (string.IsNullOrEmpty(normalizedExpectedTitle) || string.IsNullOrEmpty(normalizedCandidateTitle))
        {
            return false;
        }

        string strippedCandidateTitle = StripTrailingByAuthor(normalizedCandidateTitle, normalizedCandidateAuthor);
        string coreExpectedTitle = ExtractCoreTitle(normalizedExpectedTitle, normalizedExpectedArtist);
        string coreCandidateTitle = ExtractCoreTitle(strippedCandidateTitle, normalizedCandidateAuthor);

        // Shared words and title prefixes cannot identify a particular upload/version.
        bool titleMatches = CheckTitleMatch(normalizedExpectedTitle, strippedCandidateTitle) ||
                            CheckTitleMatch(coreExpectedTitle, coreCandidateTitle);
        if (!titleMatches)
            return false;

        if (HasMeaningfulArtist(normalizedExpectedArtist))
            return CheckPositiveArtistMatch(normalizedExpectedArtist, normalizedCandidateAuthor, normalizedCandidateTitle);

        return !strictMode || candidateScore >= 3;
    }

    private static bool HasMeaningfulArtist(string normalizedArtist)
        => !string.IsNullOrEmpty(normalizedArtist) &&
           normalizedArtist != "soundcloud" &&
           normalizedArtist != "browser" &&
           normalizedArtist != "unknown";

    private static string StripTrailingByAuthor(string normalizedCandidateTitle, string normalizedCandidateAuthor)
    {
        if (string.IsNullOrEmpty(normalizedCandidateTitle) || string.IsNullOrEmpty(normalizedCandidateAuthor))
        {
            return normalizedCandidateTitle;
        }

        string suffix = " by " + normalizedCandidateAuthor;
        if (normalizedCandidateTitle.EndsWith(suffix, StringComparison.Ordinal) &&
            normalizedCandidateTitle.Length > suffix.Length)
        {
            return normalizedCandidateTitle[..^suffix.Length].TrimEnd();
        }

        return normalizedCandidateTitle;
    }

    private static string ExtractCoreTitle(string normalizedTitle, string normalizedArtist)
    {
        if (HasMeaningfulArtist(normalizedArtist) &&
            normalizedTitle.StartsWith(normalizedArtist + " ", StringComparison.Ordinal) &&
            normalizedTitle.Length > normalizedArtist.Length + 1)
        {
            return normalizedTitle[(normalizedArtist.Length + 1)..].TrimStart();
        }

        return normalizedTitle;
    }

    private static bool CheckTitleMatch(string expectedTitle, string candidateTitle)
    {
        if (string.IsNullOrEmpty(expectedTitle) || string.IsNullOrEmpty(candidateTitle))
        {
            return false;
        }

        return expectedTitle.Equals(candidateTitle, StringComparison.Ordinal);
    }

    private static bool CheckPositiveArtistMatch(string expectedArtist, string candidateAuthor, string candidateTitle)
    {
        if (!string.IsNullOrEmpty(candidateAuthor))
        {
            return candidateAuthor.Equals(expectedArtist, StringComparison.Ordinal);
        }

        return expectedArtist.Length >= 3 &&
               (candidateTitle.StartsWith(expectedArtist + " ", StringComparison.Ordinal) ||
                candidateTitle.EndsWith(" by " + expectedArtist, StringComparison.Ordinal));
    }

    public static string NormalizeArtworkUrl(string url)
    {
        // Preserve the URL supplied by oEmbed, including its size and any signature.
        return url.Replace("\\u0026", "&").Replace("\\/", "/");
    }
}
