using System.IO;
using System.Security.Cryptography;
using System.Text;
using VNotch.Models;
using VNotch.Services.Clipboard;
using Xunit;

namespace VNotch.Tests;

public sealed class ClipboardHistoryTests
{
    [Fact]
    public async Task CorruptImagesHaveStableDistinctFingerprintsAndDoNotBreakLaterImports()
    {
        using var fixture = new Fixture();
        using var store = fixture.Store();
        await store.InitializeAsync();
        byte[] invalid = [137, 80, 78, 71, 0];
        var first = (await store.ImportAsync(new ClipboardCapture { ImagePng = invalid })).Entry!;
        var duplicate = (await store.ImportAsync(new ClipboardCapture { ImagePng = invalid.ToArray() })).Entry!;
        var different = (await store.ImportAsync(new ClipboardCapture { ImagePng = [137, 80, 78, 71, 1] })).Entry!;
        Assert.Equal(first.Id, duplicate.Id);
        Assert.NotEqual(first.Fingerprint, different.Fingerprint);
        Assert.Equal("image-raw:" + Convert.ToHexString(SHA256.HashData(invalid)), first.Fingerprint);
        Assert.NotNull((await store.ImportAsync(new ClipboardCapture { Text = "after broken image" })).Entry);
        Assert.Equal(3, store.Snapshot().Count);
    }

    [Fact]
    public async Task HistoryPersistsAndRecopyPreservesManualCategoriesAndPin()
    {
        using var fixture = new Fixture();
        Guid id;
        using (var store = fixture.Store())
        {
            await store.InitializeAsync();
            var entry = (await store.ImportAsync(new ClipboardCapture { Text = "project agenda" })).Entry!;
            id = entry.Id;
            await store.UpdateAsync(id, pinned: true, groups: ["Ideas"]);
            await store.ImportAsync(new ClipboardCapture { Text = "project agenda" });
            Assert.Single(store.Snapshot());
        }
        using var reopened = fixture.Store();
        await reopened.InitializeAsync();
        var restored = Assert.Single(reopened.Snapshot());
        Assert.Equal(id, restored.Id);
        Assert.True(restored.IsPinned);
        Assert.Equal(new[] { "Ideas" }, restored.Groups);
        Assert.Equal("project agenda", (await reopened.GetContentAsync(id)).Text);
    }

    [Fact]
    public async Task SearchReadsFullTextAndHonorsCategoryCorrectionAndArchive()
    {
        using var fixture = new Fixture();
        using var store = fixture.Store();
        await store.InitializeAsync();
        var entry = (await store.ImportAsync(new ClipboardCapture { Text = new string('a', 1200) + " unique-tail" })).Entry!;
        Assert.Single(await store.SearchAsync("unique-tail", "Text"));
        await store.UpdateAsync(entry.Id, groups: ["Work", "Invented"], archived: true);
        Assert.Empty(await store.SearchAsync("", "All"));
        Assert.Empty(await store.SearchAsync("", "Text"));
        Assert.Empty(await store.SearchAsync("", "Work"));
        Assert.Single(await store.SearchAsync("unique-tail", "Archive"));
        Assert.Single(await store.SearchAsync("unique-tail @text @work", "Archive"));
        Assert.Equal(1, store.GetCategoryCounts()["Archive"]);
        Assert.False(store.GetCategoryCounts().ContainsKey("Text"));
        Assert.False(store.GetCategoryCounts().ContainsKey("Work"));
        Assert.Equal(new[] { "Work" }, (await store.GetContentAsync(entry.Id)).Entry.Groups);
        await store.UpdateAsync(entry.Id, archived: false);
        Assert.Single(await store.SearchAsync("", "Work"));
    }

    [Fact]
    public async Task CopiesOwnFileBytesAndFoldersIncludingEmptyFolders()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.Root, "source");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.WriteAllText(Path.Combine(source, "note.txt"), "original bytes");
        using var store = fixture.Store();
        await store.InitializeAsync();
        var entry = (await store.ImportAsync(new ClipboardCapture { FilePaths = [source] })).Entry!;
        Directory.Delete(source, true);
        var exported = Assert.Single(await store.ExportFilesAsync(entry.Id));
        Assert.True(Directory.Exists(Path.Combine(exported, "empty")));
        Assert.Equal("original bytes", File.ReadAllText(Path.Combine(exported, "note.txt")));
    }

    [Fact]
    public async Task AggregateCopiesOverFiftyMiBNeedApprovalBeforeCommitting()
    {
        using var fixture = new Fixture();
        string first = Path.Combine(fixture.Root, "one.bin"), second = Path.Combine(fixture.Root, "two.bin");
        using (var stream = File.Create(first)) stream.SetLength(26L * 1024 * 1024);
        using (var stream = File.Create(second)) stream.SetLength(26L * 1024 * 1024);
        using var store = fixture.Store();
        await store.InitializeAsync();
        var capture = new ClipboardCapture { FilePaths = [first, second] };
        var pending = await store.ImportAsync(capture);
        Assert.True(pending.NeedsApproval);
        Assert.Equal(52L * 1024 * 1024, pending.ByteSize);
        Assert.Empty(store.Snapshot());
        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.HistoryRoot, "items")));
        var approved = await store.ImportAsync(capture, approved: true);
        Assert.NotNull(approved.Entry);
        Assert.Equal(2, (await store.GetContentAsync(approved.Entry!.Id)).Files.Length);
    }

    [Fact]
    public async Task RetentionKeepsPinArchiveAndPersonalAndRemovesOtherThirtyDayCopies()
    {
        using var fixture = new Fixture();
        using var store = fixture.Store();
        await store.InitializeAsync();
        var plain = (await store.ImportAsync(new ClipboardCapture { Text = "plain" })).Entry!;
        var pin = (await store.ImportAsync(new ClipboardCapture { Text = "pin" })).Entry!;
        var archive = (await store.ImportAsync(new ClipboardCapture { Text = "archived" })).Entry!;
        var personal = (await store.ImportAsync(new ClipboardCapture { Text = "private" })).Entry!;
        await store.UpdateAsync(pin.Id, pinned: true);
        await store.UpdateAsync(archive.Id, archived: true);
        Assert.True(await UnlockAsync(store, "123456", create: true));
        await store.MovePersonalAsync(personal.Id, true);
        await store.PruneAsync(DateTime.UtcNow.AddDays(31));
        Assert.Equal(new[] { pin.Id, archive.Id }.Order(), store.Snapshot().Select(entry => entry.Id).Order());
        Assert.Equal(personal.Id, Assert.Single(store.Snapshot(personal: true)).Id);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.GetContentAsync(plain.Id));
    }

    [Fact]
    public async Task PersonalEncryptsMetadataAndAttachmentLocksAndDeletesTemporaryExports()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.Root, "secret.txt");
        const string secret = "highly confidential note 98342";
        File.WriteAllText(source, secret);
        Guid id;
        using (var store = fixture.Store())
        {
            await store.InitializeAsync();
            var entry = (await store.ImportAsync(new ClipboardCapture { Text = secret, FilePaths = [source] })).Entry!;
            id = entry.Id;
            Assert.False(await UnlockAsync(store, "12345", true));
            Assert.True(await UnlockAsync(store, "123456", true));
            await store.MovePersonalAsync(id, true);
            Assert.Empty(store.Snapshot());
            Assert.Single(await store.SearchAsync("secret", "Personal"));
            Assert.False(Directory.Exists(Path.Combine(fixture.HistoryRoot, "items", id.ToString("N"))));
            foreach (string path in Directory.GetFiles(Path.Combine(fixture.HistoryRoot, "personal"), "*", SearchOption.AllDirectories))
            {
                Assert.EndsWith(".enc", path);
                Assert.DoesNotContain(secret, Encoding.UTF8.GetString(File.ReadAllBytes(path)));
            }
            string export = Assert.Single(await store.ExportFilesAsync(id));
            Assert.Equal(secret, Encoding.UTF8.GetString(ReadProtectedExport(export)));
            var locking = store.LockPersonalAsync();
            Assert.False(store.IsPersonalUnlocked);
            await locking;
            Assert.False(File.Exists(export));
            Assert.Empty(await store.SearchAsync("", "Personal"));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => store.GetContentAsync(id));
        }
        using var reopened = fixture.Store();
        await reopened.InitializeAsync();
        Assert.True(await UnlockAsync(reopened, "123456"));
        Assert.Equal(secret, (await reopened.GetContentAsync(id)).Text);
        await reopened.MovePersonalAsync(id, false);
        Assert.Single(reopened.Snapshot());
        Assert.Equal(secret, File.ReadAllText(Assert.Single(await reopened.ExportFilesAsync(id))));
    }

    [Fact]
    public async Task WrongPinTamperingAndResetCannotExposePersonal()
    {
        using var fixture = new Fixture();
        Guid id;
        using (var store = fixture.Store())
        {
            await store.InitializeAsync();
            id = (await store.ImportAsync(new ClipboardCapture { Text = "private secret" })).Entry!.Id;
            Assert.True(await UnlockAsync(store, "123456", true));
            await store.MovePersonalAsync(id, true);
        }
        using var reopened = fixture.Store();
        await reopened.InitializeAsync();
        Assert.False(await UnlockAsync(reopened, "654321"));
        Assert.Empty(reopened.Snapshot(true));
        // Another vault instance verifies integrity without waiting out the intentional retry delay.
        using (var intact = fixture.Store())
        {
            await intact.InitializeAsync();
            Assert.True(await UnlockAsync(intact, "123456"));
            string content = Path.Combine(fixture.HistoryRoot, "personal", id.ToString("N"), "content.json.enc");
            byte[] bytes = File.ReadAllBytes(content); bytes[^1] ^= 1; File.WriteAllBytes(content, bytes);
            await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => intact.GetContentAsync(id));
        }
        await reopened.ResetPersonalAsync();
        Assert.False(reopened.HasPersonalPasscode);
        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.HistoryRoot, "personal")));
        Assert.True(await UnlockAsync(reopened, "222222", true));
        Assert.Empty(reopened.Snapshot(true));
    }

    [Fact]
    public async Task PersonalImageRoundTripsAndEncryptedFileRejectsTruncation()
    {
        using var fixture = new Fixture();
        using var store = fixture.Store();
        await store.InitializeAsync();
        byte[] image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var picture = (await store.ImportAsync(new ClipboardCapture { ImagePng = image })).Entry!;
        Assert.True(await UnlockAsync(store, "123456", true));
        await store.MovePersonalAsync(picture.Id, true);
        Assert.Equal(image, await store.GetImageAsync(picture.Id));
        await store.MovePersonalAsync(picture.Id, false);
        Assert.Equal(image, await store.GetImageAsync(picture.Id));
        string source = Path.Combine(fixture.Root, "chunks.bin");
        File.WriteAllBytes(source, RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 79));
        var file = (await store.ImportAsync(new ClipboardCapture { FilePaths = [source] })).Entry!;
        await store.MovePersonalAsync(file.Id, true);
        Assert.Equal(File.ReadAllBytes(source), ReadProtectedExport(Assert.Single(await store.ExportFilesAsync(file.Id))));
        string encrypted = Path.Combine(fixture.HistoryRoot, "personal", file.Id.ToString("N"), "0.bin.enc");
        using (var stream = new FileStream(encrypted, FileMode.Open, FileAccess.Write)) stream.SetLength(stream.Length - 1);
        await Assert.ThrowsAsync<CryptographicException>(() => store.ExportFilesAsync(file.Id));
    }

    [Theory]
    [InlineData("https://example.com", ClipboardKind.Link)]
    [InlineData("#1F4E79", ClipboardKind.Color)]
    [InlineData("#000000", ClipboardKind.Color)]
    [InlineData("#000", ClipboardKind.Color)]
    [InlineData("#111111", ClipboardKind.Color)]
    [InlineData("#123456", ClipboardKind.Color)]
    [InlineData("#123", ClipboardKind.Color)]
    [InlineData("const palette = { ink: '#1F4E79' };", ClipboardKind.Color)]
    [InlineData("Màu chủ đạo: #aBc;", ClipboardKind.Color)]
    [InlineData("#1234", ClipboardKind.Color)]
    [InlineData("#12345678", ClipboardKind.Color)]
    [InlineData("#12345", ClipboardKind.Text)]
    [InlineData("#1234567", ClipboardKind.Text)]
    [InlineData("#123456789", ClipboardKind.Text)]
    [InlineData("#GGFFFF", ClipboardKind.Text)]
    [InlineData("#FF0000 #00FF00", ClipboardKind.Text)]
    [InlineData("#abc #abc", ClipboardKind.Text)]
    [InlineData("FF0000", ClipboardKind.Text)]
    [InlineData("https://example.com/#abc", ClipboardKind.Link)]
    [InlineData("plain text", ClipboardKind.Text)]
    public void OfflineClassificationRecognizesContent(string text, ClipboardKind expected)
    {
        Assert.Equal(expected, ClipboardClassifier.Detect(new ClipboardCapture { Text = text }));
    }

    [Theory]
    [InlineData("#123", 0xFF112233u)]
    [InlineData("#abcd", 0xDDAABBCCu)]
    [InlineData("#a1b2c3", 0xFFA1B2C3u)]
    [InlineData("#12345680", 0x80123456u)]
    public void HexColorsUseCorrectChannelsAndTrailingAlpha(string text, uint expected)
    {
        Assert.True(ClipboardClassifier.TryParseColor(text, out uint argb));
        Assert.Equal(expected, argb);
        var card = new VNotch.ViewModels.ClipboardCardViewModel(new ClipboardEntry { Kind = ClipboardKind.Color, Preview = text });
        var color = Assert.IsType<System.Windows.Media.SolidColorBrush>(card.CardBrush).Color;
        Assert.Equal(expected, ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B);
    }

    [Fact]
    public async Task SingleEmbeddedColorUsesColorCategoryAndPreservesCopiedText()
    {
        using var fixture = new Fixture();
        using var store = fixture.Store();
        await store.InitializeAsync();
        string text = new string('x', 1100) + " color: #a1B2c3;";
        var entry = (await store.ImportAsync(new ClipboardCapture { Text = text })).Entry!;
        Assert.Equal(ClipboardKind.Color, entry.Kind);
        Assert.Equal("#A1B2C3", entry.Preview);
        Assert.Equal(text, (await store.GetContentAsync(entry.Id)).Text);
        Assert.Equal(entry.Id, Assert.Single(await store.SearchAsync("", "Colors")).Id);
    }

    [Fact]
    public void OfflineCategoriesUseOnlyFixedGroupsAndNeverSelectPersonal()
    {
        Assert.Contains("Receipts", ClipboardClassifier.Classify("Invoice subtotal 200", ClipboardKind.Text));
        Assert.Contains("Email", ClipboardClassifier.Classify("me@example.com", ClipboardKind.Text));
        Assert.Contains("Ideas", ClipboardClassifier.Classify("ý tưởng mới", ClipboardKind.Text));
        Assert.DoesNotContain("Personal", ClipboardClassifier.Classify("private personal password", ClipboardKind.Text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExcessiveTopLevelImportIsRejectedBeforeAccessingPaths(bool approved)
    {
        using var fixture = new Fixture();
        using var store = fixture.Store();
        await store.InitializeAsync();
        var capture = new ClipboardCapture
        {
            FilePaths = Enumerable.Repeat(Path.Combine(fixture.Root, "does-not-exist"), ClipboardHistoryStore.MaxImportItems + 1).ToArray()
        };
        await Assert.ThrowsAsync<ClipboardImportLimitException>(() => store.ImportAsync(capture, approved));
        Assert.Empty(store.Snapshot());
        Assert.False(Directory.Exists(Path.Combine(fixture.HistoryRoot, "staging")));
        Assert.NotNull((await store.ImportAsync(new ClipboardCapture { Text = "queue still works" })).Entry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleFoldersWithMoreThan512CombinedItemsCanBeImported(bool approved)
    {
        using var fixture = new Fixture();
        string first = Path.Combine(fixture.Root, "first"), second = Path.Combine(fixture.Root, "second");
        for (int i = 0; i < ClipboardHistoryStore.MaxImportItems / 2; i++)
        {
            Directory.CreateDirectory(Path.Combine(first, i.ToString()));
            Directory.CreateDirectory(Path.Combine(second, i.ToString()));
        }
        using var store = fixture.Store();
        await store.InitializeAsync();
        var result = await store.ImportAsync(new ClipboardCapture { FilePaths = [first, second] }, approved);
        Assert.NotNull(result.Entry);
        Assert.Single(store.Snapshot());
    }

    [Fact]
    public async Task FolderAtImportLimitStillPreservesEmptyDirectories()
    {
        using var fixture = new Fixture();
        string folder = Path.Combine(fixture.Root, "folder");
        for (int i = 0; i < ClipboardHistoryStore.MaxImportItems - 1; i++)
            Directory.CreateDirectory(Path.Combine(folder, i.ToString()));
        using var store = fixture.Store();
        await store.InitializeAsync();
        var entry = (await store.ImportAsync(new ClipboardCapture { FilePaths = [folder] })).Entry!;
        string export = Assert.Single(await store.ExportFilesAsync(entry.Id));
        Assert.Equal(ClipboardHistoryStore.MaxImportItems - 1, Directory.GetDirectories(export).Length);
    }

    [Fact]
    public async Task RapidGroupUpdatesPersistInSubmissionOrder()
    {
        using var fixture = new Fixture();
        Guid id;
        using (var store = fixture.Store())
        {
            await store.InitializeAsync();
            id = (await store.ImportAsync(new ClipboardCapture { Text = "manual grouping" })).Entry!.Id;
            var observed = new List<string[]>();
            store.Changed += () => observed.Add(Assert.Single(store.Snapshot()).Groups);
            Task first = store.UpdateAsync(id, groups: ["Work"]);
            Task second = store.UpdateAsync(id, groups: ["Work", "Ideas"]);
            Task last = store.UpdateAsync(id, groups: ["Ideas"]);
            await Task.WhenAll(first, second, last);
            Assert.Equal(3, observed.Count);
            Assert.Equal(new[] { "Work" }, observed[0]);
            Assert.Equal(new[] { "Work", "Ideas" }, observed[1]);
            Assert.Equal(new[] { "Ideas" }, observed[2]);
        }
        using var reopened = fixture.Store();
        await reopened.InitializeAsync();
        Assert.Equal(new[] { "Ideas" }, Assert.Single(reopened.Snapshot()).Groups);
    }

    [Fact]
    public async Task ConsecutiveScreenshotBitmapAndTempFileImportsDeduplicateIntoSingleEntry()
    {
        using var fixture = new Fixture();
        using var store = fixture.Store();
        await store.InitializeAsync();

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(120, 80, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        byte[] png = ms.ToArray();

        var firstCapture = new ClipboardCapture
        {
            ImagePng = png,
            SourceApp = "svchost",
            CopiedUtc = DateTime.UtcNow
        };
        var firstResult = await store.ImportAsync(firstCapture);
        Assert.NotNull(firstResult.Entry);

        string tempScreenshot = Path.Combine(fixture.Root, $"{{{Guid.NewGuid():D}}}.png");
        File.WriteAllBytes(tempScreenshot, png);

        var secondCapture = new ClipboardCapture
        {
            FilePaths = [tempScreenshot],
            SourceApp = "svchost",
            CopiedUtc = firstCapture.CopiedUtc.AddMilliseconds(140)
        };
        var secondResult = await store.ImportAsync(secondCapture);

        Assert.NotNull(secondResult.Entry);
        Assert.Equal(firstResult.Entry.Id, secondResult.Entry.Id);
        Assert.Single(store.Snapshot());
    }

    private static async Task<bool> UnlockAsync(ClipboardHistoryStore store, string digits, bool create = false)
    {
        using var pin = new System.Security.SecureString();
        foreach (char digit in digits) pin.AppendChar(digit);
        pin.MakeReadOnly();
        return await store.UnlockPersonalAsync(pin, create);
    }
    private static byte[] ReadProtectedExport(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "vnotch-clipboard-test-" + Guid.NewGuid().ToString("N"));
        internal string HistoryRoot => Path.Combine(Root, "history");
        internal Fixture() => Directory.CreateDirectory(Root);
        internal ClipboardHistoryStore Store() => new(HistoryRoot);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
