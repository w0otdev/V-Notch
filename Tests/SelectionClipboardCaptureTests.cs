using System.IO;
using System.Windows;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class SelectionClipboardCaptureTests
{
    [Fact]
    public void NonSeekableAuxiliaryStreamIsSkippedWithoutConsumingIt() => SharedStaTestRunner.Run(() =>
    {
        using var stream = new ForwardOnlyStream();
        var original = new DataObject();
        original.SetData("forward-only", stream, false);
        original.SetData(DataFormats.UnicodeText, "keep", false);
        var backup = SelectionClipboardCapture.Snapshot(original);
        Assert.False(backup.GetDataPresent("forward-only", false));
        Assert.Equal("keep", backup.GetData(DataFormats.UnicodeText, false));
        Assert.False(stream.ReadAttempted);
    });

    private sealed class ForwardOnlyStream : Stream
    {
        internal bool ReadAttempted;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { ReadAttempted = true; return 0; }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

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
    public void UnsupportedAuxiliaryPayloadDoesNotDiscardText() => SharedStaTestRunner.Run(() =>
    {
        var original = new DataObject();
        original.SetData("opaque-format", new object(), false);
        original.SetData(DataFormats.UnicodeText, "previous text", false);
        var backup = SelectionClipboardCapture.Snapshot(original);
        Assert.Equal("previous text", backup.GetData(DataFormats.UnicodeText, false));
        Assert.False(backup.GetDataPresent("opaque-format", false));
    });

    [Fact]
    public void SnapshotPreservesSystemDrawingImagesAndToleratesNullFormats() => SharedStaTestRunner.Run(() =>
    {
        using var bmp = new System.Drawing.Bitmap(16, 16);
        var original = new DataObject();
        original.SetData(DataFormats.Bitmap, bmp, false);
        original.SetData("empty-auxiliary", null, false);
        var backup = SelectionClipboardCapture.Snapshot(original);
        Assert.NotNull(backup.GetData(DataFormats.Bitmap, false));
    });

    [Theory]
    [InlineData(0u, 100u, true)]
    [InlineData(100u, 100u, true)]
    [InlineData(100u, 200u, false)]
    public void IsOwnerAcceptableHandlesZeroAndProcessComparison(uint owner, uint target, bool expected)
    {
        Assert.Equal(expected, SelectionClipboardCapture.IsOwnerAcceptable(owner, target));
    }
}
