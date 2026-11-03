using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace VNotch.Services;

internal static partial class YouTubeMetadataParsing
{
    internal const int MaxJsonResponseBytes = 2 * 1024 * 1024;
    internal static bool LooksLikeQuotaExceeded(ReadOnlySpan<byte> body) =>
        body.IndexOf("quotaExceeded"u8) >= 0 || body.IndexOf("dailyLimitExceeded"u8) >= 0 || body.IndexOf("rateLimitExceeded"u8) >= 0;
    internal readonly record struct ParsedItem(string First, string Second, string Third, string Fourth, string Fifth, long Ticks, int Flags);
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct NativeJsonRow
    {
        public fixed int Offsets[5];
        public fixed int Lengths[5];
        public long Ticks;
        public int Flags;
    }
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int vn_youtube_parse_utf8(ReadOnlySpan<byte> bytes, int length, int kind, ReadOnlySpan<char> expected, int expectedLength,
        out long handle, out NativeJsonRow* rows, out int count, out char* characters, out byte hasCollection);
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_youtube_parsed_close(long handle);
    private sealed class ParsedHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal ParsedHandle(long value) : base(true) => SetHandle((IntPtr)value);
        protected override bool ReleaseHandle() => vn_youtube_parsed_close(handle.ToInt64()) == 0;
    }

    internal static unsafe (List<ParsedItem> Items, bool HasCollection) ParseUtf8(ReadOnlySpan<byte> bytes, int kind, string expected = "")
    {
        if (bytes.IsEmpty) return (new(), false);
        if (vn_youtube_parse_utf8(bytes, bytes.Length, kind, expected, expected.Length, out long handle, out NativeJsonRow* rows,
                out int count, out char* characters, out byte collection) != 0) throw new JsonException("Invalid YouTube JSON response.");
        using var owner = new ParsedHandle(handle);
        var result = new List<ParsedItem>(count);
        for (int i = 0; i < count; ++i)
        {
            NativeJsonRow* row = rows + i;
            result.Add(new(ReadField(row, 0, characters), ReadField(row, 1, characters), ReadField(row, 2, characters),
                ReadField(row, 3, characters), ReadField(row, 4, characters), row->Ticks, row->Flags));
        }
        return (result, collection != 0);
    }

    private static unsafe string ReadField(NativeJsonRow* row, int field, char* characters) =>
        row->Lengths[field] == 0 ? "" : new string(characters + row->Offsets[field], 0, row->Lengths[field]);

    internal static async Task<List<YouTubeLookupResult>> ReadMetadataAsync(HttpResponseMessage response, int kind, string expected, CancellationToken cancellation)
    {
        using var body = await PooledUtf8Response.ReadAsync(response, MaxJsonResponseBytes, cancellation).ConfigureAwait(false);
        var parsed = ParseUtf8(body.Span, kind, expected);
        var result = new List<YouTubeLookupResult>(parsed.Items.Count);
        foreach (var item in parsed.Items)
            result.Add(new YouTubeLookupResult { Id = item.First, Title = item.Second, Author = item.Third,
                ThumbnailUrl = string.IsNullOrEmpty(item.Fourth) ? null : item.Fourth, Duration = TimeSpan.FromTicks(item.Ticks),
                Source = kind is 0 or 4 ? YouTubeLookupSource.DataApi : YouTubeLookupSource.OEmbed });
        return result;
    }
}
