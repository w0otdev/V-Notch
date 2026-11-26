using System.Buffers;
using System.Runtime.InteropServices;
using VNotch.Interop;
using VNotch.Models;

namespace VNotch.Services.Spotlight;

// The snapshot owns all buffers. Native calls borrow their spans synchronously,
// so overlapping searches can finish on a retired index without a handle or a pin.
internal sealed partial class AppSearchIndex
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Row
    {
        internal int Offset, RawTitle, Title, Subtitle, Stem, Filename, Bonus;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Match
    {
        internal double Score;
        internal int Index, Reserved;
    }

    [LibraryImport(NativeAbi.LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotlight_prepare_app(
        ReadOnlySpan<char> display, int displayLen, ReadOnlySpan<char> raw, int rawLen,
        ReadOnlySpan<char> subtitle, int subtitleLen, ReadOnlySpan<char> target, int targetLen,
        Span<char> buffer, int capacity, out Row row, out int written);

    [LibraryImport(NativeAbi.LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotlight_rank_apps(
        ReadOnlySpan<Row> rows, int count, ReadOnlySpan<char> arena, int arenaLen,
        ReadOnlySpan<char> query, int queryLen, Span<Match> matches, int limit, out int found);

    internal IReadOnlyList<SpotlightSearchItem> Apps { get; }
    private readonly Row[] _rows;
    private readonly char[] _text;

    internal AppSearchIndex(IReadOnlyList<SpotlightSearchItem> apps)
    {
        Apps = apps;
        _rows = new Row[apps.Count];
        var text = new ArrayBufferWriter<char>();
        for (int i = 0; i < apps.Count; i++)
        {
            var item = apps[i];
            var presentation = new SpotlightResultPresentation(item);
            string display = presentation.DisplayTitle;
            string subtitle = item.Subtitle.StartsWith("spotlight.", StringComparison.Ordinal)
                ? presentation.DisplaySubtitle : item.Subtitle;
            var buffer = text.GetSpan();
            int status = Prepare(buffer, out var row, out int written);
            if (status == (int)NativeStatus.BufferTooSmall)
                status = Prepare(text.GetSpan(written), out row, out written);
            NativeResult.RequireSuccess(status, nameof(vn_spotlight_prepare_app));
            row.Offset = text.WrittenCount;
            _rows[i] = row;
            text.Advance(written);

            int Prepare(Span<char> output, out Row prepared, out int length) =>
                vn_spotlight_prepare_app(display, display.Length, item.Title, item.Title.Length,
                    subtitle, subtitle.Length, item.Target, item.Target.Length, output, output.Length, out prepared, out length);
        }
        _text = text.WrittenSpan.ToArray();
    }

    internal IReadOnlyList<SpotlightSearchItem> Search(string query, int limit, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        limit = Math.Min(limit, _rows.Length);
        if (limit <= 0 || string.IsNullOrWhiteSpace(query)) return [];
        Span<Match> matches = limit <= 64 ? stackalloc Match[limit] : new Match[limit];
        NativeResult.RequireSuccess(vn_spotlight_rank_apps(_rows, _rows.Length, _text, _text.Length,
            query, query.Length, matches, limit, out int found), nameof(vn_spotlight_rank_apps));
        cancellation.ThrowIfCancellationRequested();
        if (found == 0) return [];
        string subtitle = Loc.Get("spotlight.kind.application");
        var results = new SpotlightSearchItem[found];
        for (int i = 0; i < found; i++)
            results[i] = Apps[matches[i].Index] with { Subtitle = subtitle, Score = matches[i].Score };
        return results;
    }
}
