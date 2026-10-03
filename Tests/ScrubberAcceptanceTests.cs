using System.Diagnostics;
using System.Text.Json;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class ScrubberAcceptanceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullAndEmptyInputReturnEmptyText(string? input)
        => Assert.Equal(string.Empty, SensitiveDataScrubber.Scrub(input));

    [Theory]
    [InlineData("password: anh yeu em 123", "password: [REDACTED]")]
    [InlineData("pwd=秘密の合言葉 🌸 123", "pwd=[REDACTED]")]
    [InlineData("password=\"a, b; c \\\" d\"; request=42", "password=\"[REDACTED]\"; request=42")]
    [InlineData("password='two words'; status=failed", "password='[REDACTED]'; status=failed")]
    [InlineData("ERROR auth\npassword: anh yeu em 123\nrequest=42; retry=false", "ERROR auth\npassword: [REDACTED]\nrequest=42; retry=false")]
    [InlineData("password: \r\nrequest=42", "password: [REDACTED]\r\nrequest=42")]
    [InlineData("password=\"unfinished secret\nmore secret", "password=\"[REDACTED]\"")]
    [InlineData("password: words, more; words", "password: [REDACTED]")]
    [InlineData("password=[REDACTED]secret tail", "password=[REDACTED]")]
    [InlineData("api_key=[REDACTED]secret-tail; request=42", "api_key=[REDACTED]; request=42")]
    [InlineData("-PASSWORD: secret words\nrequest=42", "-PASSWORD: [REDACTED]\nrequest=42")]
    public void WholePasswordValueIsRedactedAndDelimitedContextSurvives(string input, string expected)
    {
        Assert.Equal(expected, SensitiveDataScrubber.Scrub(input));
        Assert.Equal(expected, SensitiveDataScrubber.Scrub(expected));
    }

    [Theory]
    [InlineData("{\"password\": \"\", \"status\": 401}")]
    [InlineData("{\"password\": null, \"status\": 401}")]
    [InlineData("{\"password\": \"秘密 🌸 with spaces\", \"status\": 401}")]
    [InlineData("{\"PASSWORD\": \"quote \\\" and slash \\\\ inside\", \"status\": 401}")]
    [InlineData("{\"password\":\n  \"日本語\", \"status\": 401}")]
    [InlineData("{\"password\": {\"secret\": [\"nested, value\", null]}, \"status\": 401}")]
    public void JsonPasswordIncludingEmptyAndNullBecomesRedactedString(string input)
    {
        string output = SensitiveDataScrubber.Scrub(input);
        using var document = JsonDocument.Parse(output);
        var password = document.RootElement.EnumerateObject().First(p => p.Name.Equals("password", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("[REDACTED]", password.Value.GetString());
        Assert.Equal(401, document.RootElement.GetProperty("status").GetInt32());
    }

    [Theory]
    [InlineData("api_key=")]
    [InlineData("Authorization: Bearer ")]
    [InlineData("sk-")]
    [InlineData("AIza")]
    [InlineData("enc:")]
    [InlineData("sp_dc=")]
    public void TenThousandCharacterTokenHasNoUnredactedTail(string prefix)
    {
        string token = new string('A', 10_000);
        string input = "WARN 日本語 🌸 " + prefix + token + "; request=42";
        string output = SensitiveDataScrubber.Scrub(input);
        Assert.DoesNotContain(new string('A', 32), output);
        Assert.Contains("WARN 日本語 🌸", output);
        Assert.Contains("request=42", output);
    }

    [Fact]
    public void LargeEntryPreservesDebugContextAndStillRedactsAllSecrets()
    {
        string noise = new string('a', 50_000) + "!";
        string input = "ERROR request=42 日本語 🌸\npassword: two secret words\n"
            + "{\"password\":null,\"status\":401}\nAuthorization: Bearer private-token\n"
            + noise + "\n at App.Authenticate()";
        string result = SensitiveDataScrubber.Scrub(input);
        Assert.Contains("ERROR request=42 日本語 🌸", result);
        Assert.Contains("\"status\":401", result);
        Assert.Contains(" at App.Authenticate()", result);
        Assert.DoesNotContain("secret words", result);
        Assert.DoesNotContain("private-token", result);
    }

    [Fact]
    public void LongHyphenatedIdentifierDoesNotCauseQuadraticRetries()
    {
        string input = string.Concat(Enumerable.Repeat("a-", 100_000)) + "!\napi_key=private-key; request=42";
        var timer = Stopwatch.StartNew();
        string result = SensitiveDataScrubber.Scrub(input);
        Assert.DoesNotContain("private-key", result);
        Assert.Contains("request=42", result);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"Linear scan took {timer.Elapsed}.");
    }
}
