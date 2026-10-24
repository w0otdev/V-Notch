using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;

namespace VNotch.Services.Spotlight.Providers;

internal sealed partial class CalculatorProvider : ISpotlightProvider
{
    private const string NativeDll = "VNotch.Native.dll";

    internal const double ResultScore = 1100;

    public bool IsAvailable => true;
    public bool IsInstant => true;

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_calculator_try_evaluate(
        ReadOnlySpan<char> queryPtr,
        int queryLen,
        out double outValue,
        out byte outValid);

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
        if (string.IsNullOrWhiteSpace(query)) return false;

        string expression = query.Trim();
        ReadOnlySpan<char> span = expression.AsSpan();
        vn_calculator_try_evaluate(span, span.Length, out double value, out byte valid);
        if (valid == 0) return false;

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
