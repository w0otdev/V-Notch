using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using VNotch.Models;

namespace VNotch.Services.Spotlight.Providers;

/// <summary>
/// Evaluates plain arithmetic queries ("52*18+3") inline, macOS-Spotlight style.
/// The result row copies its value to the clipboard when opened.
/// </summary>
internal sealed partial class CalculatorProvider : ISpotlightProvider
{
    private const int MaxExpressionLength = 64;

    /// <summary>Outranks every lexical tier in SpotlightRanker (max 1000).</summary>
    internal const double ResultScore = 1100;

    public bool IsAvailable => true;
    public bool IsInstant => true;

    [GeneratedRegex(@"^[\d\s+\-*/().%]+$")]
    private static partial Regex ExpressionShape();

    [GeneratedRegex(@"(?<![\d.])(\d+)(?![\d.])")]
    private static partial Regex IntegerLiteral();

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

        double value;
        try
        {
            // DataTable.Compute performs integer division on integer literals;
            // promoting them to decimals gives calculator semantics (5/2 = 2.5).
            string promoted = IntegerLiteral().Replace(expression, "$1.0");
            object result = new DataTable().Compute(promoted, null);
            if (result is DBNull or null) return false;
            value = Convert.ToDouble(result, CultureInfo.InvariantCulture);
        }
        catch
        {
            return false;
        }

        if (double.IsNaN(value) || double.IsInfinity(value)) return false;

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
}
