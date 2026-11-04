using System.Text.Json;

namespace VNotch.Services;

internal readonly record struct LyricWord(TimeSpan Start, TimeSpan End, int Offset, int Length);

internal static partial class LyricTiming
{
    internal static LyricLine ParseEnhanced(TimeSpan time, string text) => NativeLyricParser.ParseEnhanced(time, text);

    internal static LyricWord[]? ValidateWords(TimeSpan lineTime, string text, List<LyricWord> words)
    {
        if (words.Count == 0 || words.Count > 256) return null;
        int offset = 0;
        TimeSpan previous = lineTime;
        foreach (var word in words)
        {
            if (word.Start < previous || word.End < word.Start || word.Offset < offset ||
                word.Length <= 0 || word.Offset > text.Length - word.Length) return null;
            previous = word.Start;
            offset = word.Offset + word.Length;
        }
        return words.ToArray();
    }

    internal static List<LyricLine> ParseTimedLines(JsonElement lines, JsonElement wordMap = default, bool durationTiming = false)
    {
        if (lines.ValueKind != JsonValueKind.Array) return new();
        string json = "{\"lines\":" + lines.GetRawText() + ",\"wordMap\":" +
            (wordMap.ValueKind == JsonValueKind.Undefined ? "null" : wordMap.GetRawText()) + "}";
        return NativeLyricParser.Parse(System.Text.Encoding.UTF8.GetBytes(json), durationTiming ? 3 : 2);
    }
}
