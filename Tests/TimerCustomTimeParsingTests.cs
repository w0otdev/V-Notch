using System;
using VNotch.Controllers;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

public class TimerCustomTimeParsingTests
{
    [Theory]
    [InlineData("1e20")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("NaN")]
    [InlineData("99999999999999999999d")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("0:00")]
    public void TryParseCustomTime_RejectsUnrepresentableOrNonPositive(string input)
    {
        var vm = new TimerViewModel();

        bool parsed = vm.TryParseCustomTime(input, out TimeSpan result);

        Assert.False(parsed);
        Assert.Equal(TimeSpan.Zero, result);
    }

    [Theory]
    [InlineData("99999999:00")]
    [InlineData("99999999:00:00")]
    [InlineData("2147483647:59:59")]
    public void TryParseCustomTime_HugeColonInputs_DoNotThrow(string input)
    {
        var vm = new TimerViewModel();

        bool parsed = vm.TryParseCustomTime(input, out TimeSpan result);

        if (parsed)
            Assert.True(result > TimeSpan.Zero);
        else
            Assert.Equal(TimeSpan.Zero, result);
    }

    [Theory]
    [InlineData("5", 300)]
    [InlineData("1.5", 90)]
    [InlineData("2:30", 150)]
    [InlineData("1:02:03", 3723)]
    [InlineData("1h 30m", 5400)]
    [InlineData("45s", 45)]
    public void TryParseCustomTime_ValidInputs(string input, double expectedSeconds)
    {
        var vm = new TimerViewModel();

        bool parsed = vm.TryParseCustomTime(input, out TimeSpan result);

        Assert.True(parsed);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    public void TryParseCustomTime_EmptyOrGarbage_ReturnsFalse(string? input)
    {
        var vm = new TimerViewModel();

        Assert.False(vm.TryParseCustomTime(input, out _));
    }
}

public class MagnifierCaptureSourceDisposeTests
{
    [Fact]
    public void Dispose_WithoutInitialize_IsIdempotent()
    {
        var source = new MagnifierCaptureSource();

        source.Dispose();
        var ex = Record.Exception(source.Dispose);

        Assert.Null(ex);
        Assert.False(source.IsReady);
    }
}
