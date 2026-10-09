using System.Windows;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class SelectionTextReaderTests
{
    [Fact]
    public void MultipleSelectedRangesPreserveOrderAndExcludeEmptyCarets()
        => Assert.Equal("first\nsecond", SelectionTextReader.JoinSelection(["first", "", "second"]));

    [Fact]
    public void CombinedSelectionLimitDoesNotTruncateOrTranslateOnlyTheFirstRange()
    {
        Assert.Null(SelectionTextReader.JoinSelection([new string('a', 1500), new string('b', 600)]));
        Assert.Null(SelectionTextReader.JoinSelection(["", " "]));
    }

    [Fact]
    public void InvalidProviderRectanglesAreRejected()
    {
        Assert.False(SelectionTextReader.ValidBounds(Rect.Empty));
        Assert.False(SelectionTextReader.ValidBounds(new Rect(0, 0, 0, 10)));
        Assert.False(SelectionTextReader.ValidBounds(new Rect(double.PositiveInfinity, 0, 10, 10)));
        Assert.True(SelectionTextReader.ValidBounds(new Rect(-1200, 50, 300, 20)));
    }

    [Fact]
    public void NativeWindowsEditReadsOnlySelectedUnicodeText() => SharedStaTestRunner.Run(() =>
    {
        using var box = new System.Windows.Forms.TextBox { Text = "prefix Tiếng Việt suffix" };
        _ = box.Handle;
        box.Select(7, 10);
        Assert.Equal("Tiếng Việt", NativeSelectionReader.ReadControl(box.Handle));
        box.Select(0, 0);
        Assert.Null(NativeSelectionReader.ReadControl(box.Handle));
    });

    [Fact]
    public void NativeWindowsPasswordEditNeverReturnsSelection() => SharedStaTestRunner.Run(() =>
    {
        using var box = new System.Windows.Forms.TextBox { Text = "private", UseSystemPasswordChar = true };
        _ = box.Handle;
        box.SelectAll();
        Assert.Null(NativeSelectionReader.ReadControl(box.Handle));
    });

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(2, 2)]
    [InlineData(0, 100)]
    public void NativeRangesMustStillMatchTheReturnedText(int start, int end)
        => Assert.Null(NativeSelectionReader.Slice("short", start, end));

    [Theory]
    [InlineData("WindowsTerminal", true)]
    [InlineData("pwsh", true)]
    [InlineData("cmd", true)]
    [InlineData("Telegram", false)]
    [InlineData("chrome", false)]
    public void TerminalCopyDoesNotUseTheInterruptShortcut(string process, bool expected)
        => Assert.Equal(expected, SelectionClipboardCapture.UsesTerminalCopy(process));
}
