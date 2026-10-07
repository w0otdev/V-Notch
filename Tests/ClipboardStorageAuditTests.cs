using System.IO;
using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using VNotch.Models;
using VNotch.Services.Clipboard;
using Xunit;

namespace VNotch.Tests;

public sealed class ClipboardStorageAuditTests
{
    [Fact]
    public async Task FailedAndCanceledDiskWorkDoesNotPoisonQueuedImportsOrUnlock()
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.History);
        await store.InitializeAsync();
        var entry = (await store.ImportAsync(new ClipboardCapture { Text = "first" })).Entry!;
        Task failure = store.GetContentAsync(Guid.NewGuid());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Task cancellation = store.GetImageAsync(entry.Id, canceled.Token);
        Task<ClipboardImportResult> following = store.ImportAsync(new ClipboardCapture { Text = "following" });
        using var pin = Pin();
        Task<bool> unlock = store.UnlockPersonalAsync(pin, true);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => failure);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellation);
        Assert.NotNull((await following.WaitAsync(TimeSpan.FromSeconds(15))).Entry);
        Assert.True(await unlock.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(2, (await store.SearchAsync("", "All")).Count);
    }

    [Fact]
    public async Task DisposeAfterAFaultedOperationStillCompletes()
    {
        using var fixture = new Fixture();
        var store = new ClipboardHistoryStore(fixture.History);
        await store.InitializeAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.GetContentAsync(Guid.NewGuid()));
        await Task.Run(store.Dispose).WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectPersonalImportsRoundTripAndKeepOnlyCiphertext(bool directory)
    {
        using var fixture = new Fixture();
        const string secret = "confidential document 493877";
        string source = Path.Combine(fixture.Root, directory ? "folder" : "source.enc");
        string file = directory ? Path.Combine(source, "nested", "source.enc") : source;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (directory) Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.WriteAllText(file, secret);
        Guid id;
        using (var store = new ClipboardHistoryStore(fixture.History))
        {
            await store.InitializeAsync();
            using var pin = Pin(); Assert.True(await store.UnlockPersonalAsync(pin, true));
            var capture = new ClipboardCapture { FilePaths = [source], TargetCategory = "Personal" };
            var entry = (await store.ImportAsync(capture)).Entry!;
            id = entry.Id;
            Assert.Equal(Encoding.UTF8.GetByteCount(secret), entry.ByteSize);
            Assert.Equal(id, (await store.ImportAsync(capture)).Entry!.Id);
            Assert.Single(store.Snapshot(true));
            AssertEncryptedTree(fixture.History, id, secret);
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(fixture.History, "staging")));
        }
        using var reopened = new ClipboardHistoryStore(fixture.History);
        await reopened.InitializeAsync();
        using var reopenedPin = Pin(); Assert.True(await reopened.UnlockPersonalAsync(reopenedPin));
        string exported = Assert.Single(await reopened.ExportFilesAsync(id));
        string exportedFile = directory ? Path.Combine(exported, "nested", "source.enc") : exported;
        Assert.Equal(secret, Encoding.UTF8.GetString(ReadExport(exportedFile)));
        if (directory) Assert.True(Directory.Exists(Path.Combine(exported, "empty")));
        await reopened.MovePersonalAsync(id, false);
        string publicExport = Assert.Single(await reopened.ExportFilesAsync(id));
        Assert.Equal(secret, File.ReadAllText(directory ? Path.Combine(publicExport, "nested", "source.enc") : publicExport));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectPersonalImagesAndFilePreviewsAreEncrypted(bool fileCapture)
    {
        using var fixture = new Fixture();
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        string source = Path.Combine(fixture.Root, "secret.png");
        File.WriteAllBytes(source, png);
        using var store = new ClipboardHistoryStore(fixture.History);
        await store.InitializeAsync();
        using var pin = Pin(); Assert.True(await store.UnlockPersonalAsync(pin, true));
        var capture = fileCapture
            ? new ClipboardCapture { FilePaths = [source], TargetCategory = "Personal" }
            : new ClipboardCapture { ImagePng = png, TargetCategory = "Personal" };
        var entry = (await store.ImportAsync(capture)).Entry!;
        Assert.True(entry.IsPersonal);
        Assert.Equal(ClipboardKind.Image, entry.Kind);
        Assert.NotEmpty((await store.GetImageAsync(entry.Id))!);
        AssertEncryptedTree(fixture.History, entry.Id, "secret.png");
        Assert.Equal(png, ReadExport(Assert.Single(await store.ExportFilesAsync(entry.Id))));
    }

    [Fact]
    public async Task FailedPersonalImportPreservesSourcesAndPreviouslyCommittedHistory()
    {
        using var fixture = new Fixture();
        string first = Path.Combine(fixture.Root, "first.txt"), second = Path.Combine(fixture.Root, "second.txt");
        File.WriteAllText(first, "first source"); File.WriteAllText(second, "second source");
        FileStream? blocked = null;
        bool blockCopy = false;
        using var store = new ClipboardHistoryStore(fixture.History, () =>
        {
            if (blockCopy) return 0;
            return long.MaxValue;
        });
        await store.InitializeAsync();
        using var pin = Pin(); Assert.True(await store.UnlockPersonalAsync(pin, true));
        var previous = (await store.ImportAsync(new ClipboardCapture { Text = "saved private note", TargetCategory = "Personal" })).Entry!;
        blockCopy = true;
        try
        {
            await Assert.ThrowsAsync<IOException>(() => store.ImportAsync(new ClipboardCapture { FilePaths = [first, second], TargetCategory = "Personal" }));
        }
        finally { blocked?.Dispose(); blockCopy = false; }
        Assert.Equal("first source", File.ReadAllText(first));
        Assert.Equal("second source", File.ReadAllText(second));
        Assert.Equal(previous.Id, Assert.Single(store.Snapshot(true)).Id);
        Assert.Equal("saved private note", (await store.GetContentAsync(previous.Id)).Text);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(fixture.History, "staging")));
        Assert.NotNull((await store.ImportAsync(new ClipboardCapture { Text = "retry succeeds", TargetCategory = "Personal" })).Entry);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024 * 1024 + 79)]
    public void StreamingEncryptionKeepsExistingVaultFormatAndUsesOnlyEncryptedScratch(int length)
    {
        using var fixture = new Fixture();
        using var vault = new ClipboardVault(fixture.History);
        using var pin = Pin(); Assert.True(vault.Unlock(pin, true));
        byte[] bytes = RandomNumberGenerator.GetBytes(length);
        string destination = Path.Combine(fixture.History, "attachment.bin.enc");
        vault.WriteEncryptedFile(destination, output =>
        {
            output.Write(bytes);
            output.Flush();
            Assert.False(File.Exists(destination));
            Assert.EndsWith(".enc", Assert.Single(Directory.GetFiles(fixture.History, "*.staging.enc")));
            Assert.False(File.Exists(Path.Combine(fixture.History, "attachment.bin")));
            return bytes.Length;
        });
        Assert.Empty(Directory.GetFiles(fixture.History, "*.staging.enc"));
        Assert.Equal("VNC1", Encoding.ASCII.GetString(File.ReadAllBytes(destination), 0, 4));
        using var restored = new MemoryStream();
        using var input = File.OpenRead(destination);
        vault.TransformFile(input, restored, false);
        Assert.Equal(bytes, restored.ToArray());
    }

    [Fact]
    public void FailedStreamingWriterDoesNotPublishAnAttachment()
    {
        using var fixture = new Fixture();
        using var vault = new ClipboardVault(fixture.History);
        using var pin = Pin(); Assert.True(vault.Unlock(pin, true));
        string destination = Path.Combine(fixture.History, "failed.bin.enc");
        Assert.Throws<IOException>(() => vault.WriteEncryptedFile(destination, output =>
        {
            output.Write(Encoding.UTF8.GetBytes("sensitive partial file"));
            Assert.Single(Directory.GetFiles(fixture.History, "*.staging.enc"));
            throw new IOException("Injected copy failure.");
        }));
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(fixture.History, "*.staging.enc"));
    }

    [Fact]
    public async Task LockingPersonalDoesNotDiscardThePublicFingerprintWithTheSameContent()
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.History);
        await store.InitializeAsync();
        var capture = new ClipboardCapture { Text = "shared bytes" };
        using var pin = Pin(); Assert.True(await store.UnlockPersonalAsync(pin, true));
        var personal = (await store.ImportAsync(capture with { TargetCategory = "Personal" })).Entry!;
        await store.LockPersonalAsync();
        var publicEntry = (await store.ImportAsync(capture)).Entry!;
        Assert.NotEqual(personal.Id, publicEntry.Id);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(await store.UnlockPersonalAsync(pin));
            Assert.True(await store.UnlockPersonalAsync(pin));
            Assert.Single(store.Snapshot(true));
            Assert.Equal(personal.Id, (await store.ImportAsync(capture)).Entry!.Id);
            await store.LockPersonalAsync();
            Assert.Equal(publicEntry.Id, (await store.ImportAsync(capture)).Entry!.Id);
            Assert.Single(store.Snapshot());
        }
    }

    [Fact]
    public async Task PersonalImportDoesNotReturnAnExistingPublicRecord()
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.History);
        await store.InitializeAsync();
        var capture = new ClipboardCapture { Text = "same content" };
        var publicEntry = (await store.ImportAsync(capture)).Entry!;
        using var pin = Pin(); Assert.True(await store.UnlockPersonalAsync(pin, true));
        var personal = (await store.ImportAsync(capture with { TargetCategory = "Personal" })).Entry!;
        Assert.True(personal.IsPersonal);
        Assert.NotEqual(publicEntry.Id, personal.Id);
        Assert.Single(store.Snapshot()); Assert.Single(store.Snapshot(true));
    }

    [Fact]
    public async Task RemovingOneOfTwoSameFingerprintEntriesKeepsTheSurvivingIndex()
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.History);
        await store.InitializeAsync();
        using var pin = Pin(); Assert.True(await store.UnlockPersonalAsync(pin, true));
        var capture = new ClipboardCapture { Text = "matching content" };
        var privateEntry = (await store.ImportAsync(capture with { TargetCategory = "Personal" })).Entry!;
        await store.LockPersonalAsync();
        var publicEntry = (await store.ImportAsync(capture)).Entry!;
        Assert.True(await store.UnlockPersonalAsync(pin));
        await store.MovePersonalAsync(privateEntry.Id, false);
        await store.DeleteAsync(privateEntry.Id);
        Assert.Equal(publicEntry.Id, (await store.ImportAsync(capture)).Entry!.Id);
        Assert.Single(store.Snapshot());
    }

    [Theory]
    [InlineData(false, "../escaped.txt")]
    [InlineData(false, "..\\escaped.txt")]
    [InlineData(true, "../escaped.txt")]
    [InlineData(true, "..\\escaped.txt")]
    public async Task PublicAndPersonalExportsRejectZipTraversalAndCleanPartialExports(bool personal, string archivePath)
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.Root, "folder"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "original.txt"), "source remains intact");
        using var store = new ClipboardHistoryStore(fixture.History);
        await store.InitializeAsync();
        using var pin = Pin(); if (personal) Assert.True(await store.UnlockPersonalAsync(pin, true));
        var entry = (await store.ImportAsync(new ClipboardCapture { FilePaths = [source], TargetCategory = personal ? "Personal" : null })).Entry!;
        using var malicious = new MemoryStream();
        using (var zip = new ZipArchive(malicious, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var good = new StreamWriter(zip.CreateEntry("valid.txt").Open())) good.Write("partial export");
            using (var bad = new StreamWriter(zip.CreateEntry(archivePath).Open())) bad.Write("escaped content");
        }
        string directory = Path.Combine(fixture.History, personal ? "personal" : "items", entry.Id.ToString("N"));
        string attachment = Path.Combine(directory, "0.zip" + (personal ? ".enc" : ""));
        if (personal)
        {
            using var vault = new ClipboardVault(fixture.History); Assert.True(vault.Unlock(pin, false));
            using var output = new MemoryStream(); malicious.Position = 0;
            vault.TransformFile(malicious, output, true); File.WriteAllBytes(attachment, output.ToArray());
        }
        else File.WriteAllBytes(attachment, malicious.ToArray());
        await Assert.ThrowsAsync<IOException>(() => store.ExportFilesAsync(entry.Id));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.History, "exports"), "*", SearchOption.AllDirectories));
        Assert.NotNull(store.GetEntry(entry.Id));
        Assert.Equal("source remains intact", File.ReadAllText(Path.Combine(source, "original.txt")));
        Assert.NotNull((await store.ImportAsync(new ClipboardCapture { Text = "queue after rejected ZIP" })).Entry);
    }

    private static void AssertEncryptedTree(string history, Guid id, string secret)
    {
        Assert.All(Directory.GetFiles(Path.Combine(history, "personal", id.ToString("N"))), path =>
        {
            Assert.EndsWith(".enc", path);
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        });
    }
    private static byte[] ReadExport(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
    }
    private static SecureString Pin()
    {
        var pin = new SecureString(); foreach (char c in "123456") pin.AppendChar(c); pin.MakeReadOnly(); return pin;
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "vnotch-storage-audit-" + Guid.NewGuid().ToString("N"));
        internal string History => Path.Combine(Root, "history");
        internal Fixture() => Directory.CreateDirectory(History);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
