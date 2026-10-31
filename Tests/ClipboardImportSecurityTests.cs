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
        string source = Path.Combine(fixture.Root, "file.txt");
        await File.WriteAllTextAsync(source, "bytes");
        using var store = new ClipboardHistoryStore(fixture.History, () => 0);
        await store.InitializeAsync();
        await Assert.ThrowsAsync<IOException>(() => store.ImportAsync(new ClipboardCapture { FilePaths = [source] }, true));
        Assert.Empty(store.Snapshot());
        Assert.False(Directory.Exists(Path.Combine(fixture.History, "staging")));
        Assert.Equal("bytes", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task FolderAboveTheOldLimitKeepsEveryFileAndDeduplicatesAnIdenticalRecopy()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.Root, "folder"); Directory.CreateDirectory(source);
        for (int i = 0; i < 600; i++) await File.WriteAllTextAsync(Path.Combine(source, i + ".txt"), i.ToString());
        var modified = new DateTime(2024, 1, 2, 3, 4, 6, DateTimeKind.Local);
        File.SetLastWriteTime(Path.Combine(source, "599.txt"), modified);
        using var store = new ClipboardHistoryStore(fixture.History);
        await store.InitializeAsync();
        var first = (await store.ImportAsync(new ClipboardCapture { FilePaths = [source] })).Entry!;
        var second = (await store.ImportAsync(new ClipboardCapture { FilePaths = [source] })).Entry!;
        Assert.Equal(first.Id, second.Id);
        string export = Assert.Single(await store.ExportFilesAsync(first.Id));
        Assert.Equal(600, Directory.GetFiles(export).Length);
        Assert.Equal("599", await File.ReadAllTextAsync(Path.Combine(export, "599.txt")));
        Assert.Equal(modified, File.GetLastWriteTime(Path.Combine(export, "599.txt")));
    }

    [Fact]
    public async Task ImportingAParentFolderExcludesHistoryAndItsGrowingStagingArea()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "file.txt"), "source");
        using var store = new ClipboardHistoryStore(fixture.History);
        await store.InitializeAsync();
        var entry = (await store.ImportAsync(new ClipboardCapture { FilePaths = [fixture.Root] })).Entry!;
        string export = Assert.Single(await store.ExportFilesAsync(entry.Id));
        Assert.Equal("source", await File.ReadAllTextAsync(Path.Combine(export, "file.txt")));
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
        await File.WriteAllTextAsync(sourceFile, "private bytes");
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
            Assert.Equal("private bytes", await reader.ReadToEndAsync());
        }
        Assert.False(File.Exists(file));
        await store.LockPersonalAsync();
        Assert.False(Directory.Exists(Path.Combine(fixture.History, "exports", "personal")));
    }

    [Fact]
    public async Task PersonalImportsPublishOnlyEncryptedRecordsAndRejectALockedVault()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.Root, "secret.txt");
        await File.WriteAllTextAsync(source, "private bytes");
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

    [Fact]
    public void ValidateEntryHandlesBoundaryAndInvalidPathsWithoutCrashing()
    {
        using var fixture = new Fixture();
        string validFile = Path.Combine(fixture.Root, "boundary.txt");
        File.WriteAllText(validFile, "ok");

        var attrs = ClipboardImportPaths.ValidateEntry(validFile);
        Assert.Equal(0, (int)(attrs & FileAttributes.ReparsePoint));

        string longSubDir = Path.Combine(fixture.Root, new string('a', 120), new string('b', 120));
        Directory.CreateDirectory(longSubDir);
        Assert.True((ClipboardImportPaths.ValidateEntry(longSubDir) & FileAttributes.Directory) != 0);

        Assert.Throws<IOException>(() => ClipboardImportPaths.ValidateEntry(@"\\attacker\share\boundary.txt"));
        Assert.Throws<IOException>(() => ClipboardImportPaths.ValidateEntry(""));
    }

    [Fact]
    public async Task UnapprovedImportGrowingPastApprovalThresholdAbortsAndCleansStaging()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.Root, "growing.bin");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);

        bool grew = false;
        using var store = new ClipboardHistoryStore(fixture.History, () =>
        {
            if (!grew)
            {
                grew = true;
                using var fs = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                fs.SetLength(ClipboardHistoryStore.ApprovalThreshold + 4096);
            }
            return long.MaxValue;
        });
        await store.InitializeAsync();

        await Assert.ThrowsAsync<ClipboardImportLimitException>(() =>
            store.ImportAsync(new ClipboardCapture { FilePaths = [source] }, approved: false));

        Assert.Empty(store.Snapshot());
        string stagingRoot = Path.Combine(fixture.History, "staging");
        if (Directory.Exists(stagingRoot))
            Assert.Empty(Directory.GetFileSystemEntries(stagingRoot));
    }

    [Fact]
    public async Task ToctouSymlinkOrJunctionSwapBeforeOpenValidatedReadThrowsIOException()
    {
        using var fixture = new Fixture();
        string targetDir = Path.Combine(fixture.Root, "target");
        Directory.CreateDirectory(targetDir);
        string targetFile = Path.Combine(targetDir, "secret.txt");
        await File.WriteAllTextAsync(targetFile, "redirected secret");

        string workDir = Path.Combine(fixture.Root, "work");
        Directory.CreateDirectory(workDir);
        string sourceFile = Path.Combine(workDir, "secret.txt");
        await File.WriteAllTextAsync(sourceFile, "benign");

        ClipboardImportPaths.ValidateEntry(sourceFile);

        bool replacedWithFileSymlink = false;
        try
        {
            File.Delete(sourceFile);
            File.CreateSymbolicLink(sourceFile, targetFile);
            replacedWithFileSymlink = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Directory.Delete(workDir, true);
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                ArgumentList = { "/c", "mklink", "/J", workDir, targetDir },
                CreateNoWindow = true,
                UseShellExecute = false
            })!;
            await proc.WaitForExitAsync();
            Assert.Equal(0, proc.ExitCode);
        }

        Assert.Throws<IOException>(() =>
        {
            using var stream = ClipboardImportPaths.OpenValidatedRead(sourceFile);
        });
        Assert.True(replacedWithFileSymlink || Directory.Exists(workDir));
        if (replacedWithFileSymlink && File.Exists(sourceFile)) File.Delete(sourceFile);
        else if (Directory.Exists(workDir) && (File.GetAttributes(workDir) & FileAttributes.ReparsePoint) != 0) Directory.Delete(workDir, false);
    }

    [Fact]
    public void ExtractArchiveDirectoryHandlesTrailingSeparatorAndRejectsNtfsAlternateDataStream()
    {
        using var fixture = new Fixture();

        using var validZipStream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(validZipStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("sub/hello.txt").Open());
            writer.Write("hello world");
        }
        validZipStream.Position = 0;

        string destWithTrailingSep = Path.Combine(fixture.Root, "extracted") + Path.DirectorySeparatorChar;
        ClipboardHistoryStore.ExtractArchiveDirectory(validZipStream, destWithTrailingSep, null);
        Assert.Equal("hello world", File.ReadAllText(Path.Combine(fixture.Root, "extracted", "sub", "hello.txt")));

        using var adsZipStream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(adsZipStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("test.txt:ads").Open());
            writer.Write("hidden ads payload");
        }
        adsZipStream.Position = 0;

        string adsDest = Path.Combine(fixture.Root, "ads-extracted");
        Assert.Throws<InvalidDataException>(() =>
            ClipboardHistoryStore.ExtractArchiveDirectory(adsZipStream, adsDest, null));
    }

    private static byte[] Encode(byte[] pixels)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4)));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
    private static SecureString Pin()
    {
        var pin = new SecureString();
        foreach (char c in "123456") pin.AppendChar(c);
        pin.MakeReadOnly();
        return pin;
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "vnotch-import-security-" + Guid.NewGuid().ToString("N"));
        internal string History => Path.Combine(Root, "history");
        internal Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
