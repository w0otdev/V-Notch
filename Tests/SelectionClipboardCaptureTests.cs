using System.IO;
using System.Windows;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class SelectionClipboardCaptureTests
{
    [Theory]
    [InlineData(10u, 11u, 11u, true, "selected", true)]
    [InlineData(10u, 10u, 10u, true, "old clipboard", false)]
    [InlineData(10u, 11u, 12u, true, "newer copy", false)]
    [InlineData(10u, 11u, 11u, false, "old window", false)]
    [InlineData(10u, 11u, 11u, true, "  ", false)]
    public void OnlyFreshStableCopyFromCurrentRequestIsAccepted(uint before, uint copied, uint current, bool focused, string text, bool expected)
        => Assert.Equal(expected, SelectionClipboardCapture.AcceptCopy(before, copied, current, focused, text));

    [Fact]
    public void OversizeTextIsNotSentToTranslation()
        => Assert.False(SelectionClipboardCapture.AcceptCopy(1, 2, 2, true, new string('x', 2001)));

    [Fact]
    public void BackupMaterializesTextHtmlAndBinaryFormatsWithoutSharingStreams() => SharedStaTestRunner.Run(() =>
    {
        using var stream = new MemoryStream([1, 2, 3]);
        stream.Position = 2;
        var original = new DataObject();
        original.SetData(DataFormats.UnicodeText, "previous text", false);
        original.SetData(DataFormats.Html, "<b>previous text</b>", false);
        original.SetData("custom-binary", stream, false);
        var backup = SelectionClipboardCapture.Snapshot(original);
        stream.SetLength(0);
        Assert.Equal("previous text", backup.GetData(DataFormats.UnicodeText, false));
        Assert.Equal("<b>previous text</b>", backup.GetData(DataFormats.Html, false));
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<MemoryStream>(backup.GetData("custom-binary", false)).ToArray());
    });

    [Fact]
    public void UnsupportedClipboardPayloadAbortsBeforeCopyInsteadOfLosingIt() => SharedStaTestRunner.Run(() =>
    {
        var original = new DataObject();
        original.SetData("opaque-format", new object(), false);
        Assert.Throws<NotSupportedException>(() => SelectionClipboardCapture.Snapshot(original));
    });
}
