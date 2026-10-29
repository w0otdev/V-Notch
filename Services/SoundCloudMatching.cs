using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;

namespace VNotch.Services;

internal static partial class SoundCloudMatching
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_soundcloud_is_valid_track_candidate(
        ReadOnlySpan<char> userPtr,
        int userLen,
        ReadOnlySpan<char> slugPtr,
        int slugLen,
        out byte outValid);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_soundcloud_extract_track_url(
        ReadOnlySpan<char> textPtr,
        int textLen,
        Span<char> outBuf,
        int outCap,
        out int outLen,
        out byte outFound);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_soundcloud_sanitize_search_text(
        ReadOnlySpan<char> valPtr,
        int valLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_soundcloud_score_candidate(
        ReadOnlySpan<char> userPtr,
        int userLen,
        ReadOnlySpan<char> slugPtr,
        int slugLen,
        ReadOnlySpan<char> normTitlePtr,
        int normTitleLen,
        ReadOnlySpan<char> normArtistPtr,
        int normArtistLen,
        out int outScore);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_soundcloud_is_oembed_match(
        ReadOnlySpan<char> expTitlePtr,
        int expTitleLen,
        ReadOnlySpan<char> expArtistPtr,
        int expArtistLen,
        ReadOnlySpan<char> candTitlePtr,
        int candTitleLen,
        ReadOnlySpan<char> candAuthorPtr,
        int candAuthorLen,
        int candScore,
        byte strictMode,
        out byte outMatch);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_soundcloud_normalize_artwork_url(
        ReadOnlySpan<char> urlPtr,
        int urlLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_soundcloud_find_next_anchor_href(
        ReadOnlySpan<char> htmlPtr,
        int htmlLen,
        int startPos,
        out int outHrefStart,
        out int outHrefLen,
        out int outNextPos,
        out byte outFound);

    public static bool IsValidTrackCandidate(string user, string slug)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(slug))
            return false;

        ReadOnlySpan<char> u = user.AsSpan();
        ReadOnlySpan<char> s = slug.AsSpan();
        vn_soundcloud_is_valid_track_candidate(u, u.Length, s, s.Length, out byte valid);
        return valid != 0;
    }

    public static string? ExtractTrackUrl(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return null;

        ReadOnlySpan<char> span = rawText.AsSpan();
        int cap = span.Length + 32;
        Span<char> buf = cap <= 512 ? stackalloc char[cap] : new char[cap];
        vn_soundcloud_extract_track_url(span, span.Length, buf, buf.Length, out int len, out byte found);
        return found != 0 && len > 0 ? new string(buf[..len]) : null;
    }

    public static string SanitizeSearchText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        ReadOnlySpan<char> span = value.AsSpan();
        int cap = span.Length + 16;
        Span<char> buf = cap <= 512 ? stackalloc char[cap] : new char[cap];
        vn_soundcloud_sanitize_search_text(span, span.Length, buf, buf.Length, out int len);
        return len > 0 ? new string(buf[..len]) : string.Empty;
    }

    public static List<(string Url, int Score)> ExtractTrackUrlsFromSearchHtml(string html, string title, string artist)
    {
        var result = new List<(string Url, int Score)>();
        if (string.IsNullOrWhiteSpace(html))
            return result;

        string normalizedTitle = PlatformDetector.NormalizeForLooseMatch(SanitizeSearchText(title));
        string normalizedArtist = PlatformDetector.NormalizeForLooseMatch((artist ?? "").ToLowerInvariant());
        var scoreMap = new Dictionary<string, (int Score, int FirstIndex)>(StringComparer.OrdinalIgnoreCase);
        int orderIndex = 0;

#pragma warning disable S1075 // Canonical SoundCloud origin URI
        var origin = new Uri("https://soundcloud.com/");
#pragma warning restore S1075
        ReadOnlySpan<char> htmlSpan = html.AsSpan();
        int pos = 0;
        while (pos < htmlSpan.Length &&
               vn_soundcloud_find_next_anchor_href(htmlSpan, htmlSpan.Length, pos, out int hrefStart, out int hrefLen, out int nextPos, out byte found) == 0 &&
               found != 0)
        {
            pos = nextPos > pos ? nextPos : pos + 1;
            if (hrefLen <= 0) continue;
            string href = WebUtility.HtmlDecode(html.Substring(hrefStart, hrefLen));
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
            return;

        string url = $"https://soundcloud.com/{user}/{slug}";
        int score = ScoreCandidate(user, slug, normalizedTitle, normalizedArtist);
        if (scoreMap.TryGetValue(url, out var existing))
        {
            if (score > existing.Score)
                scoreMap[url] = (score, existing.FirstIndex);
        }
        else
        {
            scoreMap[url] = (score, orderIndex);
        }
    }

    public static int ScoreCandidate(string user, string slug, string normalizedTitle, string normalizedArtist)
    {
        ReadOnlySpan<char> u = (user ?? string.Empty).AsSpan();
        ReadOnlySpan<char> s = (slug ?? string.Empty).AsSpan();
        ReadOnlySpan<char> t = (normalizedTitle ?? string.Empty).AsSpan();
        ReadOnlySpan<char> a = (normalizedArtist ?? string.Empty).AsSpan();
        vn_soundcloud_score_candidate(u, u.Length, s, s.Length, t, t.Length, a, a.Length, out int score);
        return score;
    }

    public static bool IsOEmbedMatch(string expectedTitle, string expectedArtist, string? candidateTitle, string? candidateAuthor, int candidateScore, bool strictMode = false)
    {
        ReadOnlySpan<char> et = (expectedTitle ?? string.Empty).AsSpan();
        ReadOnlySpan<char> ea = (expectedArtist ?? string.Empty).AsSpan();
        ReadOnlySpan<char> ct = (candidateTitle ?? string.Empty).AsSpan();
        ReadOnlySpan<char> ca = (candidateAuthor ?? string.Empty).AsSpan();
        vn_soundcloud_is_oembed_match(
            et, et.Length,
            ea, ea.Length,
            ct, ct.Length,
            ca, ca.Length,
            candidateScore,
            strictMode ? (byte)1 : (byte)0,
            out byte match);
        return match != 0;
    }

    public static string NormalizeArtworkUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
            return string.Empty;

        ReadOnlySpan<char> span = url.AsSpan();
        int cap = span.Length + 8;
        Span<char> buf = cap <= 512 ? stackalloc char[cap] : new char[cap];
        vn_soundcloud_normalize_artwork_url(span, span.Length, buf, buf.Length, out int len);
        return len > 0 ? new string(buf[..len]) : string.Empty;
    }
}
