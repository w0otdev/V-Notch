using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VNotch.Services.Translation;

internal sealed record CurrencyRate(string Base, string Quote, decimal Rate, DateOnly Date, DateOnly Checked);
internal sealed record CurrencyConversion(SelectedPrice Price, string Target, decimal Amount, CurrencyRate? Rate, bool Cached);

internal sealed partial class CurrencyRateService
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_currency_rate_validate(
        ReadOnlySpan<char> basePtr, int baseLen,
        ReadOnlySpan<char> quotePtr, int quoteLen,
        ReadOnlySpan<char> srcPtr, int srcLen,
        ReadOnlySpan<char> tgtPtr, int tgtLen,
        byte rateInBounds,
        int rateDateDayNumber,
        int rateCheckedDayNumber,
        int todayDayNumber,
        out byte outValid);

    private static readonly HttpClient Client = new(NetworkPrivacy.Handler(NetworkFeature.CurrencyRates,
        new NativeWinHttpHandler { AllowAutoRedirect = false }))
    { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 16384 };
    internal static CurrencyRateService Shared { get; } = new();
    private readonly HttpClient _client;
    private readonly NetworkPrivacy _privacy;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CurrencyRate> _rates = new();
    private readonly Dictionary<string, DateTimeOffset> _retryAfter = new();

    internal CurrencyRateService(HttpClient? client = null, NetworkPrivacy? privacy = null, string? root = null, Func<DateTimeOffset>? now = null)
    {
        _client = client ?? Client; _privacy = privacy ?? NetworkPrivacy.Current;
        _now = now ?? (() => DateTimeOffset.Now);
        _root = root ?? Path.Combine(AppPaths.LocalAppDataRoot, "currency-rates");
    }

    internal async Task<CurrencyConversion> ConvertAsync(SelectedPrice price, string target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (price.Amount < 0 || price.Amount > 1_000_000_000_000_000m || !TranslationCurrencies.Supported(price.Currency) || !TranslationCurrencies.Supported(target))
            throw new TranslationException("translation.currencyChoose");
        if (price.Currency == target) return new(price, target, price.Amount, null, false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            DateOnly today = DateOnly.FromDateTime(_now().DateTime);
            string key = price.Currency + "-" + target;
            if (!_rates.TryGetValue(key, out var rate)) rate = await ReadCacheAsync(key, ct).ConfigureAwait(false);
            if (rate != null && !Valid(rate, price.Currency, target, today)) rate = null;
            if (rate?.Checked == today) return Convert(price, target, rate, false);
            if (_privacy.IsAllowed(NetworkFeature.CurrencyRates) && (!_retryAfter.TryGetValue(key, out var retry) || retry <= _now()))
            {
                try
                {
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(ct, _privacy.Acquire(NetworkFeature.CurrencyRates));
                    request.CancelAfter(TimeSpan.FromSeconds(8));
                    using var response = await _client.GetAsync($"https://api.frankfurter.dev/v2/rate/{price.Currency}/{target}", request.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(request.Token).ConfigureAwait(false));
                    var row = json.RootElement;
                    var fresh = new CurrencyRate(row.GetProperty("base").GetString()!, row.GetProperty("quote").GetString()!,
                        row.GetProperty("rate").GetDecimal(), DateOnly.ParseExact(row.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture), today);
                    if (!Valid(fresh, price.Currency, target, today)) throw new FormatException();
                    ct.ThrowIfCancellationRequested();
                    _rates[key] = fresh;
                    await WriteCacheAsync(key, fresh, ct).ConfigureAwait(false);
                    return Convert(price, target, fresh, false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException or OverflowException or ArgumentException)
                { _retryAfter[key] = _now().AddMinutes(5); }
            }
            ct.ThrowIfCancellationRequested();
            if (rate != null) return Convert(price, target, rate, true);
            throw new TranslationException("translation.currencyUnavailable");
        }
        finally { _gate.Release(); }
    }

    private static bool Valid(CurrencyRate rate, string source, string target, DateOnly today)
    {
        byte inBounds = rate.Rate > 0 && rate.Rate <= 1_000_000_000m ? (byte)1 : (byte)0;
        return vn_currency_rate_validate(
            rate.Base.AsSpan(), rate.Base.Length,
            rate.Quote.AsSpan(), rate.Quote.Length,
            source.AsSpan(), source.Length,
            target.AsSpan(), target.Length,
            inBounds,
            rate.Date.DayNumber,
            rate.Checked.DayNumber,
            today.DayNumber,
            out byte valid) == 0 && valid != 0;
    }

    private static CurrencyConversion Convert(SelectedPrice price, string target, CurrencyRate rate, bool cached) =>
        new(price, target, decimal.Round(price.Amount * rate.Rate, TranslationCurrencies.DecimalPlaces(target), MidpointRounding.AwayFromZero), rate, cached);

    private async Task<CurrencyRate?> ReadCacheAsync(string key, CancellationToken ct)
    {
        try
        {
            var file = new FileInfo(Path.Combine(_root, key + ".json"));
            if (!file.Exists || file.Length > 16384) return null;
            var rate = JsonSerializer.Deserialize<CurrencyRate>(await File.ReadAllTextAsync(file.FullName, ct).ConfigureAwait(false));
            if (rate != null) _rates[key] = rate;
            return rate;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private async Task WriteCacheAsync(string key, CurrencyRate rate, CancellationToken ct)
    {
        string path = Path.Combine(_root, key + ".json"), temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(_root);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(rate), ct).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); } catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException) { /* Best-effort cleanup of temporary rate cache file. */ }
        }
    }
}
