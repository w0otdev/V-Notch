using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml;

namespace VNotch.Services;

internal static partial class NativeLyricParser
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Word { internal long Start, End; internal int Offset, Length; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Line { internal long Start; internal int Offset, Length, WordOffset, WordCount; }
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int vn_lyrics_parse(ReadOnlySpan<byte> bytes, int length, int encoding, int kind,
        out long handle, out Line* lines, out int count, out Word* words, out char* characters);
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_lyrics_parsed_close(long handle);
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_lyrics_parse_enhanced(ReadOnlySpan<char> text, int length, long time,
        Span<char> clean, int capacity, out int written, Span<Word> words, int wordCapacity, out int count);

    internal static List<LyricLine> ParseXml(string xml, bool amll) =>
        Parse(MemoryMarshal.AsBytes(xml.AsSpan()), amll ? 0 : 1, utf16: true);

    internal static unsafe List<LyricLine> Parse(ReadOnlySpan<byte> bytes, int kind, bool utf16 = false)
    {
        if (bytes.IsEmpty) return new();
        int status = vn_lyrics_parse(bytes, bytes.Length, utf16 ? 1 : 0, kind,
            out long handle, out Line* lines, out int count, out Word* words, out char* characters);
        if (status != 0)
        {
            if (kind < 2) throw new XmlException("Invalid or oversized lyric XML.");
            throw new JsonException("Invalid or oversized timed lyric JSON.");
        }
        try
        {
            var result = new List<LyricLine>(count);
            foreach (var line in new ReadOnlySpan<Line>(lines, count))
            {
                LyricWord[]? parts = line.WordCount == 0 ? null : new LyricWord[line.WordCount];
                if (parts != null)
                {
                    var source = new ReadOnlySpan<Word>(words + line.WordOffset, line.WordCount);
                    for (int i = 0; i < parts.Length; ++i)
                        parts[i] = new(TimeSpan.FromTicks(source[i].Start), TimeSpan.FromTicks(source[i].End), source[i].Offset, source[i].Length);
                }
                result.Add(new(TimeSpan.FromTicks(line.Start), new string(characters + line.Offset, 0, line.Length), parts));
            }
            return result;
        }
        finally { _ = vn_lyrics_parsed_close(handle); }
    }

    internal static LyricLine ParseEnhanced(TimeSpan time, string text)
    {
        if (!text.Contains('<')) return new(time, text);
        char[]? rented = text.Length > 1024 ? ArrayPool<char>.Shared.Rent(text.Length) : null;
        Span<char> clean = rented == null ? stackalloc char[text.Length] : rented;
        Span<Word> words = stackalloc Word[256];
        try
        {
            if (vn_lyrics_parse_enhanced(text, text.Length, time.Ticks, clean, clean.Length, out int written, words, words.Length, out int count) != 0)
                return new(time, text);
            LyricWord[]? parts = count == 0 ? null : new LyricWord[count];
            for (int i = 0; i < count; ++i)
                parts![i] = new(TimeSpan.FromTicks(words[i].Start), TimeSpan.FromTicks(words[i].End), words[i].Offset, words[i].Length);
            return new(time, written == text.Length ? text : new string(clean[..written]), parts);
        }
        finally { if (rented != null) ArrayPool<char>.Shared.Return(rented); }
    }
}
