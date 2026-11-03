using System.IO;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VNotch.Services;
using VNotch.Services.Clipboard;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

public sealed class NativeHotPathTests(ITestOutputHelper output)
{
    [Fact]
    public void LargeUnusedJsonOnlyAllocatesTheFinalManagedFields()
    {
        string json = "{\"unused\":\"" + new string('x', 1_000_000) + "\",\"items\":[{\"id\":{\"videoId\":\"valid000001\"},\"snippet\":{\"title\":\"Fixture song\"}}]}";
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        _ = YouTubeMetadataParsing.ParseUtf8(bytes, 0, "Fixture song");
        long before = GC.GetAllocatedBytesForCurrentThread();
        var parsed = YouTubeMetadataParsing.ParseUtf8(bytes, 0, "Fixture song");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"UTF-8 JSON ({bytes.Length:N0} bytes): {allocated:N0} managed bytes for parsing and final fields.");
        Assert.Equal("valid000001", Assert.Single(parsed.Items).First);
        Assert.True(allocated < 32 * 1024, $"Allocated {allocated:N0} managed bytes.");
    }
    [Fact]
    public void Utf8MetadataSelectsMatchingCandidatesAndDecodesEscapedUnicode()
    {
        const string json = """
            {"items":[
              {"id":{"videoId":"wrong000001"},"snippet":{"title":"Unrelated"}},
              {"id":{"videoId":"valid000001"},"snippet":{"title":"old","tit\u006ce":"Bài hát \uD83D\uDE3A","channelTitle":"A \"quoted\" artist"}},
              {"id":{"videoId":"valid000002"},"snippet":{"title":"Bài hát live"}}
            ],"unused":{"text":"ignore { \"title\": \"fake\" }"}}
            """;
        var parsed = YouTubeMetadataParsing.ParseUtf8(Encoding.UTF8.GetBytes(json), 0, "Bài hát");
        Assert.Equal(2, parsed.Items.Count);
        Assert.Equal("valid000001", parsed.Items[0].First);
        Assert.Equal("Bài hát 😺", parsed.Items[0].Second);
        Assert.Equal("A \"quoted\" artist", parsed.Items[0].Third);
    }

    [Fact]
    public void Utf8MetadataExtractsPipedIdentityDurationAndThumbnail()
    {
        const string json = """{"items":[{"url":"/watch?v=dQw4w9WgXcQ","title":"Fixture song","uploaderName":"Singer","duration":123,"thumbnail":"https://images.test/a.jpg"}]}""";
        var item = Assert.Single(YouTubeMetadataParsing.ParseUtf8(Encoding.UTF8.GetBytes(json), 1, "Fixture song").Items);
        Assert.Equal("dQw4w9WgXcQ", item.First);
        Assert.Equal(TimeSpan.FromSeconds(123).Ticks, item.Ticks);
        Assert.Equal("Singer", item.Third);
        Assert.Equal("https://images.test/a.jpg", item.Fourth);
    }

    [Fact]
    public void LiveSearchMetadataKeepsItsUnknownDuration()
    {
        const string json = """[{"videoId":"valid000001","title":"Fixture live","lengthSeconds":-1}]""";
        var item = Assert.Single(YouTubeMetadataParsing.ParseUtf8(Encoding.UTF8.GetBytes(json), 2, "Fixture live").Items);
        Assert.Equal(-TimeSpan.TicksPerSecond, item.Ticks);
    }

    [Fact]
    public void OEmbedRequiresAnObjectResponse() =>
        Assert.Empty(YouTubeMetadataParsing.ParseUtf8("[]"u8, 3, "valid000001").Items);

    [Theory]
    [InlineData("{\"items\":[{},]}")]
    [InlineData("{\"items\":[]} trailing")]
    [InlineData("{\"items\":[],\"unused\":01}")]
    [InlineData("{\"unused\":\"\\x\"}")]
    public void Utf8ParserRejectsMalformedDocumentsEvenInUnusedFields(string json) =>
        Assert.Throws<JsonException>(() => YouTubeMetadataParsing.ParseUtf8(Encoding.UTF8.GetBytes(json), 0, "song"));

    [Fact]
    public void Utf8ParserRejectsDeepNestingAndInvalidUtf8()
    {
        string deep = new string('[', 33) + "0" + new string(']', 33);
        Assert.Throws<JsonException>(() => YouTubeMetadataParsing.ParseUtf8(Encoding.UTF8.GetBytes(deep), 0));
        Assert.Throws<JsonException>(() => YouTubeMetadataParsing.ParseUtf8([0x22, 0xC3, 0x22], 0));
        Assert.Throws<JsonException>(() => YouTubeMetadataParsing.ParseUtf8(Encoding.UTF8.GetBytes("{\"title\":\"\\uD800\"}"), 3));
    }

    [Theory]
    [InlineData("PT2147483647H")]
    [InlineData("PT999999999999999999999999999999999999999999999999999H")]
    public void NativeDurationParsingRejectsOverflow(string duration) =>
        Assert.Equal(TimeSpan.Zero, YouTubeMetadataParsing.ParseIso8601Duration(duration));

    [Fact]
    public async Task PooledReaderHandlesSplitUtf8AndEnforcesUnknownLengthLimit()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("{\"title\":\"Tiếng Việt 😺\"}");
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ShortReads(bytes)) };
        using var body = await PooledUtf8Response.ReadAsync(response, bytes.Length, default);
        Assert.Equal("Tiếng Việt 😺", Assert.Single(YouTubeMetadataParsing.ParseUtf8(body.Span, 3, "video").Items).Second);
        using var oversized = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ShortReads(bytes)) };
        await Assert.ThrowsAsync<InvalidDataException>(() => PooledUtf8Response.ReadAsync(oversized, bytes.Length - 1, default));
    }

    [Fact]
    public void NativeConsumerProbeMatchesFullPathsAndSpecificProcesses()
    {
        using var probe = new PrivacyIndicatorService.ConsumerProcessProbe();
        string raw = Environment.ProcessPath!.Replace('\\', '#');
        Assert.True(probe.IsRunning(raw));
        Assert.True(probe.MatchesProcess(raw.ToUpperInvariant(), (uint)Environment.ProcessId));
        Assert.False(probe.MatchesProcess(raw, uint.MaxValue));
        string wrong = Path.Combine(Path.GetTempPath(), "different-location", Path.GetFileName(Environment.ProcessPath!)).Replace('\\', '#');
        Assert.False(probe.IsRunning(wrong));
        Assert.False(probe.IsRunning(raw + "\0ignored"));
    }

    [Theory]
    [InlineData("YouTube", false)]
    [InlineData("YouTube - Home - Google Chrome", false)]
    [InlineData("SoundCloud - Home", true)]
    public void NativeMediaTitleScannerRejectsHomePagesAndWrongRecoveryPlatform(string title, bool recovery) =>
        Assert.Null(WindowTitleScanner.ParseMediaWindowTitle(title, recovery));

    [Fact]
    public void NativeMediaTitleScannerSplitsRecoveryTrackAndArtist()
    {
        var match = Assert.IsType<MediaWindowTitle>(WindowTitleScanner.ParseMediaWindowTitle("Singer - Song - YouTube - Google Chrome", true));
        Assert.Equal(MediaPlatform.YouTube, match.Platform);
        Assert.Equal("Song", match.Track);
        Assert.Equal("Singer", match.Artist);
        Assert.Equal("Singer - Song", match.ExtractedTitle);
    }

    [Fact]
    public void NativeVaultImageAuthenticatesBeforeExportAndAllocatesOnlyOneManagedPayload()
    {
        string root = Path.Combine(Path.GetTempPath(), "VNotch-native-vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var vault = new ClipboardVault(root);
            using var pin = new SecureString();
            foreach (char digit in "123456") pin.AppendChar(digit);
            Assert.True(vault.Unlock(pin, true));
            string path = Path.Combine(root, "image.enc");
            byte[] expected = RandomNumberGenerator.GetBytes(1_000_000);
            byte[] envelope = vault.Seal(expected);
            File.WriteAllBytes(path, envelope);
            _ = vault.ReadImage(path, default); // Warm interop and FileStream paths.
            long before = GC.GetAllocatedBytesForCurrentThread();
            byte[] actual = vault.ReadImage(path, default);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            output.WriteLine($"Vault image ({expected.Length:N0} bytes): {allocated:N0} managed bytes including the returned payload.");
            Assert.Equal(expected, actual);
            Assert.True(allocated < expected.Length + 64 * 1024, $"Allocated {allocated:N0} managed bytes.");
            using var exported = new MemoryStream();
            vault.CopyImage(path, exported);
            Assert.Equal(expected, exported.ToArray());
            envelope[12] ^= 1; File.WriteAllBytes(path, envelope);
            using var rejected = new MemoryStream();
            Assert.Throws<CryptographicException>(() => vault.ReadImage(path, default));
            Assert.Throws<CryptographicException>(() => vault.CopyImage(path, rejected));
            Assert.Equal(0, rejected.Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ShortReads(byte[] bytes) : Stream
    {
        private int _offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int length = Math.Min(Math.Min(count, 3), bytes.Length - _offset);
            bytes.AsSpan(_offset, length).CopyTo(buffer.AsSpan(offset, length)); _offset += length;
            return length;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min(Math.Min(buffer.Length, 3), bytes.Length - _offset);
            bytes.AsMemory(_offset, length).CopyTo(buffer); _offset += length;
            return ValueTask.FromResult(length);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
