using VNotch.Services.Clipboard;
using Xunit;

namespace VNotch.Tests;

public sealed class ClipboardSelectionModelTests
{
    [Fact]
    public void ShiftExtendsFromAnchorAndControlTogglesWithoutDroppingOtherItems()
    {
        string[] items = ["a", "b", "c", "d"];
        var selection = new ClipboardSelectionModel<string>();
        Assert.True(selection.Select(items, [], "b", false, false, false).SetEquals(["b"]));
        var shifted = selection.Select(items, ["b"], "d", false, true, true).ToArray();
        Assert.True(shifted.ToHashSet().SetEquals(["b", "c", "d"]));
        Assert.True(selection.Modified);
        Assert.True(selection.Select(items, shifted, "c", true, false, true).SetEquals(["b", "d"]));
        selection.Reset();
        Assert.Null(selection.Anchor);
        Assert.False(selection.Modified);
    }

    [Fact]
    public void ShrinkingLassoRecomputesTargetsWhileKeepingExtendedSeed()
    {
        string[] items = ["a", "b", "c", "d"];
        var selection = new ClipboardSelectionModel<string>();
        selection.BeginLasso(["d"], extend: true);
        selection.IncludeRange(items, 0, 2);
        Assert.True(selection.Targets.SetEquals(items));
        selection.ResetLassoTargets();
        selection.IncludeRange(items, 1, 1);
        Assert.True(selection.Targets.SetEquals(["b", "d"]));
        selection.EndLasso();
        selection.BeginLasso(["d"], extend: false);
        Assert.Empty(selection.Targets);
    }
}
