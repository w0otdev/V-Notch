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
        Assert.Equal(SpotlightResultKind.Calculation, item.Kind);
        Assert.Equal(expected, double.Parse(item.Title, NumberStyles.Float, CultureInfo.CurrentCulture));
        Assert.Equal(item.Title, item.Target);
        Assert.Equal(CalculatorProvider.ResultScore, item.Score);
    }

    [Theory]
    [InlineData("vi-VN", "2,5")]
    [InlineData("en-US", "2.5")]
    [InlineData("sv-SE", "2,5")]
    public void CopiedResultCanBeUsedInAnotherExpression(string locale, string expected)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(locale);
            Assert.True(CalculatorProvider.TryEvaluate("5/2", out var first));
            Assert.Equal(expected, first!.Target);
            Assert.True(CalculatorProvider.TryEvaluate(first.Target + "*2", out var second));
            Assert.Equal("5", second!.Target);
            Assert.True(CalculatorProvider.TryEvaluate("2.5*2", out _));
            Assert.True(CalculatorProvider.TryEvaluate("0-2.5", out var negative));
            Assert.True(CalculatorProvider.TryEvaluate(negative!.Target + "*2", out var twice));
            Assert.Equal((-5).ToString(CultureInfo.CurrentCulture), twice!.Target);
            Assert.False(CalculatorProvider.TryEvaluate("1,2.3+4", out _));
        }
        finally { CultureInfo.CurrentCulture = original; }
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
