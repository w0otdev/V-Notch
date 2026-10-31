using System.IO;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Clipboard;
using VNotch.Services.Spotlight;
using VNotch.Services.Spotlight.Providers;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class AuditRemediationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vnotch-audit-fix-").FullName;

    [Fact]
    public async Task FileNamesCannotBeConfusedWithTheStartOfFileContents()
    {
        string a = Path.Combine(_root, "a"), ab = Path.Combine(_root, "ab");
        await File.WriteAllBytesAsync(a, "bc"u8.ToArray());
        await File.WriteAllBytesAsync(ab, "c"u8.ToArray());
        await using var store = new ClipboardHistoryStore(Path.Combine(_root, "clipboard"));
        await store.InitializeAsync();
        var first = (await store.ImportAsync(new ClipboardCapture { FilePaths = [a] })).Entry!;
        var second = (await store.ImportAsync(new ClipboardCapture { FilePaths = [ab] })).Entry!;
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        Assert.Equal("c", await File.ReadAllTextAsync(Assert.Single(await store.ExportFilesAsync(second.Id))));
    }

    [Fact]
    public async Task UnreadablePersonalItemDoesNotHideHealthyItems()
    {
        string root = Path.Combine(_root, "clipboard");
        await using var store = new ClipboardHistoryStore(root);
        await store.InitializeAsync();
        using var pin = new SecureString();
        for (int i = 0; i < ClipboardPasscode.Length; i++) pin.AppendChar('1');
        Assert.True(await store.UnlockPersonalAsync(pin, create: true));
        var healthy = (await store.ImportAsync(new ClipboardCapture { Text = "healthy note", TargetCategory = "Personal" })).Entry!;
        var corrupt = (await store.ImportAsync(new ClipboardCapture { Text = "damaged note", TargetCategory = "Personal" })).Entry!;
        await store.LockPersonalAsync();
        string damaged = Path.Combine(root, "personal", corrupt.Id.ToString("N"), "entry.json.enc");
        await File.WriteAllBytesAsync(damaged, [1, 2, 3]);
        Assert.True(await store.UnlockPersonalAsync(pin));
        Assert.Equal(healthy.Id, Assert.Single(store.Snapshot(personal: true)).Id);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(damaged));
    }

    [Fact]
    public async Task ActivePublicExportSurvivesRetentionAndIsReleasedExplicitly()
    {
        string file = Path.Combine(_root, "source.txt");
        await File.WriteAllTextAsync(file, "keep clipboard data");
        await using var store = new ClipboardHistoryStore(Path.Combine(_root, "clipboard"));
        await store.InitializeAsync();
        var entry = (await store.ImportAsync(new ClipboardCapture { FilePaths = [file] })).Entry!;
        string export = Assert.Single(await store.ExportFilesAsync(entry.Id));
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(export)!, DateTime.UtcNow.AddHours(-3));
        await store.PruneAsync(DateTime.UtcNow);
        Assert.Equal("keep clipboard data", await File.ReadAllTextAsync(export));
        await store.ReleasePersonalExportsAsync([export]);
        await store.PruneAsync(DateTime.UtcNow);
        Assert.False(File.Exists(export));
    }

    [Fact]
    public async Task FailedCredentialDecryptionCannotBeOverwrittenByAnAutomaticSave()
    {
        string path = Path.Combine(_root, "settings.json");
        string original = JsonSerializer.Serialize(new { SettingsVersion = SettingsMigrator.CurrentVersion, YouTubeApiKey = "enc:invalid", EnableLocalOnlyMode = true, Width = 500 });
        await File.WriteAllTextAsync(path, original);
        await using var settings = new SettingsService(path, _ => { });
        var loaded = settings.Load();
        Assert.True(loaded.EnableLocalOnlyMode);
        Assert.Equal(500, loaded.Width);
        await Assert.ThrowsAsync<CryptographicException>(() => settings.SaveAsync(loaded));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new NotchSettings { SettingsVersion = SettingsMigrator.CurrentVersion }));
        await settings.SaveAsync(settings.Load());
    }

    [Fact]
    public void ImportRejectsOversizedFileBeforeJsonParsing()
    {
        string path = Path.Combine(_root, "oversized.vns");
        using (var file = File.Create(path)) file.SetLength(2 * 1024 * 1024 + 1);
        using var settings = new SettingsService(Path.Combine(_root, "settings.json"), _ => { });
        Assert.Throws<InvalidOperationException>(() => settings.ImportSettingsFromFile(path));
    }

    [Fact]
    public void InstallationRejectsAnUnrelatedNonemptyFolder()
    {
        File.WriteAllText(Path.Combine(_root, "personal.txt"), "must survive");
        Assert.Throws<ArgumentException>(() => InstallDirectoryCleanup.ValidateInstallDirectory(_root));
        Assert.Equal("must survive", File.ReadAllText(Path.Combine(_root, "personal.txt")));
    }

    [Fact]
    public async Task InterruptedPayloadCopyRestoresEveryExistingFile()
    {
        string source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        string install = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        foreach (string name in new[] { "V-Notch.exe", "asset.txt" })
        {
            File.WriteAllText(Path.Combine(source, name), "new " + name);
            File.WriteAllText(Path.Combine(install, name), "old " + name);
        }
        File.WriteAllText(Path.Combine(install, "personal.txt"), "unrelated data");
        var callbacks = new SetupInstallCallbacks(() => Task.CompletedTask, (_, _) => { }, (_, _) => { }, (_, _) => { }, _ => { });
        await Assert.ThrowsAsync<IOException>(() => SetupOperations.InstallAsync(new(source, install, false), progress =>
        {
            if (!progress.IsIndeterminate && progress.CurrentStep == 1) throw new IOException("Interrupted after the first replacement.");
        }, callbacks));
        Assert.Equal("old V-Notch.exe", File.ReadAllText(Path.Combine(install, "V-Notch.exe")));
        Assert.Equal("old asset.txt", File.ReadAllText(Path.Combine(install, "asset.txt")));
        Assert.Equal("unrelated data", File.ReadAllText(Path.Combine(install, "personal.txt")));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:192.168.1.2", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void ImageConnectionsRejectPrivateAndMappedAddresses(string address, bool allowed) =>
        Assert.Equal(allowed, ClipboardDropReader.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AiStreamRequiresItsCompletionMarker(bool complete)
    {
        string body = "data: {\"choices\":[{\"delta\":{\"content\":\"partial answer\"}}]}\n\n" +
            (complete ? "data: [DONE]\n\n" : "");
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
        var service = new SpotlightAiService(client);
        var settings = new NotchSettings { AllowOnlineAi = true, SpotlightAiProvider = "OpenAI", SpotlightOpenAIApiKey = "fixture-key", SpotlightOpenAIModel = "fixture-model" };
        async Task Consume() { await foreach (var _ in service.StreamAsync(settings, [new("user", "hello")], default)) { } }
        if (complete) await Consume();
        else await Assert.ThrowsAsync<SpotlightAiException>(Consume);
    }

    [Fact]
    public async Task LyricsFallbackRunsAfterThePrimaryTransportFails()
    {
        using var primary = Client((_, _) => throw new HttpRequestException("Primary unavailable."));
        int fallbackRequests = 0;
        using var fallback = Client((_, _) =>
        {
            fallbackRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"meta\":{\"level\":\"word\"},\"lines\":[{\"text\":\"Fallback lyric\",\"start\":1200,\"end\":2400}]}") });
        });
        using var lyrics = new LyricsService(primary, fallback);
        Assert.NotNull(await lyrics.FetchSyncedLyricsAsync("Track", "Artist", 240));
        Assert.Equal(1, fallbackRequests);
    }

    [Fact]
    public async Task AppIndexRefreshesAfterItsLifetime()
    {
        DateTime now = DateTime.UtcNow;
        int builds = 0;
        var provider = new AppSearchProvider(() => [new($"app:{++builds}", SpotlightResultKind.Application, "Editor", "App", "editor.exe")], () => now);
        var first = await provider.WarmupAsync();
        Assert.Same(first, await provider.WarmupAsync());
        now = now.AddMinutes(2);
        Assert.NotEqual(first[0].Id, (await provider.WarmupAsync())[0].Id);
    }

    [Fact]
    public async Task AsyncChatSaveOwnsASnapshotAndFlushesBeforeShutdown()
    {
        string path = Path.Combine(_root, "chats.enc");
        var store = new SpotlightChatStore(path);
        var chats = new List<SpotlightSavedChat> { new() { Draft = "saved draft" } };
        Task<bool> saved = store.SaveAsync(chats);
        chats[0].Draft = "later edit";
        Assert.True(await saved);
        await store.FlushAsync();
        Assert.Equal("saved draft", Assert.Single(store.Load()).Draft);
    }

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new Handler(send)) { BaseAddress = new Uri("https://fixture.invalid") };

    [Fact]
    public async Task LyricsBodyTimeoutStillAllowsTheFallback()
    {
        var stalled = new StalledContent();
        using var primary = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = stalled }));
        primary.Timeout = TimeSpan.FromMilliseconds(100);
        using var fallback = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"meta\":{\"level\":\"word\"},\"lines\":[{\"text\":\"Fallback\",\"start\":1000}]}") }));
        using var lyrics = new LyricsService(primary, fallback);
        Assert.NotNull(await lyrics.FetchSyncedLyricsAsync("Track", "Artist", 240).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(stalled.Cancelled);
    }

    private sealed class StalledContent : StreamContent
    {
        private readonly StalledStream _stream;
        internal bool Cancelled => _stream.Cancelled;
        internal StalledContent() : this(new StalledStream()) { }
        private StalledContent(StalledStream stream) : base(stream) => _stream = stream;
    }

    private sealed class StalledStream : Stream
    {
        internal bool Cancelled;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { Cancelled = token.IsCancellationRequested; }
            return 0;
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ClearingHistoryCannotReportSuccessWhileTheFileCannotBeDeleted()
    {
        string path = Path.Combine(_root, "usage.json");
        await using var usage = new SpotlightUsageStore(path, () => DateTime.UtcNow);
        usage.RecordLaunch(new("fixture", SpotlightResultKind.Application, "App", "App", "fixture.exe"));
        await usage.WaitForPendingSavesAsync();
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => usage.ClearHistory());
        usage.ClearHistory();
        Assert.False(File.Exists(path));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    public void Dispose()
    {
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase);
        Directory.Delete(_root, true);
    }
}
