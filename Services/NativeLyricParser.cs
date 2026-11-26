using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml;
using Microsoft.Win32.SafeHandles;
using VNotch.Interop;

namespace VNotch.Services;

internal static partial class NativeLyricParser
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Word
    {
        internal long Start;
        internal long End;
        internal int Offset;
        internal int Length;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Line
    {
        internal long Start;
        internal int Offset;
        internal int Length;
        internal int WordOffset;
        internal int WordCount;
    }

    private sealed class ParsedLyricsHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ParsedLyricsHandle() : base(true) { }

        protected override bool ReleaseHandle() =>
            vn_lyrics_parsed_close(handle.ToInt64()) == (int)NativeStatus.Ok;
    }

    [LibraryImport(NativeAbi.LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int vn_lyrics_parse(
        ReadOnlySpan<byte> bytes,
        int length,
        int encoding,
        int kind,
        out ParsedLyricsHandle handle,
        out Line* lines,
        out int count,
        out Word* words,
        out char* characters);

    [LibraryImport(NativeAbi.LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_lyrics_parsed_close(long handle);

    [LibraryImport(NativeAbi.LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_lyrics_parse_enhanced(
        ReadOnlySpan<char> text,
        int length,
        long time,
        Span<char> clean,
        int capacity,
        out int written,
        Span<Word> words,
        int wordCapacity,
        out int count);

    internal static unsafe List<LyricLine> Parse(ReadOnlySpan<byte> bytes, int kind, bool utf16 = false)
    {
        if (bytes.IsEmpty)
        {
            return [];
        }

        int status = vn_lyrics_parse(
            bytes,
            bytes.Length,
            utf16 ? 1 : 0,
            kind,
            out ParsedLyricsHandle handle,
            out Line* lines,
            out int count,
            out Word* words,
            out char* characters);

        using (handle)
        {
            if (status != (int)NativeStatus.Ok)
            {
                if (kind < 2)
                {
                    throw new XmlException("Invalid or oversized lyric XML.");
                }

                throw new JsonException("Invalid or oversized timed lyric JSON.");
            }

            bool addedRef = false;
            try
            {
                handle.DangerousAddRef(ref addedRef);
                var result = new List<LyricLine>(count);
                foreach (Line line in new ReadOnlySpan<Line>(lines, count))
                {
                    LyricWord[]? parts = line.WordCount == 0 ? null : new LyricWord[line.WordCount];
                    if (parts != null)
                    {
                        var source = new ReadOnlySpan<Word>(words + line.WordOffset, line.WordCount);
                        for (int i = 0; i < parts.Length; ++i)
                        {
                            parts[i] = new(
                                TimeSpan.FromTicks(source[i].Start),
                                TimeSpan.FromTicks(source[i].End),
                                source[i].Offset,
                                source[i].Length);
                        }
                    }

                    result.Add(new(
                        TimeSpan.FromTicks(line.Start),
                        new string(characters + line.Offset, 0, line.Length),
                        parts));
                }

                return result;
            }
            finally
            {
                if (addedRef)
                {
                    handle.DangerousRelease();
                }
            }
        }
    }

    internal static LyricLine ParseEnhanced(TimeSpan time, string text)
    {
        if (!text.Contains('<'))
        {
            return new(time, text);
        }

        char[]? rented = text.Length > 1024 ? ArrayPool<char>.Shared.Rent(text.Length) : null;
        Span<char> clean = rented == null ? stackalloc char[text.Length] : rented;
        Span<Word> words = stackalloc Word[256];
        try
        {
            if (vn_lyrics_parse_enhanced(text, text.Length, time.Ticks, clean, clean.Length, out int written, words, words.Length, out int count) != (int)NativeStatus.Ok)
            {
                return new(time, text);
            }

            LyricWord[]? parts = count == 0 ? null : new LyricWord[count];
            for (int i = 0; i < count; ++i)
            {
                parts![i] = new(
                    TimeSpan.FromTicks(words[i].Start),
                    TimeSpan.FromTicks(words[i].End),
                    words[i].Offset,
                    words[i].Length);
            }

            return new(time, written == text.Length ? text : new string(clean[..written]), parts);
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }
}
