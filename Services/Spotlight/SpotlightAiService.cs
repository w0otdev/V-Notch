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
        RuntimeLog.Debug("SPOTLIGHT-AI", $"response; HTTP {(int)response.StatusCode}");
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
                        throw StreamError(error);
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
            RuntimeLog.Debug("SPOTLIGHT-AI", $"stream request; attempt={attempt + 1}");
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            RuntimeLog.Debug("SPOTLIGHT-AI", $"stream response; attempt={attempt + 1}; HTTP {(int)response.StatusCode}");
            // Retry only explicit unavailability before any response text is consumed.
            if ((int)response.StatusCode != 503 || attempt >= 2) return response;
            var retryAfter = response.Headers.RetryAfter;
            var delay = retryAfter?.Delta ?? (retryAfter?.Date is { } date
                ? date - DateTimeOffset.UtcNow
                : TimeSpan.FromSeconds(2 * (1 << attempt) + Random.Shared.NextDouble()));
            // Respect a long server cooldown by returning the error instead of retrying early.
            if (delay > TimeSpan.FromSeconds(15)) return response;
            response.Dispose();
            RuntimeLog.Warn("SPOTLIGHT-AI", $"HTTP 503; retry {attempt + 1}/2");
            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        }
    }

    private static SpotlightAiException ProviderError(int code, bool streaming = false) => new(code switch
    {
        401 => "spotlight.ai.authError",
        403 => "spotlight.ai.permissionError",
        402 => "spotlight.ai.billingError",
        408 or 504 => "spotlight.ai.requestTimeout",
        413 => "spotlight.ai.contextError",
        429 => "spotlight.ai.rateError",
        400 or 422 => "spotlight.ai.requestError",
        404 => "spotlight.ai.modelError",
        >= 500 => "spotlight.ai.serverError",
        _ => "spotlight.ai.providerError"
    }, code > 0 ? $"{(streaming ? "API" : "HTTP")} {code}" : "API stream error");

    // Only fixed labels and numeric status codes may cross into UI/log diagnostics.
    // Never include provider messages, request URLs, headers, model IDs or chat text.
    internal static SpotlightAiException StreamError(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object) return ProviderError(0, true);
        int code = error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int parsed) && parsed is >= 100 and <= 599 ? parsed : 0;
        string? label = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (label == null && error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            label = type.GetString();
        if (label == null && error.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
            label = status.GetString();
        string? key = label switch
        {
            "insufficient_quota" => "spotlight.ai.billingError",
            "context_length_exceeded" => "spotlight.ai.contextError",
            _ => null
        };
        if (key != null) return new SpotlightAiException(key, "API " + label);
        if (code == 0) code = label switch
        {
            "overloaded_error" or "UNAVAILABLE" => 503,
            "rate_limit_error" or "rate_limit_exceeded" or "RESOURCE_EXHAUSTED" => 429,
            "authentication_error" or "invalid_api_key" or "UNAUTHENTICATED" => 401,
            "permission_error" or "PERMISSION_DENIED" => 403,
            "invalid_request_error" or "INVALID_ARGUMENT" => 400,
            "not_found_error" or "model_not_found" or "NOT_FOUND" => 404,
            "request_too_large" => 413,
            "DEADLINE_EXCEEDED" => 504,
            "api_error" or "INTERNAL" => 500,
            _ => 0
        };
        return ProviderError(code, true);
    }

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
        RuntimeLog.Debug("SPOTLIGHT-AI", "non-stream request started");
        using var request = CreateRequest(settings, history);
        using var response = await _client.SendAsync(request, token).ConfigureAwait(false);
        RuntimeLog.Debug("SPOTLIGHT-AI", $"response; HTTP {(int)response.StatusCode}");
        if (!response.IsSuccessStatusCode)
            throw ProviderError((int)response.StatusCode);
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
