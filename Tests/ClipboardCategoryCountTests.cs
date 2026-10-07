using VNotch.Controls;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

public sealed class ClipboardCategoryCountTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(9, "9")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    [InlineData(1234, "99+")]
    public void CountLabelCapsDisplayWithoutChangingCount(int count, string expected)
    {
        var category = new ClipboardCategoryViewModel(TrayIconKind.Files) { Count = count };
        Assert.Equal(expected, category.CountLabel);
        Assert.Equal(count, category.Count);
    }

    [Fact]
    public void AboveLimitDoesNotAnimateUnchangedLabelAndDroppingBelowUpdatesIt()
    {
        var category = new ClipboardCategoryViewModel(TrayIconKind.Files) { Count = 100 };
        int updates = 0;
        category.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(category.CountLabel)) updates++; };
        category.Count = 101;
        Assert.Equal(0, updates);
        category.Count = 99;
        Assert.Equal(1, updates);
        Assert.Equal("99", category.CountLabel);
        category.Count = 100;
        Assert.Equal(2, updates);
        Assert.Equal("99+", category.CountLabel);
    }
}
