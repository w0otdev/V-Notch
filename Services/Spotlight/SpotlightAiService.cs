using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using VNotch.Models;

namespace VNotch.Services.Spotlight;

internal sealed record SpotlightAiMessage(string Role, string Content)
{
    public JsonElement[]? GeminiParts { get; init; }
    public bool IsIncomplete { get; init; }
}

internal sealed class SpotlightAiException(string resourceKey, string? diagnostic = null) : Exception(resourceKey)
{
    internal string? Diagnostic { get; } = diagnostic;
}

internal sealed partial class SpotlightAiService
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(90), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    private readonly HttpClient _client;
    internal SpotlightAiService(HttpClient? client = null) => _client = client ?? SharedClient;
    internal static readonly string[] Providers = ["OpenAI", "Gemini", "Claude", "DeepSeek"];

    internal static (string Key, string Model) Configuration(NotchSettings s, string provider) => provider switch
    {
        "OpenAI" => (s.SpotlightOpenAIApiKey, s.SpotlightOpenAIModel),
        "Gemini" => (s.SpotlightGeminiApiKey, s.SpotlightGeminiModel),
        "Claude" => (s.SpotlightClaudeApiKey, s.SpotlightClaudeModel),
        "DeepSeek" => (s.SpotlightDeepSeekApiKey, s.SpotlightDeepSeekModel),
        _ => ("", "")
    };

    internal static void Configure(NotchSettings s, string provider, string key, string model)
    {
        switch (provider)
        {
            case "OpenAI": s.SpotlightOpenAIApiKey = key; s.SpotlightOpenAIModel = model; break;
            case "Gemini": s.SpotlightGeminiApiKey = key; s.SpotlightGeminiModel = model; break;
            case "Claude": s.SpotlightClaudeApiKey = key; s.SpotlightClaudeModel = model; break;
            case "DeepSeek": s.SpotlightDeepSeekApiKey = key; s.SpotlightDeepSeekModel = model; break;
        }
    }

    internal static HttpRequestMessage CreateRequest(NotchSettings settings, IReadOnlyList<SpotlightAiMessage> history, bool streaming = false)
    {
        string provider = settings.SpotlightAiProvider;
        var (key, model) = Configuration(settings, provider);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(model))
            throw new SpotlightAiException("spotlight.ai.configure");
        key = key.Trim();
        // Reject invalid header values before HTTP code can include them in an exception.
        if (key.Length > 4096 || key.Any(c => c < '!' || c > '~'))
            throw new SpotlightAiException("spotlight.ai.authError");
        object body;
        string url;
        if (provider == "Gemini")
        {
            url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model.Trim())}:{(streaming ? "streamGenerateContent?alt=sse" : "generateContent")}";
            body = new { contents = history.Select(m => new { role = m.Role == "assistant" ? "model" : "user", parts = m.GeminiParts is { Length: > 0 } saved ? (object)saved : new[] { new { text = m.Content } } }) };
        }
        else
        {
            var messages = history.Select(m => new { role = m.Role, content = m.Content }).ToArray();
            url = provider switch
            {
                "Claude" => "https://api.anthropic.com/v1/messages",
                "DeepSeek" => "https://api.deepseek.com/chat/completions",
                "OpenAI" => "https://api.openai.com/v1/chat/completions",
                _ => throw new SpotlightAiException("spotlight.ai.configure")
            };
            body = provider == "Claude" ? (object)new { model = model.Trim(), max_tokens = 4096, messages, stream = streaming }
                : streaming ? (object)new { model = model.Trim(), messages, stream = true, stream_options = new { include_usage = true } }
                : new { model = model.Trim(), messages, stream = false };
        }
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        if (provider == "Gemini") request.Headers.Add("x-goog-api-key", key.Trim());
        else if (provider == "Claude")
        {
            request.Headers.Add("x-api-key", key.Trim());
            request.Headers.Add("anthropic-version", "2023-06-01");
        }
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        return request;
    }

    internal async IAsyncEnumerable<string> StreamAsync(NotchSettings settings,
        IReadOnlyList<SpotlightAiMessage> history, [EnumeratorCancellation] CancellationToken token, Action<JsonElement[]>? onGeminiParts = null, Action<AiUsageSnapshot>? onUsage = null)
    {
        using var response = await OpenStreamAsync(settings, history, token).ConfigureAwait(false);
        var usage = AiUsageSnapshot.FromHeaders(response, settings.SpotlightAiProvider);
        onUsage?.Invoke(usage);
        if (!response.IsSuccessStatusCode)
            throw ProviderError((int)response.StatusCode);
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var data = new StringBuilder();
        bool received = false;
        int total = 0;
        while (true)
        {
            string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line == null || line.Length == 0)
            {
                if (data.Length > 0)
                {
                    string payload = data.ToString();
                    data.Clear();
                    if (payload == "[DONE]") break;
                    using var json = JsonDocument.Parse(payload);
                    var root = json.RootElement;
                    usage = usage.Read(root, settings.SpotlightAiProvider);
                    onUsage?.Invoke(usage);
                    if (root.TryGetProperty("error", out var error))
                    {
                        int code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var value) && value.TryGetInt32(out int parsed) ? parsed : 0;
                        string? errorType = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("type", out var t) ? t.GetString() : null;
                        if (code == 0) code = errorType switch { "overloaded_error" => 503, "rate_limit_error" => 429, "authentication_error" => 401, "invalid_request_error" => 400, _ => 0 };
                        throw ProviderError(code, streaming: true);
                    }
                    if (settings.SpotlightAiProvider == "Gemini" &&
                        root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0 &&
                        candidates[0].TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
                        onGeminiParts?.Invoke(parts.EnumerateArray().Select(part => part.Clone()).ToArray());
                    string delta = ReadDelta(root, settings.SpotlightAiProvider);
                    if (delta.Length > 0)
                    {
                        total += delta.Length;
                        if (total > 200000) throw new SpotlightAiException("spotlight.ai.tooLong");
                        received = true;
                        yield return delta;
                    }
                    if (settings.SpotlightAiProvider == "Claude" &&
                        root.TryGetProperty("type", out var eventType) && eventType.GetString() == "message_stop") break;
                }
                if (line == null) break;
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart());
                if (data.Length > 1024 * 1024) throw new SpotlightAiException("spotlight.ai.emptyError");
            }
        }
        if (!received) throw new SpotlightAiException("spotlight.ai.emptyError");
    }

    private async Task<HttpResponseMessage> OpenStreamAsync(NotchSettings settings,
        IReadOnlyList<SpotlightAiMessage> history, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = CreateRequest(settings, history, streaming: true);
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            // Retry only explicit unavailability before any response text is consumed.
            if ((int)response.StatusCode != 503 || attempt >= 2) return response;
            var retryAfter = response.Headers.RetryAfter;
            var delay = retryAfter?.Delta ?? (retryAfter?.Date is { } date
                ? date - DateTimeOffset.UtcNow
                : TimeSpan.FromSeconds(2 * (1 << attempt) + Random.Shared.NextDouble()));
            // Respect a long server cooldown by returning the error instead of retrying early.
            if (delay > TimeSpan.FromSeconds(15)) return response;
            response.Dispose();
            RuntimeLog.Warn("SPOTLIGHT-AI", $"{settings.SpotlightAiProvider} HTTP 503; retry {attempt + 1}/2");
            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        }
    }

    private static SpotlightAiException ProviderError(int code, bool streaming = false) => new(code switch
    {
        401 or 403 => "spotlight.ai.authError",
        429 => "spotlight.ai.rateError",
        400 or 404 => "spotlight.ai.modelError",
        >= 500 => "spotlight.ai.serverError",
        _ => "spotlight.ai.providerError"
    }, code > 0 ? $"{(streaming ? "API" : "HTTP")} {code}" : "API stream error");

    private static string ReadDelta(JsonElement root, string provider)
    {
        if (provider == "Gemini")
        {
            if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0 ||
                !candidates[0].TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts)) return "";
            return string.Concat(parts.EnumerateArray().Where(p => p.TryGetProperty("text", out _) &&
                (!p.TryGetProperty("thought", out var thought) || thought.ValueKind != JsonValueKind.True))
                .Select(p => p.GetProperty("text").GetString()));
        }
        if (provider == "Claude")
        {
            return root.TryGetProperty("delta", out var delta) && delta.TryGetProperty("type", out var type) &&
                type.GetString() == "text_delta" && delta.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
        }
        return root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("delta", out var d) && d.TryGetProperty("content", out var c) &&
            c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
    }

    internal async Task<string> SendAsync(NotchSettings settings, IReadOnlyList<SpotlightAiMessage> history, CancellationToken token)
    {
        using var request = CreateRequest(settings, history);
        using var response = await _client.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new SpotlightAiException((int)response.StatusCode switch
            {
                401 or 403 => "spotlight.ai.authError",
                429 => "spotlight.ai.rateError",
                400 or 404 => "spotlight.ai.modelError",
                _ => "spotlight.ai.networkError"
            });
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            var root = json.RootElement;
            string? text = settings.SpotlightAiProvider switch
            {
                "Gemini" => string.Concat(root.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")
                    .EnumerateArray().Where(p => p.TryGetProperty("text", out _) &&
                        (!p.TryGetProperty("thought", out var thought) || thought.ValueKind != JsonValueKind.True))
                    .Select(p => p.GetProperty("text").GetString())),
                "Claude" => string.Concat(root.GetProperty("content").EnumerateArray()
                    .Where(p => p.GetProperty("type").GetString() == "text").Select(p => p.GetProperty("text").GetString())),
                _ => root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
            };
            if (string.IsNullOrWhiteSpace(text)) throw new SpotlightAiException("spotlight.ai.emptyError");
            return text;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        { throw new SpotlightAiException("spotlight.ai.emptyError"); }
    }
}
