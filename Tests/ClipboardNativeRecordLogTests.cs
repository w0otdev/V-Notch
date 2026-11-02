using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using VNotch.Models;
using VNotch.Services.Clipboard;
using Xunit;

namespace VNotch.Tests;

public sealed class ClipboardNativeRecordLogTests
{
    [Fact]
    public void EncryptedRecordsAppendReopenAndIgnoreAnUncommittedTail()
    {
        using var fixture = new Fixture();
        byte[] first = "{\"text\":\"clipboard document secret alpha 918375\"}"u8.ToArray();
        byte[] second = "{\"text\":\"clipboard document secret beta 542887\"}"u8.ToArray();
        long firstSize;
        using (var records = new NativeClipboardRecordStore(fixture.Root))
        {
            records.Append(fixture.Path, first);
            firstSize = new FileInfo(fixture.Path).Length;
            records.Append(fixture.Path, second);
            Assert.True(new FileInfo(fixture.Path).Length > firstSize);
            Assert.Equal(second, records.Read(fixture.Path));
            string disk = Encoding.UTF8.GetString(File.ReadAllBytes(fixture.Path));
            Assert.DoesNotContain("clipboard document secret", disk);
            Assert.Equal("VNCLOG1\0", disk[..8]);
        }
        using (var append = new FileStream(fixture.Path, FileMode.Append)) append.Write("incomplete record"u8);
        using var reopened = new NativeClipboardRecordStore(fixture.Root);
        Assert.Equal(second, reopened.Read(fixture.Path));
        reopened.Append(fixture.Path, first);
        Assert.Equal(first, reopened.Read(fixture.Path));
    }

    [Fact]
    public void LegacyJsonIsReadableAndMigrationPublishesOnlyCiphertext()
    {
        using var fixture = new Fixture();
        byte[] original = "{\"text\":\"legacy clipboard secret 741998\"}"u8.ToArray();
        File.WriteAllBytes(fixture.Path, original);
        using var records = new NativeClipboardRecordStore(fixture.Root);
        Assert.Null(records.Read(fixture.Path));
        Assert.False(File.Exists(Path.Combine(fixture.Root, ".history-key.dpapi")));
        records.Migrate(fixture.Path);
        Assert.Equal(original, records.Read(fixture.Path));
        Assert.DoesNotContain("legacy clipboard secret", Encoding.UTF8.GetString(File.ReadAllBytes(fixture.Path)));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
    }

    [Fact]
    public void CorruptLatestCiphertextFailsAuthenticationAndCannotBeAppendedOver()
    {
        using var fixture = new Fixture();
        using var records = new NativeClipboardRecordStore(fixture.Root);
        records.Append(fixture.Path, "{\"value\":1}"u8);
        records.Append(fixture.Path, "{\"value\":2}"u8);
        byte[] damaged = File.ReadAllBytes(fixture.Path);
        int offset = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(damaged.AsSpan(4096 + 8, 8)));
        damaged[offset + 48] ^= 0x80;
        File.WriteAllBytes(fixture.Path, damaged);
        Assert.Throws<IOException>(() => records.Read(fixture.Path));
        Assert.Throws<IOException>(() => records.TryReadJson<Dictionary<string, int>>(fixture.Path, out _));
        Assert.Throws<IOException>(() => records.Append(fixture.Path, "{\"value\":3}"u8));
        Assert.Equal(damaged, File.ReadAllBytes(fixture.Path));
    }

    [Fact]
    public void TornCommitSlotRecoversThePreviousDurableRecord()
    {
        using var fixture = new Fixture();
        using var records = new NativeClipboardRecordStore(fixture.Root);
        byte[] previous = "{\"value\":\"previous\"}"u8.ToArray();
        records.Append(fixture.Path, previous);
        records.Append(fixture.Path, "{\"value\":\"latest\"}"u8);
        byte[] damaged = File.ReadAllBytes(fixture.Path);
        damaged[4096 + 24] ^= 0x80; // Generation 2 commit checksum; generation 1 is on a separate page.
        File.WriteAllBytes(fixture.Path, damaged);
        Assert.Equal(previous, records.Read(fixture.Path));
        records.Append(fixture.Path, "{\"value\":\"recovered\"}"u8);
        Assert.Equal("{\"value\":\"recovered\"}"u8.ToArray(), records.Read(fixture.Path));
    }

    [Fact]
    public void CompactionBoundsObsoleteVersionsAndKeepsTheNewestPayload()
    {
        using var fixture = new Fixture();
        using var records = new NativeClipboardRecordStore(fixture.Root);
        byte[] large = new byte[64 * 1024];
        new Random(83).NextBytes(large);
        for (int i = 0; i < 8; ++i) records.Append(fixture.Path, large);
        records.Append(fixture.Path, "latest"u8);
        Assert.InRange(new FileInfo(fixture.Path).Length, 12288, 64 * 1024);
        Assert.Equal("latest"u8.ToArray(), records.Read(fixture.Path));
    }

    [Fact]
    public void AKeyFromAnotherHistoryCannotDecryptTheRecord()
    {
        using var first = new Fixture();
        using var second = new Fixture();
        using (var records = new NativeClipboardRecordStore(first.Root)) records.Append(first.Path, "first history"u8);
        using (var records = new NativeClipboardRecordStore(second.Root)) records.Append(second.Path, "second history"u8);
        string key = Path.Combine(first.Root, ".history-key.dpapi");
        File.SetAttributes(key, FileAttributes.Normal);
        File.Copy(Path.Combine(second.Root, ".history-key.dpapi"), key, overwrite: true);
        using var reopened = new NativeClipboardRecordStore(first.Root);
        Assert.Throws<IOException>(() => reopened.Read(first.Path));
    }

    [Fact]
    public void MissingKeyFailsClosedWithoutCreatingAReplacement()
    {
        using var fixture = new Fixture();
        using (var records = new NativeClipboardRecordStore(fixture.Root)) records.Append(fixture.Path, "existing record"u8);
        string key = System.IO.Path.Combine(fixture.Root, ".history-key.dpapi");
        File.Delete(key);
        using var reopened = new NativeClipboardRecordStore(fixture.Root);
        Assert.Throws<IOException>(() => reopened.Read(fixture.Path));
        Assert.False(File.Exists(key));
        Assert.Throws<IOException>(() => reopened.Append(fixture.Path, "replacement"u8));
        Assert.False(File.Exists(key));
        using var separateContext = new NativeClipboardRecordStore(fixture.Root);
        Assert.Throws<IOException>(() => separateContext.Append(System.IO.Path.Combine(fixture.Root, "content.json"), "new record"u8));
        Assert.False(File.Exists(key));
    }

    [Fact]
    public void InterruptedKeyCreationCanRecoverBeforeAnyEncryptedRecordExists()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(System.IO.Path.Combine(fixture.Root, ".history-key.dpapi"), []);
        using var records = new NativeClipboardRecordStore(fixture.Root);
        records.Append(fixture.Path, "first committed record"u8);
        Assert.Equal("first committed record"u8.ToArray(), records.Read(fixture.Path));
    }

    [Fact]
    public async Task HistoryMigratesExistingJsonAndPersistsLaterMetadataUpdates()
    {
        using var fixture = new Fixture();
        var id = Guid.NewGuid();
        string directory = System.IO.Path.Combine(fixture.Root, "items", id.ToString("N"));
        Directory.CreateDirectory(directory);
        const string text = "existing public note survives migration 918833";
        var entry = new ClipboardEntry
        {
            Id = id,
            Title = text,
            Preview = text,
            CopiedUtc = DateTime.UtcNow,
            ByteSize = text.Length,
            Fingerprint = "legacy-test"
        };
        File.WriteAllBytes(System.IO.Path.Combine(directory, "entry.json"), JsonSerializer.SerializeToUtf8Bytes(entry));
        File.WriteAllBytes(System.IO.Path.Combine(directory, "content.json"), JsonSerializer.SerializeToUtf8Bytes(new ClipboardContent { Entry = entry, Text = text }));
        using (var store = new ClipboardHistoryStore(fixture.Root))
        {
            await store.InitializeAsync();
            Assert.Equal(id, Assert.Single(store.Snapshot()).Id);
            Assert.Equal(text, (await store.GetContentAsync(id)).Text);
            await store.UpdateAsync(id, pinned: true);
        }
        using var reopened = new ClipboardHistoryStore(fixture.Root);
        await reopened.InitializeAsync();
        Assert.True(Assert.Single(reopened.Snapshot()).IsPinned);
        Assert.Equal(text, (await reopened.GetContentAsync(id)).Text);
        foreach (string path in Directory.GetFiles(directory))
            Assert.DoesNotContain(text, Encoding.UTF8.GetString(File.ReadAllBytes(path)));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(4096)]
    [InlineData(65536)]
    [InlineData(300000)]
    public void JsonReadsHandleStackPooledAndMappedPayloadsAndObserveUpdates(int size)
    {
        using var fixture = new Fixture();
        using var records = new NativeClipboardRecordStore(fixture.Root);
        string original = new('a', size);
        records.Append(fixture.Path, JsonSerializer.SerializeToUtf8Bytes(original));
        Assert.True(records.TryReadJson<string>(fixture.Path, out var value));
        Assert.Equal(original, value);
        records.Append(fixture.Path, JsonSerializer.SerializeToUtf8Bytes("updated"));
        Assert.True(records.TryReadJson<string>(fixture.Path, out value));
        Assert.Equal("updated", value);
    }

    [Fact]
    public void JsonReadsDistinguishLegacyMissingAndInvalidDocuments()
    {
        using var fixture = new Fixture();
        using var records = new NativeClipboardRecordStore(fixture.Root);
        Assert.Throws<FileNotFoundException>(() => records.TryReadJson<string>(fixture.Path, out _));
        File.WriteAllText(fixture.Path, "{\"legacy\":true}");
        Assert.False(records.TryReadJson<string>(fixture.Path, out _));
        records.Append(fixture.Path, "not-json"u8);
        Assert.Throws<JsonException>(() => records.TryReadJson<string>(fixture.Path, out _));
        records.Append(fixture.Path, "\"valid after failure\""u8);
        Assert.True(records.TryReadJson<string>(fixture.Path, out var value));
        Assert.Equal("valid after failure", value);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(65536)]
    [InlineData(300000)]
    public void BufferedAndMappedJsonReadsRejectTamperedCiphertext(int size)
    {
        using var fixture = new Fixture();
        using var records = new NativeClipboardRecordStore(fixture.Root);
        records.Append(fixture.Path, JsonSerializer.SerializeToUtf8Bytes(new string('a', size)));
        byte[] bytes = File.ReadAllBytes(fixture.Path);
        int offset = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8192 + 8, 8)));
        bytes[offset + 48 + size / 2] ^= 1;
        File.WriteAllBytes(fixture.Path, bytes);
        Assert.Throws<IOException>(() => records.TryReadJson<string>(fixture.Path, out _));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(100)]
    [InlineData(8192)]
    [InlineData(12287)]
    public void TruncatedBinaryHeadersFailWithoutBeingTreatedAsLegacy(int size)
    {
        using var fixture = new Fixture();
        byte[] bytes = new byte[size];
        "VNCLOG1\0"u8.CopyTo(bytes);
        File.WriteAllBytes(fixture.Path, bytes);
        using var records = new NativeClipboardRecordStore(fixture.Root);
        Assert.Throws<IOException>(() => records.TryReadJson<string>(fixture.Path, out _));
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Root, ".history-key.dpapi")));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VNotch.RecordTests", Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Root, "entry.json");
        internal Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
