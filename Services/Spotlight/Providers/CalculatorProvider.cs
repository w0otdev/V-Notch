using System.Globalization;
using System.Text.RegularExpressions;
using VNotch.Models;

namespace VNotch.Services.Spotlight.Providers;

internal sealed partial class CalculatorProvider : ISpotlightProvider
{
    private const int MaxExpressionLength = 64;

    internal const double ResultScore = 1100;

    public bool IsAvailable => true;
    public bool IsInstant => true;

    [GeneratedRegex(@"^[\d\s+\-*/().%]+$")]
    private static partial Regex ExpressionShape();

    public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SpotlightSearchItem> results =
            limit > 0 && TryEvaluate(query, out SpotlightSearchItem? item)
                ? new[] { item! }
                : Array.Empty<SpotlightSearchItem>();
        return Task.FromResult(results);
    }

    internal static bool TryEvaluate(string query, out SpotlightSearchItem? item)
    {
        item = null;
        string expression = query.Trim();
        if (expression.Length == 0 || expression.Length > MaxExpressionLength) return false;
        if (!ExpressionShape().IsMatch(expression)) return false;
        if (!expression.Any(char.IsAsciiDigit)) return false;
        // A bare number ("2024") is a search term, not a calculation.
        if (!expression.Any(c => c is '+' or '*' or '/' or '%')
            && expression.IndexOf('-', 1) < 0)
        {
            return false;
        }

        int pos = 0;
        ReadOnlySpan<char> span = expression.AsSpan();
        if (!TryParseAddSub(span, ref pos, out double value)) return false;
        SkipWhitespace(span, ref pos);
        if (pos != span.Length || double.IsNaN(value) || double.IsInfinity(value)) return false;

        string display = value.ToString("0.##########", CultureInfo.CurrentCulture);
        item = new SpotlightSearchItem(
            $"calc:{expression}",
            SpotlightResultKind.Calculation,
            display,
            $"{expression} =",
            display)
        {
            Score = ResultScore
        };
        return true;
    }

    private static bool TryParseAddSub(ReadOnlySpan<char> s, ref int pos, out double value)
    {
        if (!TryParseMulDivMod(s, ref pos, out value)) return false;
        while (true)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length || (s[pos] != '+' && s[pos] != '-')) return true;
            char op = s[pos++];
            if (!TryParseMulDivMod(s, ref pos, out double rhs)) return false;
            value = op == '+' ? value + rhs : value - rhs;
        }
    }

    private static bool TryParseMulDivMod(ReadOnlySpan<char> s, ref int pos, out double value)
    {
        if (!TryParseFactor(s, ref pos, out value)) return false;
        while (true)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length || (s[pos] != '*' && s[pos] != '/' && s[pos] != '%')) return true;
            char op = s[pos++];
            if (!TryParseFactor(s, ref pos, out double rhs)) return false;
            value = op switch
            {
                '*' => value * rhs,
                '/' => value / rhs,
                _ => value % rhs
            };
        }
    }

    private static bool TryParseFactor(ReadOnlySpan<char> s, ref int pos, out double value)
    {
        SkipWhitespace(s, ref pos);
        value = 0;
        if (pos >= s.Length) return false;

        if (s[pos] is '+' or '-')
        {
            char sign = s[pos++];
            if (!TryParseFactor(s, ref pos, out double inner)) return false;
            value = sign == '-' ? -inner : inner;
            return true;
        }

        if (s[pos] == '(')
        {
            pos++;
            if (!TryParseAddSub(s, ref pos, out value)) return false;
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length || s[pos] != ')') return false;
            pos++;
            return true;
        }

        int start = pos;
        while (pos < s.Length && (char.IsAsciiDigit(s[pos]) || s[pos] == '.'))
            pos++;

        return pos > start && double.TryParse(
            s[start..pos],
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static void SkipWhitespace(ReadOnlySpan<char> s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos]))
            pos++;
    }
}
