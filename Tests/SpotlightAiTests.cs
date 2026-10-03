using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Input;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotlightAiTests
{
    [Theory]
    [InlineData("OpenAI", "api.openai.com", "Authorization")]
    [InlineData("DeepSeek", "api.deepseek.com", "Authorization")]
    [InlineData("Gemini", "generativelanguage.googleapis.com", "x-goog-api-key")]
    [InlineData("Claude", "api.anthropic.com", "x-api-key")]
    public async Task UsesProviderEndpointCredentialsAndConversation(string provider, string host, string header)
    {
        var settings = Settings(provider);
        using var request = SpotlightAiService.CreateRequest(settings,
            [new("user", "first"), new("assistant", "answer"), new("user", "follow-up")]);
        Assert.Equal(host, request.RequestUri!.Host);
        Assert.Equal("https", request.RequestUri.Scheme);
        Assert.DoesNotContain("test-secret", request.RequestUri.ToString());
        Assert.Contains("test-secret", request.Headers.GetValues(header).Single());
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        var messages = body.RootElement.GetProperty(provider == "Gemini" ? "contents" : "messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal(provider == "Gemini" ? "model" : "assistant", messages[1].GetProperty("role").GetString());
        if (provider == "Claude")
        {
            Assert.True(body.RootElement.GetProperty("max_tokens").GetInt32() > 0);
            Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
        }
    }

    [Theory]
    [InlineData("OpenAI", "{\"choices\":[{\"message\":{\"content\":\"hello\"}}]}")]
    [InlineData("DeepSeek", "{\"choices\":[{\"message\":{\"content\":\"hello\"}}]}")]
    [InlineData("Gemini", "{\"candidates\":[{\"content\":{\"parts\":[{\"thought\":true,\"text\":\"private\"},{\"text\":\"hello\"}]}}]}")]
    [InlineData("Claude", "{\"content\":[{\"type\":\"thinking\",\"thinking\":\"private\"},{\"type\":\"text\",\"text\":\"hello\"}]}")]
    public async Task ReadsTextFromEachProvider(string provider, string body)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })));
        Assert.Equal("hello", await new SpotlightAiService(client).SendAsync(Settings(provider), [new("user", "hi")], default));
    }

    [Theory]
    [InlineData(401, "spotlight.ai.authError")]
    [InlineData(429, "spotlight.ai.rateError")]
    [InlineData(404, "spotlight.ai.modelError")]
    [InlineData(500, "spotlight.ai.serverError")]
    [InlineData(400, "spotlight.ai.requestError")]
    [InlineData(403, "spotlight.ai.permissionError")]
    [InlineData(402, "spotlight.ai.billingError")]
    [InlineData(413, "spotlight.ai.contextError")]
    [InlineData(504, "spotlight.ai.requestTimeout")]
    public async Task ErrorsDoNotExposeProviderResponse(int status, string error)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("sensitive response") })));
        var ex = await Assert.ThrowsAsync<SpotlightAiException>(() => new SpotlightAiService(client).SendAsync(Settings("OpenAI"), [new("user", "hi")], default));
        Assert.Equal(error, ex.Message);
        Assert.Equal($"HTTP {status}", ex.Diagnostic);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"choices\":[]}")]
    public async Task MalformedResponsesAreActionable(string body)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })));
        var ex = await Assert.ThrowsAsync<SpotlightAiException>(() => new SpotlightAiService(client).SendAsync(Settings("OpenAI"), [new("user", "hi")], default));
        Assert.Equal("spotlight.ai.emptyError", ex.Message);
    }

    [Theory]
    [InlineData("{\"code\":\"insufficient_quota\"}", "spotlight.ai.billingError")]
    [InlineData("{\"code\":\"context_length_exceeded\"}", "spotlight.ai.contextError")]
    [InlineData("{\"type\":\"overloaded_error\"}", "spotlight.ai.serverError")]
    [InlineData("{\"code\":403}", "spotlight.ai.permissionError")]
    [InlineData("{\"code\":{},\"type\":42}", "spotlight.ai.providerError")]
    [InlineData("{\"code\":\"secret\\r\\nforged log\"}", "spotlight.ai.providerError")]
    public async Task StreamErrorsAreClassifiedWithoutExposingPayload(string error, string key)
    {
        string body = "data: {\"error\":" + error + "}\n\n";
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })));
        var ex = await Assert.ThrowsAsync<SpotlightAiException>(async () =>
        {
            await foreach (var _ in new SpotlightAiService(client).StreamAsync(Settings("OpenAI"), [new("user", "private prompt")], default)) { }
        });
        Assert.Equal(key, ex.Message);
        Assert.DoesNotContain("secret", ex.ToString() + ex.Diagnostic);
        Assert.DoesNotContain("forged", ex.ToString() + ex.Diagnostic);
        Assert.DoesNotContain("private prompt", ex.ToString() + ex.Diagnostic);
    }

    [Fact]
    public async Task CancellationReachesHttpRequest()
    {
        using var cts = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SpotlightAiService(client).SendAsync(Settings("OpenAI"), [new("user", "hi")], cts.Token));
    }

    [Fact]
    public void KeysAreEncryptedAndExcludedFromExports()
    {
        var settings = new NotchSettings();
        foreach (string provider in SpotlightAiService.Providers)
            SpotlightAiService.Configure(settings, provider, "test-secret-" + provider, "model-id");
        string json = JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("test-secret", json);
        var restored = JsonSerializer.Deserialize<NotchSettings>(json)!;
        var service = new SettingsService(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString(), "settings.json"));
        string exported = service.ExportSettingsToString(settings);
        foreach (string provider in SpotlightAiService.Providers)
        {
            Assert.Equal(provider == SpotlightAiService.CopilotProvider ? "" : "test-secret-" + provider,
                SpotlightAiService.Configuration(restored, provider).Key);
            Assert.DoesNotContain("Spotlight" + provider + "ApiKey", exported);
        }
    }

    [Fact]
    public void OnlyUnmodifiedTabTogglesAi()
    {
        Assert.True(SpotlightWindow.IsAiToggle(Key.Tab, ModifierKeys.None));
        Assert.False(SpotlightWindow.IsAiToggle(Key.Tab, ModifierKeys.Shift));
        Assert.False(SpotlightWindow.IsAiToggle(Key.Enter, ModifierKeys.Control));
    }

    [Fact]
    public void MarkdownRenderer_AppliesMacDarkThemeTypography()
    {
        var doc = VNotch.Controls.AiMarkdown.Render("# Header\n```csharp\nvar x = 1;\n```\n> Quote\n`inline code`");
        Assert.NotNull(doc);
        Assert.True(doc.Blocks.Count >= 3);
    }

    private static NotchSettings Settings(string provider)
    {
        var s = new NotchSettings { SpotlightAiProvider = provider };
        SpotlightAiService.Configure(s, provider, "test-secret", "model-id");
        return s;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
