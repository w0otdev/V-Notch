using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class TranslationDownloadTests
{
    [Fact]
    public void SharedStoreHasTheProductionHttpTransportAfterStaticInitialization()
    {
        var transport = typeof(TranslationModelStore).GetField("_client",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.IsType<HttpClient>(transport.GetValue(TranslationModelStore.Shared));
    }

    [Fact]
    public async Task WaitingForHeadersReportsConnectingAndCanBeCancelled()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new DownloadFixture(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new(HttpStatusCode.OK);
        });
        using var cancellation = new CancellationTokenSource();
        var states = new Recorder();
        var download = fixture.Store.DownloadAsync(null, cancellation.Token, states);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(states.States, state => state.Stage == TranslationDownloadStage.Connecting);
        Assert.False(download.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.False(fixture.Store.IsInstalled);
    }

    [Fact]
    public async Task StalledConnectionTimesOutWithAnActionableError()
    {
        using var fixture = new DownloadFixture(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new(HttpStatusCode.OK);
        }, TimeSpan.FromMilliseconds(100));
        var error = await Assert.ThrowsAsync<TranslationException>(() => fixture.Store.DownloadAsync(null, default));
        Assert.Equal("translation.downloadTimeout", error.MessageKey);
        Assert.False(fixture.Store.IsInstalled);
        Assert.Equal(1, fixture.Store.Gate.CurrentCount);
    }

    [Fact]
    public async Task PartialDownloadResumesAndOnlyBecomesReadyAfterHashVerification()
    {
        const int existing = 250_000;
        var states = new Recorder();
        using var fixture = new DownloadFixture((request, _) =>
        {
            Assert.Equal(existing, request.Headers.Range!.Ranges.Single().From);
            var content = new ByteArrayContent(DownloadFixture.Payload[existing..]);
            content.Headers.ContentRange = new ContentRangeHeaderValue(existing, DownloadFixture.Payload.Length - 1, DownloadFixture.Payload.Length);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
        });
        await File.WriteAllBytesAsync(fixture.Partial, DownloadFixture.Payload[..existing]);
        await fixture.Store.DownloadAsync(null, default, states);
        Assert.True(fixture.Store.IsInstalled);
        Assert.Equal(DownloadFixture.Payload, await File.ReadAllBytesAsync(fixture.Final));
        Assert.False(File.Exists(fixture.Partial));
        Assert.Contains(states.States, state => state.Stage == TranslationDownloadStage.Connecting && state.DownloadedBytes == existing);
        Assert.Contains(states.States, state => state.Stage == TranslationDownloadStage.Downloading && state.Percent == 100 && state.BytesPerSecond > 0);
        Assert.True(states.States.FindIndex(s => s.Stage == TranslationDownloadStage.Verifying) < states.States.FindIndex(s => s.Stage == TranslationDownloadStage.Completed));
        Assert.Equal(TranslationDownloadStage.Completed, states.States.Last().Stage);
    }

    [Fact]
    public async Task ServerIgnoringRangeRestartsWithoutAppendingDuplicateBytes()
    {
        using var fixture = new DownloadFixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(DownloadFixture.Payload) }));
        await File.WriteAllBytesAsync(fixture.Partial, DownloadFixture.Payload[..100]);
        await fixture.Store.DownloadAsync(null, default);
        Assert.True(fixture.Store.IsInstalled);
        Assert.Equal(DownloadFixture.Payload, await File.ReadAllBytesAsync(fixture.Final));
    }

    [Fact]
    public async Task StalledBodyKeepsPartialBytesForRetry()
    {
        using var fixture = new DownloadFixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new StalledStream()) }), TimeSpan.FromMilliseconds(100));
        var error = await Assert.ThrowsAsync<TranslationException>(() => fixture.Store.DownloadAsync(null, default));
        Assert.Equal("translation.downloadTimeout", error.MessageKey);
        Assert.Equal(16, new FileInfo(fixture.Partial).Length);
        Assert.False(fixture.Store.IsInstalled);
    }

    [Fact]
    public async Task CorruptBytesNeverReportCompleted()
    {
        byte[] corrupt = DownloadFixture.Payload.ToArray();
        corrupt[0] ^= 0xff;
        using var fixture = new DownloadFixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(corrupt) }));
        var states = new Recorder();
        var error = await Assert.ThrowsAsync<TranslationException>(() => fixture.Store.DownloadAsync(null, default, states));
        Assert.Equal("translation.modelInvalid", error.MessageKey);
        Assert.DoesNotContain(states.States, s => s.Stage == TranslationDownloadStage.Completed);
        Assert.False(fixture.Store.IsInstalled);
    }

    [Fact]
    public async Task FailedImport_RemovesTemporaryCopy_AndPreservesExistingModel()
    {
        using var fixture = new DownloadFixture((_, _) => throw new InvalidOperationException("Import must stay offline."));
        string source = Directory.CreateTempSubdirectory("vnotch-import-test-").FullName;
        byte[] original = [1, 2, 3];
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(source, "model.gguf"), DownloadFixture.Payload);
            await File.WriteAllBytesAsync(fixture.Final, original);
            File.SetAttributes(fixture.Final, FileAttributes.ReadOnly);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.ImportAsync(source, default));
            Assert.False(File.Exists(fixture.Final + ".install.part"));
            Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Final));
            Assert.Equal(1, fixture.Store.Gate.CurrentCount);
        }
        finally
        {
            File.SetAttributes(fixture.Final, FileAttributes.Normal);
            Directory.Delete(source, recursive: true);
        }
    }

    private sealed class Recorder : IProgress<TranslationDownloadProgress>
    {
        internal List<TranslationDownloadProgress> States { get; } = [];
        public void Report(TranslationDownloadProgress value) => States.Add(value);
    }

    private sealed class DownloadFixture : IDisposable
    {
        internal static readonly byte[] Payload = Enumerable.Range(0, 600_000).Select(i => (byte)(i % 251)).ToArray();
        private readonly HttpClient _client;
        internal TranslationModelStore Store { get; }
        internal string Partial => Final + ".part";
        internal string Final => Path.Combine(Store.Root, "model.gguf");
        internal DownloadFixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, TimeSpan? timeout = null)
        {
            string root = Directory.CreateTempSubdirectory("vnotch-download-test-").FullName;
            var policy = new NetworkPrivacy();
            policy.Apply(new NotchSettings());
            _client = new HttpClient(new Handler(send));
            var profile = new TranslationModelProfile("test", "test/repo", "pinned-revision",
                [new("model.gguf", "model.gguf", Payload.Length, Convert.ToHexString(SHA256.HashData(Payload)))]);
            Store = new(root, _client, profile, timeout, policy);
        }
        public void Dispose()
        {
            _client.Dispose();
            foreach (string path in new[] { Final, Partial, Path.Combine(Store.Root, "verified.txt") }) File.Delete(path);
            Directory.Delete(Store.Root);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private sealed class StalledStream : MemoryStream
    {
        private bool _sent;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                DownloadFixture.Payload.AsMemory(0, 16).CopyTo(buffer);
                return 16;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
