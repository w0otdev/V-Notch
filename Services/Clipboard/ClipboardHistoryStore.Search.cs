using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using VNotch.Models;

namespace VNotch.Services.Clipboard;

public sealed partial class ClipboardHistoryStore
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SearchSlice { public int Offset, Length; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SearchEntry
    {
        public SearchSlice Title, Preview, Source, FullText;
        public int GroupStart, GroupCount, Kind;
        public byte Personal, Archived, Pinned, Sensitive;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SearchLabel { public SearchSlice Id, Name; }

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_store_filter_entries_batch(
        ReadOnlySpan<SearchEntry> entries, int count, ReadOnlySpan<SearchSlice> groups, int groupCount,
        ReadOnlySpan<SearchLabel> labels, int labelCount, ReadOnlySpan<char> pool, int poolLength,
        ReadOnlySpan<char> query, int queryLength, ReadOnlySpan<char> category, int categoryLength,
        byte personal, Span<byte> matches, int capacity);

    // This only marshals strings. All category/token decisions run in the batch export.
    private sealed class SearchTextPool : IDisposable
    {
        private char[] _buffer = ArrayPool<char>.Shared.Rent(16384);
        private int _length;
        internal ReadOnlySpan<char> Text => _buffer.AsSpan(0, _length);
        internal SearchSlice Add(string? value)
        {
            int length = value?.Length ?? 0;
            int required = checked(_length + length);
            if (required > _buffer.Length)
            {
                char[] next = ArrayPool<char>.Shared.Rent(Math.Max(required, checked(_buffer.Length * 2)));
                _buffer.AsSpan(0, _length).CopyTo(next);
                ArrayPool<char>.Shared.Return(_buffer, clearArray: true);
                _buffer = next;
            }
            value.AsSpan().CopyTo(_buffer.AsSpan(_length));
            var slice = new SearchSlice { Offset = _length, Length = length };
            _length = required;
            return slice;
        }
        public void Dispose() => ArrayPool<char>.Shared.Return(_buffer, clearArray: true);
    }

    private IReadOnlyList<ClipboardEntry> SearchCore(string? query, string category, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        bool personal = category == "Personal";
        if (personal && !IsPersonalUnlocked) return Array.Empty<ClipboardEntry>();
        query ??= string.Empty;
        List<ClipboardEntry> candidates;
        lock (_entriesLock)
        {
            candidates = new List<ClipboardEntry>(_entries.Count);
            foreach (var pair in _entries) candidates.Add(pair.Value);
        }
        int count = candidates.Count;
        if (count == 0) return Array.Empty<ClipboardEntry>();
        int groupCount = 0;
        foreach (var entry in candidates) groupCount = checked(groupCount + entry.Groups.Length);
        var entries = ArrayPool<SearchEntry>.Shared.Rent(count);
        var groups = ArrayPool<SearchSlice>.Shared.Rent(Math.Max(1, groupCount));
        var labels = ArrayPool<SearchLabel>.Shared.Rent(Math.Max(1, _labels.Length));
        var matches = ArrayPool<byte>.Shared.Rent(count);
        using var pool = new SearchTextPool();
        try
        {
            int groupIndex = 0;
            for (int i = 0; i < count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var entry = candidates[i];
                entries[i] = new SearchEntry
                {
                    Title = pool.Add(entry.Title),
                    Preview = pool.Add(entry.Preview),
                    Source = pool.Add(entry.SourceApp),
                    FullText = new SearchSlice { Length = -1 },
                    GroupStart = groupIndex,
                    GroupCount = entry.Groups.Length,
                    Kind = (int)entry.Kind,
                    Personal = entry.IsPersonal ? (byte)1 : (byte)0,
                    Archived = entry.IsArchived ? (byte)1 : (byte)0,
                    Pinned = entry.IsPinned ? (byte)1 : (byte)0,
                    Sensitive = entry.IsSensitive ? (byte)1 : (byte)0
                };
                foreach (string group in entry.Groups) groups[groupIndex++] = pool.Add(group);
            }
            for (int i = 0; i < _labels.Length; i++)
                labels[i] = new SearchLabel { Id = pool.Add(_labels[i].Id), Name = pool.Add(_labels[i].Name) };

            int Filter() => vn_clipboard_store_filter_entries_batch(entries, count, groups, groupCount, labels, _labels.Length,
                pool.Text, pool.Text.Length, query, query.Length, category, category.Length, personal ? (byte)1 : (byte)0, matches, count);
            if (Filter() != 0) throw new InvalidOperationException("Native clipboard search failed.");
            bool readFullText = false;
            for (int i = 0; i < count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (matches[i] != 2) continue;
                var entry = candidates[i];
                string fullText;
                try
                {
                    if (!_searchText.TryGet(entry.Id, out fullText!))
                    {
                        fullText = ReadContent(entry).Text;
                        _searchText.Set(entry.Id, fullText);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
                {
                    RuntimeLog.Warn("CLIPBOARD", "An unreadable clipboard item was skipped during search.");
                    fullText = string.Empty;
                }
                entries[i].FullText = pool.Add(fullText);
                readFullText = true;
            }
            if (readFullText && Filter() != 0) throw new InvalidOperationException("Native clipboard search failed.");
            cancellation.ThrowIfCancellationRequested();
            var result = new List<ClipboardEntry>(count);
            for (int i = 0; i < count; i++) if (matches[i] == 1) result.Add(candidates[i]);
            result.Sort(s_copiedUtcDescComparison);
            return result;
        }
        finally
        {
            ArrayPool<SearchEntry>.Shared.Return(entries);
            ArrayPool<SearchSlice>.Shared.Return(groups);
            ArrayPool<SearchLabel>.Shared.Return(labels);
            ArrayPool<byte>.Shared.Return(matches);
        }
    }
}
