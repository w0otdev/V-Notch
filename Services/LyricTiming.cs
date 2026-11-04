using System.Text.Json;
using System.Text.RegularExpressions;

namespace VNotch.Services;

internal readonly record struct LyricWord(TimeSpan Start, TimeSpan End, int Offset, int Length);

internal static partial class LyricTiming
{
    [GeneratedRegex(@"<([0-9]{1,3}):([0-5][0-9])(?:\.([0-9]{1,3}))?>")]
    private static partial Regex WordTags();

    internal static LyricLine ParseEnhanced(TimeSpan time, string text)
    {
        var tags = WordTags().Matches(text);
        if (tags.Count == 0) return new(time, text);
        var clean = new System.Text.StringBuilder(text.Length);
        var words = new List<LyricWord>();
        clean.Append(text.AsSpan(0, tags[0].Index));
        for (int i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];
            int start = tag.Index + tag.Length;
            int end = i + 1 < tags.Count ? tags[i + 1].Index : text.Length;
            string part = text[start..end];
            var begin = Timestamp(tag);
            // Enhanced LRC has start tags only. The last word has no trustworthy end.
            var finish = i + 1 < tags.Count ? Timestamp(tags[i + 1]) : begin;
            words.Add(new(begin, finish, clean.Length, part.Length));
            clean.Append(part);
        }
        string result = clean.ToString();
        return new(time, result, ValidateWords(time, result, words));
    }

    private static TimeSpan Timestamp(Match tag) => TimeSpan.FromMilliseconds(
        int.Parse(tag.Groups[1].Value) * 60000L + int.Parse(tag.Groups[2].Value) * 1000L +
        (tag.Groups[3].Success ? int.Parse(tag.Groups[3].Value.PadRight(3, '0')) : 0));

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
        var result = new List<LyricLine>();
        if (lines.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in lines.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String ||
                !TryTime(item, durationTiming ? "time" : "start", out var time)) continue;
            string text = value.GetString()!;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 4096) continue;
            var words = new List<LyricWord>();
            if (!item.TryGetProperty(durationTiming ? "syllabus" : "words", out var parts) && item.TryGetProperty("id", out var id))
            {
                if (wordMap.ValueKind == JsonValueKind.Array && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out int index) && index >= 0 && index < wordMap.GetArrayLength())
                    parts = wordMap[index];
                else if (wordMap.ValueKind == JsonValueKind.Object)
                    wordMap.TryGetProperty(id.ToString(), out parts);
            }
            if (parts.ValueKind == JsonValueKind.Array)
            {
                int offset = 0;
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("text", out var label) ||
                        label.ValueKind != JsonValueKind.String || !TryTime(part, durationTiming ? "time" : "start", out var start) ||
                        !TryTime(part, durationTiming ? "duration" : "end", out var end)) { words.Clear(); break; }
                    if (durationTiming) end += start;
                    if (end > TimeSpan.FromDays(1)) { words.Clear(); break; }
                    string word = label.GetString()!;
                    int at = word.Length == 0 ? -1 : text.IndexOf(word, offset, StringComparison.Ordinal);
                    if (at < 0) { words.Clear(); break; }
                    words.Add(new(start, end, at, word.Length));
                    offset = at + word.Length;
                }
            }
            result.Add(new(time, text, ValidateWords(time, text, words)));
        }
        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        return result;
    }

    private static bool TryTime(JsonElement value, string name, out TimeSpan time)
    {
        time = default;
        if (!value.TryGetProperty(name, out var number) || number.ValueKind != JsonValueKind.Number ||
            !number.TryGetDouble(out double ms) || !double.IsFinite(ms) || ms < 0 || ms > 86400000) return false;
        time = TimeSpan.FromMilliseconds(ms);
        return true;
    }
}
