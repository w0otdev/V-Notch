using System.Globalization;
using VNotch.Models;
using VNotch.Services.Spotlight.Providers;
using Xunit;

namespace VNotch.Tests;

public sealed class CalculatorProviderTests
{
    [Theory]
    [InlineData("52*18+3", 939)]
    [InlineData("5/2", 2.5)]
    [InlineData("(2+3)*4", 20)]
    [InlineData("10%3", 1)]
    [InlineData("3-2", 1)]
    [InlineData(" 1 + 1 ", 2)]
    [InlineData("100/8", 12.5)]
    public void TryEvaluate_ComputesArithmeticExpressions(string query, double expected)
    {
        Assert.True(CalculatorProvider.TryEvaluate(query, out SpotlightSearchItem? item));

        Assert.NotNull(item);
        Assert.Equal(SpotlightResultKind.Calculation, item!.Kind);
        Assert.Equal(expected, double.Parse(item.Title, NumberStyles.Float, CultureInfo.CurrentCulture));
        Assert.Equal(item.Title, item.Target);
        Assert.Equal(CalculatorProvider.ResultScore, item.Score);
    }

    [Theory]
    [InlineData("")]
    [InlineData("notepad")]
    [InlineData("2024")]
    [InlineData("-5")]
    [InlineData("7-zip")]
    [InlineData("5+")]
    [InlineData("1..2")]
    [InlineData("()")]
    [InlineData("1/0")]
    [InlineData("50%")]
    public void TryEvaluate_RejectsNonExpressionsAndInvalidMath(string query)
    {
        Assert.False(CalculatorProvider.TryEvaluate(query, out SpotlightSearchItem? item));
        Assert.Null(item);
    }

    [Fact]
    public async Task SearchAsync_ReturnsSingleResultForExpressionsAndNothingOtherwise()
    {
        var provider = new CalculatorProvider();

        var hit = await provider.SearchAsync("6*7", 10, CancellationToken.None);
        var miss = await provider.SearchAsync("terminal", 10, CancellationToken.None);

        Assert.Single(hit);
        Assert.Empty(miss);
    }
}
