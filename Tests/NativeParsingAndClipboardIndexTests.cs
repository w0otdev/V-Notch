using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Clipboard;
using Xunit;

namespace VNotch.Tests;

public sealed class NativeParsingAndClipboardIndexTests
{
    [Fact]
    public void TtmlNamespacesEntitiesPreservedWhitespaceAndDuetsHaveStableUtf16Offsets()
    {
        const string xml = """
            <t:tt xmlns:t="http://www.w3.org/ns/ttml" xmlns:m="http://www.w3.org/ns/ttml#metadata"><t:body>
            <t:p begin="1s" end="3s" xml:space="preserve"><t:span begin="1s" end="2s">🎵&amp;</t:span> <t:span begin="2s" end="3s"><![CDATA[Đây]]></t:span><t:span m:role="x-bg">hide</t:span></t:p>
            <t:p begin="1s" end="3s">Duet</t:p><t:p begin="4s" end="5s">Last</t:p>
            </t:body></t:tt>
            """;
        var managed = LyricsService.ParseAmllTtml(xml)!;
        var utf8 = NativeLyricParser.Parse(Encoding.UTF8.GetBytes(xml), 0);
        Assert.Equal(new[] { "🎵& Đây", "Duet", "Last" }, managed.Lines.Select(line => line.Text));
        Assert.Equal(managed.Lines.Select(line => (line.Time, line.Text)), utf8.Select(line => (line.Time, line.Text)));
        Assert.Equal(new[] { 0, 4 }, managed.Lines[0].Words!.Select(word => word.Offset));
        Assert.Equal(new[] { 3, 3 }, managed.Lines[0].Words!.Select(word => word.Length));
    }

    [Fact]
    public void XmlRejectsBrokenDocumentsAndDeepNestingWithoutPartialResults()
    {
        Assert.Throws<XmlException>(() => LyricsService.ParseAmllTtml("<tt xmlns='http://www.w3.org/ns/ttml'><body><p begin='1s' end='2s'>valid</p><broken></body></tt>"));
        Assert.Empty(YouTubeSubtitleService.ParseXml("<transcript><text start='1'>valid</text><broken></transcript>"));
        string deep = string.Concat(Enumerable.Repeat("<x>", 1000)) + string.Concat(Enumerable.Repeat("</x>", 1000));
        Assert.Empty(YouTubeSubtitleService.ParseXml(deep));
    }

    [Theory]
    [InlineData("prefix<00:01.00>🎵 <00:02>Đây<00:03>", "prefix🎵 Đây", false)]
    [InlineData("<00:01.00>🎵 <00:02>Đây", "🎵 Đây", true)]
    [InlineData("<00:61>invalid<00:01.1234>invalid", "<00:61>invalid<00:01.1234>invalid", false)]
    [InlineData("<00:02>one<00:01>two", "onetwo", false)]
    public void EnhancedLrcKeepsInvalidTagsAndRejectsInvalidWordSequences(string input, string clean, bool timed)
    {
        var line = LyricTiming.ParseEnhanced(TimeSpan.FromSeconds(1), input);
        Assert.Equal(clean, line.Text); Assert.Equal(timed, line.Words != null);
    }

    [Fact]
    public void TimedJsonResolvesUnicodeWordMapAndRejectsInvalidUtf8AndTrailingTokens()
    {
        byte[] json = Encoding.UTF8.GetBytes("""{"lines":[{"id":"🎵","start":1000,"text":"🎵 Đây"}],"wordMap":{"🎵":[{"start":1000,"end":1500,"text":"🎵"},{"start":1500,"end":2000,"text":"Đây"}]}}""");
        var line = Assert.Single(NativeLyricParser.Parse(json, 2));
        Assert.Equal(new[] { 0, 3 }, line.Words!.Select(word => word.Offset));
        Assert.Throws<JsonException>(() => NativeLyricParser.Parse([0xff], 2));
        Assert.Throws<JsonException>(() => NativeLyricParser.Parse("{} false"u8, 2));
    }

    [Fact]
    public void CatalogSelectsInNativeWithCaseInsensitiveFieldsAndStrictDocumentValidation()
    {
        const string id = "3OHfY25tqY28d16oZczHc8";
        string json = $$"""{"TRACKNAME":"Faded","ID":"{{id}}","ARTIST":"Alan Walker"}""";
        Assert.Equal(id, SpotifyTrackMatcher.ParseTrackId(Encoding.UTF8.GetBytes(json), "Faded", "Alan Walker", default));
        Assert.Null(SpotifyTrackMatcher.ParseTrackId(Encoding.UTF8.GetBytes(json + "false"), "Faded", "Alan Walker", default));
        Assert.Equal("abc_123-X", NativeCatalogParser.ExtractYouTubeApiKey("bad \"INNERTUBE_API_KEY\": 4; good \"INNERTUBE_API_KEY\" : \"abc_123-X\""u8));
        Assert.True(NativeCatalogParser.ContainsPersistedQueryNotFound("{\"message\":\"PERSISTEDQUERYNOTFOUND\"}"u8));
        string collision = $$"""{"NAME":"wrong","name":"Faded","name":"second ignored","id":"{{id}}"}""";
        Assert.Equal(id, SpotifyTrackMatcher.ParseTrackId(collision, "Faded", "", default));
    }

    [Fact]
    public void DeezerUsesSearchIdentityWithContributorsFromTheDetailResponse()
    {
        var selected = NativeCatalogParser.Select("{\"contributors\":[{\"name\":\"Singer\"},{\"name\":\"Guest\"}]}"u8,
            6, "Faded", "Singer", 180000, "Album",
            "{\"data\":[{\"id\":123,\"title\":\"Faded\",\"duration\":180,\"album\":{\"title\":\"Album\"}}]}"u8, 123);
        Assert.Equal("Singer, Guest", selected.Text);
    }

    [Fact]
    public void ClipboardNativeIndexSurvivesGrowthSlotMovesFingerprintChangesAndReuse()
    {
        using var index = new NativeClipboardEntryIndex();
        var entries = Enumerable.Range(0, 4097).Select(i => new ClipboardEntry
        { Fingerprint = "group-" + i % 17, CopiedUtc = DateTime.UnixEpoch.AddSeconds(i), IsPersonal = i % 3 == 0 }).ToArray();
        foreach (var entry in entries) index[entry.Id] = entry;
        foreach (bool personal in new[] { false, true })
            for (int group = 0; group < 17; ++group)
                Assert.Equal(entries.Last(entry => entry.IsPersonal == personal && entry.Fingerprint == "group-" + group), index.FindDuplicate("group-" + group, personal));
        foreach (var entry in entries.Where((_, i) => i % 2 == 0)) Assert.True(index.TryRemove(entry.Id, out _));
        foreach (var entry in entries.Where((_, i) => i % 2 != 0))
        {
            Assert.True(index.TryGetValue(entry.Id, out var found)); Assert.Equal(entry, found);
            index[entry.Id] = entry with { Fingerprint = "changed", IsPersonal = true };
        }
        foreach (var entry in entries.Where((_, i) => i % 2 == 0)) index[entry.Id] = entry;
        Assert.Equal(entries.Length, index.Count);
        Assert.Equal(entries[^2].Id, index.FindDuplicate("changed", true)!.Id);
    }

    [Fact]
    public void NativeScratchStreamZerosHolesAndTruncatedBytesAndHandlesGrowth()
    {
        using var stream = new NativeBufferStream();
        stream.Write(new byte[100_000].AsSpan()); stream.Position = 10; stream.WriteByte(255);
        stream.SetLength(5); stream.SetLength(20); stream.Position = 0;
        Assert.All(ReadAll(stream), value => Assert.Equal(0, value));
        stream.Position = 70_000; stream.WriteByte(42); stream.Position = 20;
        byte[] tail = ReadAll(stream); Assert.Equal(69_981, tail.Length);
        Assert.All(tail[..^1], value => Assert.Equal(0, value)); Assert.Equal(42, tail[^1]);
        stream.Dispose(); Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
    }

    [Fact]
    public void NativeStreamTransferHonorsQuotasAndPreservesCallbackExceptions()
    {
        byte[] input = new byte[131_073]; new Random(42).NextBytes(input);
        using var source = new MemoryStream(input); using var target = new NativeBufferStream();
        Assert.Equal(input.Length, NativeClipboardStreamCopy.Copy(source, target, input.Length));
        target.Position = 0; Assert.Equal(input, ReadAll(target));
        source.Position = 0;
        Assert.Throws<ClipboardImportLimitException>(() => NativeClipboardStreamCopy.Copy(source, Stream.Null, input.Length - 1));
        source.Position = 0;
        var failure = new InvalidDataException("Expected adapter failure");
        Assert.Same(failure, Assert.Throws<InvalidDataException>(() => NativeClipboardStreamCopy.Copy(source, new FailingStream(failure), input.Length)));
    }

    [Fact]
    public void NativeFileTransferResumesAtTheExistingPositions()
    {
        string root = Path.Combine(Path.GetTempPath(), "VNotch-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] bytes = new byte[150_000]; new Random(10).NextBytes(bytes); File.WriteAllBytes(Path.Combine(root, "source"), bytes);
            using var source = File.OpenRead(Path.Combine(root, "source")); using var destination = File.Create(Path.Combine(root, "dest"));
            source.ReadByte(); destination.WriteByte(99);
            Assert.Equal(bytes.Length - 1, NativeClipboardStreamCopy.Copy(source, destination, bytes.Length));
            Assert.Equal(bytes.Length, source.Position); Assert.Equal(bytes.Length, destination.Position);
            destination.Dispose(); Assert.Equal(new byte[] { 99 }.Concat(bytes.Skip(1)), File.ReadAllBytes(Path.Combine(root, "dest")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static byte[] ReadAll(Stream stream) { using var result = new MemoryStream(); stream.CopyTo(result); return result.ToArray(); }
    private sealed class FailingStream(Exception failure) : MemoryStream
    { public override void Write(ReadOnlySpan<byte> buffer) => throw failure; }
}
