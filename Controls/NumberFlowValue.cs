using System.Globalization;
using System.Text.RegularExpressions;

namespace VNotch.Controls;

// Stable place-value keys keep the units column attached when 99 becomes 100.
internal static partial class NumberFlowValue
{
    internal readonly record struct Part(string Key, int Index, char Character, int Digit, int Trend, int Radix = 10);

    [GeneratedRegex(@"^[0-9]{1,3}[:\n][0-9]{2}(?::[0-9]{2})?$")]
    private static partial Regex Clock();

    [GeneratedRegex(@"[\p{Nd}]+(?:[.,\u066b\u066c][\p{Nd}]+)*")]
    private static partial Regex Numbers();

    internal static Part[] Split(string text, string previous)
    {
        var parts = new Part[text.Length];
        for (int i = 0; i < text.Length; i++) parts[i] = new($"s:{i}", i, text[i], -1, 0);
        var old = Numbers().Matches(previous);
        var current = Numbers().Matches(text);
        bool clock = Clock().IsMatch(text) && Clock().IsMatch(previous);
        static long ClockValue(string value) => value.Replace('\n', ':').Split(':').Aggregate(0L, (total, part) => total * 60 + long.Parse(part, CultureInfo.InvariantCulture));
        int clockTrend = clock ? ClockValue(text).CompareTo(ClockValue(previous)) : 0;
        int end = 0;
        for (int group = 0; group < current.Count; group++)
        {
            var match = current[group];
            for (int i = end; i < match.Index; i++)
                parts[i] = parts[i] with { Key = $"s:{group}:{i - end}" };
            int trend = group < old.Count ? Compare(match.Value, old[group].Value) : 0;
            if (clock) trend = clockTrend;
            // A minus belongs to the number, whereas colons/slashes delimit independent values.
            if (match.Index > 0 && text[match.Index - 1] is '-' or '\u2212') trend = -trend;
            int decimalAt = DecimalIndex(match.Value);
            int integerDigits = match.Value.Take(decimalAt < 0 ? match.Length : decimalAt).Count(char.IsDigit);
            int integer = 0, fraction = 0, separator = 0;
            for (int j = 0; j < match.Length; j++)
            {
                int digit = CharUnicodeInfo.GetDecimalDigitValue(match.Value[j]);
                string key = digit < 0 ? $"n:{group}:s:{separator++}" : decimalAt >= 0 && j > decimalAt
                    ? $"n:{group}:f:{fraction++}" : $"n:{group}:i:{integerDigits - ++integer}";
                int radix = clock && group > 0 && j == 0 ? 6 : 10;
                parts[match.Index + j] = new(key, match.Index + j, match.Value[j], digit, trend, radix);
            }
            end = match.Index + match.Length;
        }
        for (int i = end; i < text.Length; i++) parts[i] = parts[i] with { Key = $"s:{current.Count}:{i - end}" };
        return parts;
    }

    private static int DecimalIndex(string value)
    {
        string separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        int index = value.LastIndexOf(separator, StringComparison.Ordinal);
        return index >= 0 ? index : value.LastIndexOf('\u066b');
    }

    private static int Compare(string value, string previous)
    {
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out var a) &&
            decimal.TryParse(previous, NumberStyles.Number, CultureInfo.CurrentCulture, out var b)) return a.CompareTo(b);
        string left = string.Concat(value.Where(char.IsDigit)).TrimStart('0');
        string right = string.Concat(previous.Where(char.IsDigit)).TrimStart('0');
        return left.Length != right.Length ? left.Length.CompareTo(right.Length) : Math.Sign(string.CompareOrdinal(left, right));
    }

    internal static double Destination(double current, int digit, int trend, int radix = 10)
    {
        double cycle = Math.Floor(current / radix) * radix;
        double target = cycle + digit;
        if (trend > 0 && target < current - .001) target += radix;
        else if (trend < 0 && target > current + .001) target -= radix;
        else if (trend == 0) target += Math.Round((current - target) / radix) * radix;
        return target;
    }
}
