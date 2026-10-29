using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VNotch.Services.Spotlight;

internal sealed partial record AiUsageSnapshot(long? Tokens = null, long? Remaining = null, long? Limit = null,
    string? Reset = null, DateTimeOffset? Updated = null)
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_ai_usage_sum_claude_tokens(
        long input,
        long output,
        long cacheRead,
        long cacheCreated,
        out long outTotal);

    internal static AiUsageSnapshot FromHeaders(HttpResponseMessage response, string provider)
    {
        string prefix = provider == "Claude" ? "anthropic-ratelimit-tokens-" : "x-ratelimit-";
        string? Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
        long? Number(string name) => long.TryParse(Header(name), out long n) && n >= 0 ? n : null;
        bool claude = provider == "Claude";
        return new(Remaining: Number(prefix + (claude ? "remaining" : "remaining-tokens")),
            Limit: Number(prefix + (claude ? "limit" : "limit-tokens")),
            Reset: Header(prefix + (claude ? "reset" : "reset-tokens")), Updated: DateTimeOffset.Now);
    }

    private static long? Number(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v)
        && v.TryGetInt64(out long n) && n >= 0 ? n : null;

    internal AiUsageSnapshot Read(JsonElement root, string provider)
    {
        if (provider == "Gemini" && root.TryGetProperty("usageMetadata", out var gemini))
            return this with { Tokens = Number(gemini, "totalTokenCount") ?? Tokens };
        if (provider != "Claude")
            return root.TryGetProperty("usage", out var usage)
                ? this with { Tokens = Number(usage, "total_tokens") ?? Tokens } : this;
        if (root.TryGetProperty("message", out var message)) root = message;
        if (!root.TryGetProperty("usage", out var claude)) return this;
        var input = Number(claude, "input_tokens") ?? Input;
        var output = Number(claude, "output_tokens") ?? Output;
        var cache = Number(claude, "cache_read_input_tokens") ?? CacheRead;
        var created = Number(claude, "cache_creation_input_tokens") ?? CacheCreated;
        _ = vn_ai_usage_sum_claude_tokens(input, output, cache, created, out long total);
        return this with
        {
            Input = input,
            Output = output,
            CacheRead = cache,
            CacheCreated = created,
            Tokens = total
        };
    }

    private long Input { get; init; }
    private long Output { get; init; }
    private long CacheRead { get; init; }
    private long CacheCreated { get; init; }
}

internal sealed partial class SpotlightAiService
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_ai_usage_validate_credential_key(
        ReadOnlySpan<char> keyPtr,
        int keyLen,
        out byte outValid);

    internal async Task<string> GetBalanceAsync(string key, CancellationToken token)
    {
        key = key.Trim();
        if (vn_ai_usage_validate_credential_key(key.AsSpan(), key.Length, out byte valid) != 0 || valid == 0)
            throw new InvalidOperationException("Invalid credential format.");
        const string balanceEndpoint = "api.deepseek.com/user/balance";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Uri.UriSchemeHttps}://{balanceEndpoint}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await _client.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        var balances = new List<string>();
        foreach (var item in json.RootElement.GetProperty("balance_infos").EnumerateArray())
        {
            string? currency = item.GetProperty("currency").GetString();
            if ((currency == "USD" || currency == "CNY") && decimal.TryParse(item.GetProperty("total_balance").GetString(),
                NumberStyles.Number, CultureInfo.InvariantCulture, out decimal amount))
                balances.Add($"{amount:N2} {currency}");
        }
        return balances.Count > 0 ? string.Join(" / ", balances) : "—";
    }
}
