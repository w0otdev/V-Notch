using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class ServicesAuditFixTests
{
    [Fact]
    public async Task ConcurrentFetchForSameVideoSharesTheInFlightRequest()
    {
        var release = new TaskCompletionSource<List<LyricLine>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int fallbacks = 0;
        using var client = new HttpClient(new ThrowingHandler());
        using var service = new YouTubeSubtitleService(client, (_, _) =>
        {
            Interlocked.Increment(ref fallbacks);
            return release.Task;
        });

        var first = service.FetchSubtitlesAsync("auditfix123");
        var second = service.FetchSubtitlesAsync("auditfix123");
        var lines = new List<LyricLine> { new(TimeSpan.Zero, "Caption") };
        release.SetResult(lines);

        Assert.Same(lines, await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(lines, await second.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, fallbacks);
    }

    [Fact]
    public void SyncDisposeWaitsForQueuedSettingsSaves()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vnotch-audit-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "settings.json");
        try
        {
            var service = new SettingsService(path, _ => { });
            for (int i = 0; i < 5; i++)
                _ = service.SaveAsync(new NotchSettings { ManualCity = "City" + i });

            service.Dispose();

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("City4", doc.RootElement.GetProperty(nameof(NotchSettings.ManualCity)).GetString());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* cleanup best-effort */ }
        }
    }

    [Fact]
    public void YouTubeApiKeyIsCachedUntilSettingsFileChanges()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vnotch-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "settings.json");
        try
        {
            WriteKey(path, "first-key-0123456789");
            File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal("first-key-0123456789", MediaMetadataLookupService.GetCachedYouTubeApiKey(path));

            // Same timestamp: the cached value is reused without re-reading the file.
            WriteKey(path, "second-key-0123456789");
            File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal("first-key-0123456789", MediaMetadataLookupService.GetCachedYouTubeApiKey(path));

            File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal("second-key-0123456789", MediaMetadataLookupService.GetCachedYouTubeApiKey(path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* cleanup best-effort */ }
        }
    }

    private static void WriteKey(string path, string key) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["EnableYouTubeApi"] = true,
            ["YouTubeApiKey"] = DataProtection.Protect(key)
        }));

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Fixture offline"));
    }
}
