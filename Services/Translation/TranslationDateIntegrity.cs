using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace VNotch.Services.Translation;

// Check named weekdays in commonly used languages. This catches observable date changes;
// absence of a mismatch does not certify the rest of a sentence's meaning.
internal static partial class TranslationDateIntegrity
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnWeekdaySpan
    {
        public int Index;
        public int Length;
        public int Day;
    }

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_translation_date_preserves_weekdays(
        ReadOnlySpan<char> origPtr, int origLen,
        ReadOnlySpan<char> srcPtr, int srcLen,
        ReadOnlySpan<char> transPtr, int transLen,
        ReadOnlySpan<char> tgtPtr, int tgtLen,
        out byte outPreserved);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_translation_date_extract_weekdays(
        ReadOnlySpan<char> textPtr, int textLen,
        ReadOnlySpan<char> langPtr, int langLen,
        byte sortByIndex,
        Span<VnWeekdaySpan> outSpans,
        int outCap,
        out int outCount);

    internal static bool PreservesWeekdays(string original, string source, string translated, string target)
    {
        return vn_translation_date_preserves_weekdays(
            original.AsSpan(), original.Length,
            source.AsSpan(), source.Length,
            translated.AsSpan(), translated.Length,
            target.AsSpan(), target.Length,
            out byte preserved) == 0 && preserved != 0;
    }

    internal static string[] DescribeWeekdays(string original, string source)
    {
        return Extract(original, source).Select(day => ((DayOfWeek)day).ToString()).ToArray();
    }

    internal sealed record ProtectedWeekdays(string Text, Dictionary<string, string> Values, string Original, string Source, string Target)
    {
        internal string Restore(string translation)
        {
            bool missing = false;
            foreach (var (token, value) in Values)
            {
                if (!translation.Contains(token, StringComparison.Ordinal)) missing = true;
                translation = translation.Replace(token, value, StringComparison.Ordinal);
            }
            if (missing && !PreservesWeekdays(Original, Source, translation, Target)) throw new TranslationException("translation.failed");
            return translation;
        }
    }

    internal static ProtectedWeekdays Protect(string text, string source, string target)
    {
        var values = new Dictionary<string, string>();
        // English/Vietnamese names do not require case inflection. Other target languages
        // retain model phrasing and use the post-translation weekday check instead.
        if (target is not ("en" or "vi")) return new(text, values, text, source, target);

        Span<VnWeekdaySpan> initial = stackalloc VnWeekdaySpan[32];
        int status = vn_translation_date_extract_weekdays(
            text.AsSpan(), text.Length,
            source.AsSpan(), source.Length,
            0, initial, initial.Length, out int count);
        if (count <= 0) return new(text, values, text, source, target);


        Span<VnWeekdaySpan> spans = status == 0 ? initial[..count] : new VnWeekdaySpan[count];
        if (status != 0)
        {
            vn_translation_date_extract_weekdays(
                text.AsSpan(), text.Length,
                source.AsSpan(), source.Length,
                0, spans, spans.Length, out count);
            spans = spans[..count];
        }

        string original = text;
        var prefixBuilder = new StringBuilder("__VNT_WEEKDAY");
        while (text.Contains(prefixBuilder.ToString(), StringComparison.Ordinal)) prefixBuilder.Append('X');
        string prefix = prefixBuilder.ToString();

        var culture = CultureInfo.GetCultureInfo(target).DateTimeFormat;
        Span<VnWeekdaySpan> dayBuf = stackalloc VnWeekdaySpan[32];
        for (int day = 0; day < 7; day++)
        {
            bool hasDay = false;
            for (int i = 0; i < spans.Length; i++)
            {
                if (spans[i].Day == day) { hasDay = true; break; }
            }
            if (!hasDay) continue;

            // Re-extract on current text so offsets match sequential per-day replacement order.
            int dayStatus = vn_translation_date_extract_weekdays(
                text.AsSpan(), text.Length,
                source.AsSpan(), source.Length,
                1, dayBuf, dayBuf.Length, out int curCount);
            Span<VnWeekdaySpan> curSpans = dayStatus == 0 ? dayBuf[..curCount] : new VnWeekdaySpan[curCount];
            if (dayStatus != 0)
            {
                vn_translation_date_extract_weekdays(
                    text.AsSpan(), text.Length,
                    source.AsSpan(), source.Length,
                    1, curSpans, curSpans.Length, out curCount);
                curSpans = curSpans[..curCount];
            }

            string value = culture.GetDayName((DayOfWeek)day);
            var sb = new StringBuilder(text.Length + 32);
            int cursor = 0;
            for (int i = 0; i < curSpans.Length; i++)
            {
                if (curSpans[i].Day != day || curSpans[i].Index < cursor) continue;
                sb.Append(text, cursor, curSpans[i].Index - cursor);
                string token = $"{prefix}{values.Count}__";
                values.Add(token, value);
                sb.Append(token);
                cursor = curSpans[i].Index + curSpans[i].Length;
            }
            if (cursor > 0)
            {
                sb.Append(text, cursor, text.Length - cursor);
                text = sb.ToString();
            }
        }
        return new(text, values, original, source, target);
    }

    private static int[] Extract(string text, string language)
    {
        Span<VnWeekdaySpan> buf = stackalloc VnWeekdaySpan[32];
        int status = vn_translation_date_extract_weekdays(
            text.AsSpan(), text.Length,
            language.AsSpan(), language.Length,
            1, buf, buf.Length, out int count);
        if (count <= 0) return [];
        Span<VnWeekdaySpan> spans = status == 0 ? buf[..count] : new VnWeekdaySpan[count];
        if (status != 0)
        {
            vn_translation_date_extract_weekdays(
                text.AsSpan(), text.Length,
                language.AsSpan(), language.Length,
                1, spans, spans.Length, out count);
            spans = spans[..count];
        }
        int[] result = new int[spans.Length];
        for (int i = 0; i < spans.Length; i++) result[i] = spans[i].Day;
        return result;
    }
}
