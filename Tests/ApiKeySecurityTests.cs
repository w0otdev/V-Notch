using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

public sealed class ApiKeySecurityTests
{
    [Theory]
    [InlineData("{\"password\": \"secret with spaces\"}", "{\"password\": \"[REDACTED]\"}", "secret with spaces")]
    [InlineData("{\"password\"\n : \n \"private-secret\",\"status\":401}", "{\"password\"\n : \n \"[REDACTED]\",\"status\":401}", "private-secret")]
    [InlineData("{\"\\u0070ass\\u0077ord\":\"private-secret\",\"status\":401}", "{\"\\u0070ass\\u0077ord\":\"[REDACTED]\",\"status\":401}", "private-secret")]
    [InlineData("{\"\\u0073ession\":null,\"status\":401}", "{\"\\u0073ession\":\"[REDACTED]\",\"status\":401}", "null")]
    [InlineData("password='123456'", "password='[REDACTED]'", "123456")]
    [InlineData("Authorization: Basic YWRtaW46MTIz", "Authorization: Basic [REDACTED]", "YWRtaW46MTIz")]
    [InlineData("Basic YWRtaW46MTIz", "Basic [REDACTED]", "YWRtaW46MTIz")]
    [InlineData("Proxy-Authorization: Basic malformed-value", "Proxy-Authorization: Basic [REDACTED]", "malformed-value")]
    [InlineData("Authorization: bAsIc\tYWRtaW46MTIz; status=401", "Authorization: bAsIc\t[REDACTED]; status=401", "YWRtaW46MTIz")]
    [InlineData("{\"Authorization\":\"Basic YWRtaW46MTIz\",\"status\":401}", "{\"Authorization\":\"Basic [REDACTED]\",\"status\":401}", "YWRtaW46MTIz")]
    [InlineData("Cookie: session=private-session; theme=dark", "Cookie: session=[REDACTED]; theme=dark", "private-session")]
    [InlineData("Set-Cookie: connect.sid=s%3Aprivate-session.signature; Path=/; HttpOnly", "Set-Cookie: connect.sid=[REDACTED]; Path=/; HttpOnly", "private-session")]
    [InlineData("token='private-session'; status=401", "token='[REDACTED]'; status=401", "private-session")]
    public void RealLogPayloadsPreserveStructureAndNeverExposeTheSecret(string input, string expected, string secret)
    {
        string result = SensitiveDataScrubber.Scrub(input);
        Assert.Equal(expected, result);
        Assert.DoesNotContain(secret, result);
        Assert.Equal(result, SensitiveDataScrubber.Scrub(result));
    }

    [Theory]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.c2lnbmF0dXJl")]
    [InlineData("IAp7ImFsZyI6IkhTMjU2In0.eyJzdWIiOiIxMjMifQ.c2lnbmF0dXJl")]
    [InlineData("eyJhbGciOiJub25lIn0.eyJzdWIiOiIxMjMifQ.")]
    [InlineData("eyJhbGciOiJkaXIiLCJlbmMiOiJBMjU2R0NNIn0..aXY.Y2lwaGVy.dGFn")]
    public void StandaloneJwtIsRedactedIncludingUnsignedAndEncryptedForms(string token)
    {
        string result = SensitiveDataScrubber.Scrub("WARN token dump: \"" + token + "\"; request=42");
        Assert.Equal("WARN token dump: \"[REDACTED]\"; request=42", result);
        Assert.DoesNotContain(token, result);
    }

    [Theory]
    [InlineData("basic view enabled; session ended")]
    [InlineData("version=1.2.3; host=example.com; password control visible")]
    [InlineData("ordinary_token=visible; mysession=visible; label=secret")]
    public void OrdinaryWordsAndVersionsKeepTheirOriginalString(string input)
        => Assert.Same(input, SensitiveDataScrubber.Scrub(input));

    [Fact]
    public void JsonStructureSurvivesEscapedPasswordValues()
    {
        const string input = "{\"password\":\"quotes \\\" and backslash \\\\ and 日本語 🌸\",\"status\":401}";
        string result = SensitiveDataScrubber.Scrub(input);
        using var json = System.Text.Json.JsonDocument.Parse(result);
        Assert.Equal("[REDACTED]", json.RootElement.GetProperty("password").GetString());
        Assert.Equal(401, json.RootElement.GetProperty("status").GetInt32());
        Assert.DoesNotContain("日本語", result);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("api_key")]
    [InlineData("session")]
    [InlineData("token")]
    [InlineData("connect.sid")]
    public void UnicodeEscapedJsonFieldNamesCannotBypassRedaction(string name)
    {
        string encodedName = string.Concat(name.Select(c => $"\\u{(int)c:x4}"));
        string result = SensitiveDataScrubber.Scrub("{\"" + encodedName + "\"\n : \"private-secret\",\"status\":401}");
        using var json = System.Text.Json.JsonDocument.Parse(result);
        Assert.Equal("[REDACTED]", json.RootElement.GetProperty(name).GetString());
        Assert.Equal(401, json.RootElement.GetProperty("status").GetInt32());
        Assert.DoesNotContain("private-secret", result);
    }

    [Fact]
    public void LongEscapedProviderKeyNameKeepsTheSensitiveSuffix()
    {
        string name = new string('x', 10_000) + "\\u0041piKey";
        string result = SensitiveDataScrubber.Scrub("{\"" + name + "\":\"private-secret\",\"status\":401}");
        using var json = System.Text.Json.JsonDocument.Parse(result);
        Assert.Equal("[REDACTED]", json.RootElement.GetProperty(new string('x', 10_000) + "ApiKey").GetString());
        Assert.Equal(401, json.RootElement.GetProperty("status").GetInt32());
    }

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
