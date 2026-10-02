using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

public sealed class ApiKeySecurityTests
{
    [Fact]
    public void YouTubeKeyIsOnlyInHeader()
    {
        using var request = MediaMetadataLookupService.CreateYouTubeApiRequest(
            "https://www.googleapis.com/youtube/v3/videos?id=test", "test-secret");
        Assert.DoesNotContain("test-secret", request.RequestUri!.ToString());
        Assert.Equal("test-secret", request.Headers.GetValues("x-goog-api-key").Single());
    }

    [Theory]
    [InlineData("http://www.googleapis.com/youtube/v3/videos")]
    [InlineData("https://evil.example/youtube/v3/videos")]
    [InlineData("https://www.googleapis.com.evil.example/youtube/v3/videos")]
    [InlineData("https://www.googleapis.com/other")]
    public void YouTubeKeyCannotBeSentToOtherEndpoints(string url)
    {
        Assert.Throws<ArgumentException>(() => MediaMetadataLookupService.CreateYouTubeApiRequest(url, "test-secret"));
    }

    [Theory]
    [InlineData("x-api-key: arbitrary-secret")]
    [InlineData("x-goog-api-key: arbitrary-secret")]
    [InlineData("{\"SpotlightClaudeApiKey\":\"arbitrary-secret\"}")]
    [InlineData("api_key=arbitrary-secret")]
    [InlineData("provider rejected sk-proj-arbitrary-secret")]
    public void DiagnosticsRedactKeys(string input)
    {
        Assert.DoesNotContain("arbitrary-secret", SensitiveDataScrubber.Scrub(input));
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("Gemini")]
    [InlineData("Claude")]
    [InlineData("DeepSeek")]
    public void InvalidHeaderKeyFailsWithoutExposingValue(string provider)
    {
        foreach (string key in new[] { "secret\r\nx-header: injected", "secret token", "secret\0token", new string('s', 4097) })
        {
            var settings = new NotchSettings { SpotlightAiProvider = provider };
            SpotlightAiService.Configure(settings, provider, key, "model");
            var error = Assert.Throws<SpotlightAiException>(() => SpotlightAiService.CreateRequest(settings, []));
            Assert.Equal("spotlight.ai.authError", error.Message);
            Assert.DoesNotContain(key, error.ToString());
        }
    }
}
