using System.Net;
using System.Net.Http;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

public sealed class NetworkPrivacyTests
{
    [Fact]
    public void PrivacyPreferencesRoundTripAndOldSettingsKeepCompatibleDefaults()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<NotchSettings>("{}")!;
        Assert.True(old.AllowOnlineAi);
        Assert.True(old.AllowCopilot);
        Assert.True(old.SaveAiChatHistory);
        var settings = new NotchSettings
        {
            AllowOnlineAi = false,
            AllowCopilot = false,
            AllowOnlineCanvas = false,
            AllowOnlineSubtitles = false,
            AllowOnlineWeather = false,
            SaveAiChatHistory = false
        };
        var saved = System.Text.Json.JsonSerializer.Deserialize<NotchSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        Assert.False(saved.AllowOnlineAi);
        Assert.False(saved.AllowCopilot);
        Assert.False(saved.AllowOnlineCanvas);
        Assert.False(saved.AllowOnlineSubtitles);
        Assert.False(saved.AllowOnlineWeather);
        Assert.False(saved.SaveAiChatHistory);
    }

    [Fact]
    public async Task OfflineBlocksEveryFeatureBeforeTransportIsCalled()
    {
        var policy = new NetworkPrivacy();
        policy.Apply(new NotchSettings { EnableLocalOnlyMode = true });
        int calls = 0;
        foreach (var feature in Enum.GetValues<NetworkFeature>())
        {
            using var client = new HttpClient(new NetworkPrivacy.PrivacyHandler(policy, feature,
                new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); })));
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://example.invalid"));
        }
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RevokingAccessCancelsAnInFlightRequestAndReenableUsesNewPermission()
    {
        var policy = new NetworkPrivacy();
        policy.Apply(new NotchSettings());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new NetworkPrivacy.PrivacyHandler(policy, NetworkFeature.Ai,
            new Handler(async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            })));
        var request = client.GetAsync("https://example.invalid");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var oldPermission = policy.Acquire(NetworkFeature.Copilot);
        policy.Apply(new NotchSettings { EnableLocalOnlyMode = true });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(oldPermission.IsCancellationRequested);
        policy.Apply(new NotchSettings());
        Assert.False(policy.Acquire(NetworkFeature.Copilot).IsCancellationRequested);
        Assert.True(oldPermission.IsCancellationRequested);
    }

    [Fact]
    public async Task RevokingAccessAlsoStopsResponseBodyReadsAfterHeaders()
    {
        var policy = new NetworkPrivacy();
        policy.Apply(new NotchSettings());
        using var client = new HttpClient(new NetworkPrivacy.PrivacyHandler(policy, NetworkFeature.Ai,
            new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("private response") }))));
        using var response = await client.GetAsync("https://example.invalid", HttpCompletionOption.ResponseHeadersRead);
        using var stream = await response.Content.ReadAsStreamAsync();
        policy.Apply(new NotchSettings { AllowOnlineAi = false });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ReadAsync(new byte[32], 0, 32));
    }

    [Fact]
    public void IndividualControlsDoNotErasePreferencesOrDisableUnrelatedFeatures()
    {
        var settings = new NotchSettings { AllowCopilot = false, AllowOnlineCanvas = false, SaveAiChatHistory = false };
        Assert.True(NetworkPrivacy.Allows(settings, NetworkFeature.Ai));
        Assert.False(NetworkPrivacy.Allows(settings, NetworkFeature.Copilot));
        Assert.False(NetworkPrivacy.Allows(settings, NetworkFeature.Canvas));
        settings.EnableLocalOnlyMode = true;
        Assert.All(Enum.GetValues<NetworkFeature>(), feature => Assert.False(NetworkPrivacy.Allows(settings, feature)));
        settings.EnableLocalOnlyMode = false;
        Assert.False(settings.AllowCopilot);
        Assert.False(settings.Clone().SaveAiChatHistory);
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("GitHub Copilot")]
    public async Task AiCannotBypassOfflineWithInjectedHttpClient(string provider)
    {
        var settings = new NotchSettings { EnableLocalOnlyMode = true, SpotlightAiProvider = provider };
        using var client = new HttpClient(new Handler((_, _) => throw new Exception("Transport must not be called")));
        var error = await Assert.ThrowsAsync<SpotlightAiException>(() => new SpotlightAiService(client).SendAsync(settings, [new("user", "hello")], default));
        Assert.Equal("spotlight.ai.privacyBlocked", error.Message);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task CanvasTransportRequiresCurrentNoticeAcknowledgement(int noticeVersion, bool allowed)
    {
        var policy = new NetworkPrivacy();
        policy.Apply(new NotchSettings
        {
            EnableSpotifyCanvas = true,
            AllowOnlineCanvas = true,
            SpotifyCanvasConsentVersion = noticeVersion
        });
        int calls = 0;
        using var client = new HttpClient(new NetworkPrivacy.PrivacyHandler(policy, NetworkFeature.Canvas,
            new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); })));
        if (allowed)
        {
            using var response = await client.GetAsync("https://example.invalid");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, calls);
        }
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://example.invalid"));
            Assert.Equal(0, calls);
        }
    }

    [Fact]
    public async Task RevokingCanvasConsentCancelsTransportEvenWhenLegacyFlagsRemainTrue()
    {
        var policy = new NetworkPrivacy();
        var settings = new NotchSettings
        {
            EnableSpotifyCanvas = true,
            AllowOnlineCanvas = true,
            SpotifyCanvasConsentVersion = SpotifyCanvasConsent.CurrentNoticeVersion
        };
        policy.Apply(settings);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new NetworkPrivacy.PrivacyHandler(policy, NetworkFeature.Canvas,
            new Handler(async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            })));
        var request = client.GetAsync("https://example.invalid");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        settings.SpotifyCanvasConsentVersion = 0;
        policy.Apply(settings);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.False(policy.IsAllowed(NetworkFeature.Canvas));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://example.invalid"));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
