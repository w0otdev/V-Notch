using System.Globalization;
using System.Text.RegularExpressions;

namespace VNotch.Services.Translation;

// Check named weekdays in commonly used languages. This catches observable date changes;
// absence of a mismatch does not certify the rest of a sentence's meaning.
internal static class TranslationDateIntegrity
{
    // Sunday first, matching the DayOfWeek enum. Patterns include common inflected forms.
    private static readonly Dictionary<string, string[]> Weekdays = new()
    {
        ["en"] = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"],
        ["vi"] = ["chủ nhật", "thứ hai", "thứ ba", "thứ tư", "thứ năm", "thứ sáu", "thứ bảy"],
        ["zh"] = ["(?:周|星期|礼拜)[日天]", "(?:周|星期|礼拜)一", "(?:周|星期|礼拜)二", "(?:周|星期|礼拜)三", "(?:周|星期|礼拜)四", "(?:周|星期|礼拜)五", "(?:周|星期|礼拜)六"],
        ["ja"] = ["日曜(?:日)?", "月曜(?:日)?", "火曜(?:日)?", "水曜(?:日)?", "木曜(?:日)?", "金曜(?:日)?", "土曜(?:日)?"],
        ["ko"] = ["일요일", "월요일", "화요일", "수요일", "목요일", "금요일", "토요일"],
        ["fr"] = ["dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi"],
        ["es"] = ["domingo", "lunes", "martes", "miércoles", "jueves", "viernes", "sábado"],
        ["de"] = ["sonntag", "montag", "dienstag", "mittwoch", "donnerstag", "freitag", "samstag"],
        ["ru"] = ["воскресень[еяю]", "понедельник(?:а|у|ом)?", "вторник(?:а|у|ом)?", "сред[ауы]", "четверг(?:а|у|ом)?", "пятниц[ауы]", "суббот[ауы]"],
        ["th"] = ["วันอาทิตย์", "วันจันทร์", "วันอังคาร", "วันพุธ", "วันพฤหัสบดี", "วันศุกร์", "วันเสาร์"]
    };

    internal static bool PreservesWeekdays(string original, string source, string translated, string target)
    {
        if (!Weekdays.ContainsKey(source) || !Weekdays.ContainsKey(target)) return true;
        return Extract(original, source).SequenceEqual(Extract(translated, target));
    }

    internal static string[] DescribeWeekdays(string original, string source) => Weekdays.ContainsKey(source)
        ? Extract(original, source).Select(day => ((DayOfWeek)day).ToString()).ToArray() : [];

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
        if (target is not ("en" or "vi") || !Weekdays.TryGetValue(source, out var patterns)) return new(text, values, text, source, target);
        string original = text, prefix = "__VNT_WEEKDAY";
        while (text.Contains(prefix, StringComparison.Ordinal)) prefix += "X";
        for (int day = 0; day < patterns.Length; day++)
        {
            string pattern = Pattern(patterns[day], source);
            string value = CultureInfo.GetCultureInfo(target).DateTimeFormat.GetDayName((DayOfWeek)day);
            text = Regex.Replace(text, pattern, _ =>
            {
                string token = $"{prefix}{values.Count}__";
                values.Add(token, value);
                return token;
            }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        return new(text, values, original, source, target);
    }

    private static string Pattern(string pattern, string language) => language is "zh" or "ja" or "ko" or "th"
        ? pattern : $@"(?<!\p{{L}})(?:{pattern})(?!\p{{L}})";

    private static int[] Extract(string text, string language)
    {
        var result = new List<int>();
        var patterns = Weekdays[language];
        for (int day = 0; day < patterns.Length; day++)
        {
            string pattern = Pattern(patterns[day], language);
            var matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            result.AddRange(Enumerable.Repeat(day, matches.Count));
        }
        return result.Order().ToArray();
    }
}
