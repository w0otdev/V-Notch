using System;
using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services.Spotlight;

internal static partial class SpotlightRanker
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotlight_ranker_score(
        ReadOnlySpan<char> displayTitlePtr, int displayTitleLen,
        ReadOnlySpan<char> rawTitlePtr, int rawTitleLen,
        ReadOnlySpan<char> subtitlePtr, int subtitleLen,
        ReadOnlySpan<char> displaySubtitlePtr, int displaySubtitleLen,
        ReadOnlySpan<char> targetPtr, int targetLen,
        int kind,
        ReadOnlySpan<char> queryPtr, int queryLen,
        out double outScore);

    public static double Score(SpotlightSearchItem item, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;

        ReadOnlySpan<char> dt = (item.DisplayTitle ?? string.Empty).AsSpan();
        ReadOnlySpan<char> rt = (item.Title ?? string.Empty).AsSpan();
        ReadOnlySpan<char> sub = (item.Subtitle ?? string.Empty).AsSpan();
        ReadOnlySpan<char> dsub = (item.DisplaySubtitle ?? string.Empty).AsSpan();
        ReadOnlySpan<char> tgt = (item.Target ?? string.Empty).AsSpan();
        ReadOnlySpan<char> q = query.AsSpan();

        int kindVal = item.Kind switch
        {
            SpotlightResultKind.Application => 0,
            SpotlightResultKind.File => 1,
            SpotlightResultKind.Folder => 2,
            _ => 3
        };

        vn_spotlight_ranker_score(
            dt, dt.Length,
            rt, rt.Length,
            sub, sub.Length,
            dsub, dsub.Length,
            tgt, tgt.Length,
            kindVal,
            q, q.Length,
            out double score);

        return score;
    }
}
