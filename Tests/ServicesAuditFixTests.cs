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
            using var settingsService = new SettingsService(path);
            settingsService.Save(new VNotch.Models.NotchSettings
            {
                SettingsVersion = SettingsMigrator.CurrentVersion,
                EnableYouTubeApi = true,
                YouTubeApiKey = "first-key-0123456789"
            });
            DateTime cachedTime = File.GetLastWriteTimeUtc(path);
            Assert.Equal("first-key-0123456789", MediaMetadataLookupService.GetYouTubeApiKey(settingsService));

            // Replace encrypted key with an equal-length serialized JSON token and restore timestamp: cached value is reused.
            string rawOnDisk = File.ReadAllText(path);
            string oldToken = JsonSerializer.Serialize(JsonDocument.Parse(rawOnDisk).RootElement.GetProperty("YouTubeApiKey").GetString()!);
            Assert.Contains(oldToken, rawOnDisk, StringComparison.Ordinal);
            string newToken;
            int salt = 0;
            do
            {
                newToken = JsonSerializer.Serialize(DataProtection.Protect($"other-key-{salt++:D10}"));
            } while (newToken.Length != oldToken.Length);
            string expectedNewKey = $"other-key-{salt - 1:D10}";
            File.WriteAllText(path, rawOnDisk.Replace(oldToken, newToken, StringComparison.Ordinal));
            File.SetLastWriteTimeUtc(path, cachedTime);
            Assert.Equal("first-key-0123456789", MediaMetadataLookupService.GetYouTubeApiKey(settingsService));

            File.SetLastWriteTimeUtc(path, cachedTime.AddMinutes(1));
            Assert.Equal(expectedNewKey, MediaMetadataLookupService.GetYouTubeApiKey(settingsService));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* cleanup best-effort */ }
        }
    }

    private static void WriteKey(string path, string key) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["SettingsVersion"] = SettingsMigrator.CurrentVersion,
            ["EnableYouTubeApi"] = true,
            ["YouTubeApiKey"] = DataProtection.Protect(key)
        }));

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Fixture offline"));
    }
}
