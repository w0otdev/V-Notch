using System.IO;
using System.Security;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Models;
using VNotch.Services.Clipboard;
using Xunit;

namespace VNotch.Tests;

public sealed class ClipboardImportSecurityTests
{
    [Theory]
    [InlineData(@"\\attacker\share\file.txt")]
    [InlineData("//attacker/share/file.txt")]
    [InlineData("file://attacker/share/file.txt")]
    [InlineData(@"\\?\UNC\attacker\share\file.txt")]
    public async Task NetworkPathsAreRejectedWithoutEnteringTheDiskQueue(string path)
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.History);
        await store.InitializeAsync();
        Task<ClipboardImportResult> import = store.ImportAsync(new ClipboardCapture { FilePaths = [path] }, true);
        Assert.True(import.IsCompleted);
        await Assert.ThrowsAsync<IOException>(() => import);
        Assert.False(Directory.Exists(Path.Combine(fixture.History, "staging")));
        Assert.NotNull((await store.ImportAsync(new ClipboardCapture { Text = "queue remains usable" })).Entry);
    }

    [Theory]
    [InlineData(@"C:\", "Drive_C")]
    [InlineData(@"D:\", "Drive_D")]
    public void DriveRootsHaveExportableNames(string path, string expected)
        => Assert.Equal(expected, ClipboardImportPaths.DisplayName(path));

    [Fact]
    public void CloudTagsAreAllowedAndNameSurrogateTagsAreRejected()
    {
        for (uint i = 0; i < 16; i++) Assert.True(ClipboardImportPaths.IsCloudPlaceholder(0x9000001A | i << 12));
        Assert.True(ClipboardImportPaths.IsCloudPlaceholder(0x80000021));
        Assert.False(ClipboardImportPaths.IsCloudPlaceholder(0xA000000C)); // symbolic link
        Assert.False(ClipboardImportPaths.IsCloudPlaceholder(0xA0000003)); // junction
        Assert.False(ClipboardImportPaths.IsCloudPlaceholder(0x80000042)); // unknown provider
    }

    [Fact]
    public async Task LowDiskSpaceRejectsAnApprovedCopyBeforeCreatingStaging()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.Root, "file.txt"); File.WriteAllText(source, "bytes");
        using var store = new ClipboardHistoryStore(fixture.History, () => 0);
        await store.InitializeAsync();
        await Assert.ThrowsAsync<IOException>(() => store.ImportAsync(new ClipboardCapture { FilePaths = [source] }, true));
        Assert.Empty(store.Snapshot());
        Assert.False(Directory.Exists(Path.Combine(fixture.History, "staging")));
        Assert.Equal("bytes", File.ReadAllText(source));
    }

    [Fact]
    public async Task FolderAboveTheOldLimitKeepsEveryFileAndDeduplicatesAnIdenticalRecopy()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.Root, "folder"); Directory.CreateDirectory(source);
        for (int i = 0; i < 600; i++) File.WriteAllText(Path.Combine(source, i + ".txt"), i.ToString());
        var modified = new DateTime(2024, 1, 2, 3, 4, 6);
        File.SetLastWriteTime(Path.Combine(source, "599.txt"), modified);
        using var store = new ClipboardHistoryStore(fixture.History);
        await store.InitializeAsync();
        var first = (await store.ImportAsync(new ClipboardCapture { FilePaths = [source] })).Entry!;
        var second = (await store.ImportAsync(new ClipboardCapture { FilePaths = [source] })).Entry!;
        Assert.Equal(first.Id, second.Id);
        string export = Assert.Single(await store.ExportFilesAsync(first.Id));
        Assert.Equal(600, Directory.GetFiles(export).Length);
        Assert.Equal("599", File.ReadAllText(Path.Combine(export, "599.txt")));
        Assert.Equal(modified, File.GetLastWriteTime(Path.Combine(export, "599.txt")));
    }

    [Fact]
    public async Task ImportingAParentFolderExcludesHistoryAndItsGrowingStagingArea()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, "file.txt"), "source");
        using var store = new ClipboardHistoryStore(fixture.History);
        await store.InitializeAsync();
        var entry = (await store.ImportAsync(new ClipboardCapture { FilePaths = [fixture.Root] })).Entry!;
        string export = Assert.Single(await store.ExportFilesAsync(entry.Id));
        Assert.Equal("source", File.ReadAllText(Path.Combine(export, "file.txt")));
        Assert.False(Directory.Exists(Path.Combine(export, "history")));
    }

    [Fact]
    public async Task ScreenshotsThatDifferByOnePixelWithinTwoSecondsAreBothKept()
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.History);
        await store.InitializeAsync();
        byte[] pixels = new byte[64 * 64 * 4]; new Random(42).NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var firstCapture = new ClipboardCapture { ImagePng = Encode(pixels), SourceApp = "SnippingTool" };
        var first = (await store.ImportAsync(firstCapture)).Entry!;
        pixels[33 * 64 * 4 + 17 * 4] ^= 1;
        var second = (await store.ImportAsync(firstCapture with { ImagePng = Encode(pixels), CopiedUtc = firstCapture.CopiedUtc.AddMilliseconds(100) })).Entry!;
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, store.Snapshot().Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateExportsDeleteAfterTheLastExternalReaderCloses(bool directory)
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.Root, directory ? "folder" : "secret.txt");
        if (directory) Directory.CreateDirectory(source);
        string sourceFile = directory ? Path.Combine(source, "secret.txt") : source;
        File.WriteAllText(sourceFile, "private bytes");
        using var store = new ClipboardHistoryStore(fixture.History);
        await store.InitializeAsync();
        var entry = (await store.ImportAsync(new ClipboardCapture { FilePaths = [source] })).Entry!;
        using var pin = Pin(); Assert.True(await store.UnlockPersonalAsync(pin, true));
        await store.MovePersonalAsync(entry.Id, true);
        string export = Assert.Single(await store.ExportFilesAsync(entry.Id));
        string file = directory ? Path.Combine(export, "secret.txt") : export;
        using (var external = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            // A recipient that prevents deletion cannot take an indefinite plaintext lease.
            Assert.Throws<IOException>(() => File.OpenRead(file));
            await store.ReleasePersonalExportsAsync([export]);
            using var reader = new StreamReader(external, leaveOpen: true);
            Assert.Equal("private bytes", reader.ReadToEnd());
        }
        Assert.False(File.Exists(file));
        await store.LockPersonalAsync();
        Assert.False(Directory.Exists(Path.Combine(fixture.History, "exports", "personal")));
    }

    [Fact]
    public async Task PersonalImportsPublishOnlyEncryptedRecordsAndRejectALockedVault()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.Root, "secret.txt"); File.WriteAllText(source, "private bytes");
        using var store = new ClipboardHistoryStore(fixture.History);
        await store.InitializeAsync();
        var capture = new ClipboardCapture { FilePaths = [source], TargetCategory = "Personal" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportAsync(capture));
        using var pin = Pin(); Assert.True(await store.UnlockPersonalAsync(pin, true));
        var entry = (await store.ImportAsync(capture)).Entry!;
        Assert.True(entry.IsPersonal);
        Assert.Empty(store.Snapshot());
        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.History, "items")));
        Assert.All(Directory.GetFiles(Path.Combine(fixture.History, "personal", entry.Id.ToString("N"))), path => Assert.EndsWith(".enc", path));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(fixture.History, "staging")));
        Assert.Single(store.Snapshot(true));
    }

    private static byte[] Encode(byte[] pixels)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4)));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
    private static SecureString Pin()
    {
        var pin = new SecureString(); foreach (char c in "123456") pin.AppendChar(c); pin.MakeReadOnly(); return pin;
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "vnotch-import-security-" + Guid.NewGuid().ToString("N"));
        internal string History => Path.Combine(Root, "history");
        internal Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
