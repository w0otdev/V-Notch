using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class CurrencyConversionTests
{
    [Theory]
    [InlineData("US$1,234.56", "auto", "USD", "1234.56")]
    [InlineData("1.234,56 EUR", "auto", "EUR", "1234.56")]
    [InlineData("250.000 đ", "auto", "VND", "250000")]
    [InlineData("1 234,50 €", "auto", "EUR", "1234.50")]
    [InlineData("CA$ 50", "auto", "CAD", "50")]
    [InlineData("$50 CAD", "auto", "CAD", "50")]
    [InlineData("¥100 CNY", "auto", "CNY", "100")]
    [InlineData("¥1,200", "ja", "JPY", "1200")]
    [InlineData("￥120.50", "zh", "CNY", "120.50")]
    [InlineData("usd 0.99", "auto", "USD", "0.99")]
    [InlineData("CHF 1'234.50", "auto", "CHF", "1234.50")]
    [InlineData("1\u202f234,50 EUR", "auto", "EUR", "1234.50")]
    [InlineData("1000 dollar", "auto", "USD", "1000")]
    [InlineData("1000 dollars", "auto", "USD", "1000")]
    [InlineData("500.000 đồng", "auto", "VND", "500000")]
    [InlineData("50 euro", "auto", "EUR", "50")]
    [InlineData("1000 yen", "auto", "JPY", "1000")]
    public void ParsesWholePrices(string text, string language, string currency, string amount)
    {
        Assert.True(TranslationCurrencies.TryParse(text, language, out var price));
        Assert.Equal(currency, price.Currency);
        Assert.Equal(decimal.Parse(amount, CultureInfo.InvariantCulture), price.Amount);
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("Buy for $100 today")]
    [InlineData("$10 - $20")]
    [InlineData("$1,2,3")]
    [InlineData("$12,34,567")]
    [InlineData("$1.234.56")]
    [InlineData("$100 EUR")]
    [InlineData("2026-10-11")]
    [InlineData("¥100")]
    [InlineData("USD 1000000000000001")]
    [InlineData("USD -100")]
    [InlineData("USD 1+2")]
    public void RejectsAmbiguousOrNonPriceText(string text) => Assert.False(TranslationCurrencies.TryParse(text, "auto", out _));

    [Theory]
    [InlineData("vi", "VND")]
    [InlineData("en", "USD")]
    [InlineData("ja", "JPY")]
    [InlineData("zh", "CNY")]
    [InlineData("fr", "EUR")]
    public void MapsLanguageDefaults(string language, string currency) => Assert.Equal(currency, TranslationCurrencies.ForLanguage(language));

    [Theory]
    [InlineData("en-US", "USD")]
    [InlineData("en-CA", "CAD")]
    [InlineData("en-AU", "AUD")]
    [InlineData("en-SG", "SGD")]
    [InlineData("vi-VN", null)]
    public void BareDollarUsesCurrentRegion(string locale, string? expected)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(locale);
            Assert.Equal(expected != null, TranslationCurrencies.TryParse("$100", "en", out var price));
            if (expected != null) Assert.Equal(expected, price.Currency);
            Assert.True(TranslationCurrencies.TryParse("$100 CAD", "en", out var explicitPrice));
            Assert.Equal("CAD", explicitPrice.Currency);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void CurrencySettingsRoundTrip()
    {
        var settings = new NotchSettings { TranslationTargetCurrency = "EUR", EnableCurrencyConversion = false };
        var saved = System.Text.Json.JsonSerializer.Deserialize<NotchSettings>(System.Text.Json.JsonSerializer.Serialize(settings.Clone()))!;
        Assert.Equal("EUR", saved.TranslationTargetCurrency);
        Assert.False(saved.EnableCurrencyConversion);
        Assert.Equal("auto", new NotchSettings().TranslationTargetCurrency);
    }

    [Fact]
    public async Task FetchesOncePerDayPersistsAndOnlySendsPair()
    {
        using var f = new Fixture();
        var first = await f.Service.ConvertAsync(new(10.25m, "USD"), "VND", default);
        Assert.Equal(256250m, first.Amount);
        Assert.False(first.Cached);
        await f.Service.ConvertAsync(new(100m, "USD"), "VND", default);
        await f.NewService().ConvertAsync(new(1m, "USD"), "VND", default);
        Assert.Equal(1, f.Calls);
        Assert.Equal("https://api.frankfurter.dev/v2/rate/USD/VND", f.LastUri);
        f.Now = f.Now.AddDays(1);
        await f.Service.ConvertAsync(new(1m, "USD"), "VND", default);
        Assert.Equal(2, f.Calls);
    }

    [Fact]
    public async Task OfflineUsesRecentCacheButRejectsExpiredRate()
    {
        using var f = new Fixture();
        await f.Service.ConvertAsync(new(1m, "USD"), "VND", default);
        f.Now = f.Now.AddDays(1);
        f.Privacy.Apply(new NotchSettings { EnableLocalOnlyMode = true });
        var cached = await f.NewService().ConvertAsync(new(2m, "USD"), "VND", default);
        Assert.True(cached.Cached);
        Assert.Equal(new DateOnly(2026, 10, 11), cached.Rate!.Date);
        Assert.Equal(50000m, cached.Amount);
        f.Now = f.Now.AddDays(7);
        await Assert.ThrowsAsync<TranslationException>(() => f.Service.ConvertAsync(new(1m, "USD"), "VND", default));
        Assert.Equal(1, f.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrivacyPreventsNetworkEvenWithInjectedClient(bool localOnly)
    {
        using var f = new Fixture();
        f.Privacy.Apply(new NotchSettings { EnableLocalOnlyMode = localOnly, EnableCurrencyConversion = localOnly });
        await Assert.ThrowsAsync<TranslationException>(() => f.Service.ConvertAsync(new(1m, "USD"), "VND", default));
        Assert.Equal(0, f.Calls);
        var same = await f.Service.ConvertAsync(new(1m, "USD"), "USD", default);
        Assert.Equal(1m, same.Amount);
        Assert.Null(same.Rate);
    }

    [Theory]
    [InlineData("{\"base\":\"EUR\",\"quote\":\"VND\",\"rate\":25000,\"date\":\"2026-10-11\"}")]
    [InlineData("{\"base\":\"USD\",\"quote\":\"VND\",\"rate\":-1,\"date\":\"2026-10-11\"}")]
    [InlineData("{\"base\":\"USD\",\"quote\":\"VND\",\"rate\":25000,\"date\":\"2026-10-13\"}")]
    [InlineData("{\"base\":\"USD\",\"quote\":\"VND\",\"rate\":25000,\"date\":null}")]
    [InlineData("{}")]
    [InlineData("not json")]
    public async Task InvalidRatesNeverProduceResultAndRetriesAreBounded(string response)
    {
        using var f = new Fixture { Response = response };
        await Assert.ThrowsAsync<TranslationException>(() => f.Service.ConvertAsync(new(1m, "USD"), "VND", default));
        await Assert.ThrowsAsync<TranslationException>(() => f.Service.ConvertAsync(new(1m, "USD"), "VND", default));
        Assert.Equal(1, f.Calls);
    }

    [Fact]
    public async Task CancellationDoesNotFetchOrReturnCachedResult()
    {
        using var f = new Fixture();
        await f.Service.ConvertAsync(new(1m, "USD"), "VND", default);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.ConvertAsync(new(1m, "USD"), "VND", cancel.Token));
        Assert.Equal(1, f.Calls);
    }

    [Fact]
    public async Task FailedDailyRefreshUsesCacheAndBacksOff()
    {
        using var f = new Fixture();
        await f.Service.ConvertAsync(new(1m, "USD"), "VND", default);
        f.Now = f.Now.AddDays(1);
        f.Response = "unavailable";
        Assert.True((await f.Service.ConvertAsync(new(1m, "USD"), "VND", default)).Cached);
        Assert.True((await f.Service.ConvertAsync(new(1m, "USD"), "VND", default)).Cached);
        Assert.Equal(2, f.Calls);
        f.Response = null;
        f.Now = f.Now.AddMinutes(6);
        Assert.False((await f.Service.ConvertAsync(new(1m, "USD"), "VND", default)).Cached);
        Assert.Equal(3, f.Calls);
    }

    [Fact]
    public async Task CancellingAnInFlightFetchReleasesTheGate()
    {
        var privacy = new NetworkPrivacy();
        privacy.Apply(new NotchSettings());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new BlockingHandler(started));
        var service = new CurrencyRateService(client, privacy, Path.Combine(Path.GetTempPath(), "vnotch-fx-cancel-" + Guid.NewGuid()));
        using var cancellation = new CancellationTokenSource();
        var pending = service.ConvertAsync(new(1m, "USD"), "VND", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        privacy.Apply(new NotchSettings { EnableLocalOnlyMode = true });
        await Assert.ThrowsAsync<TranslationException>(() => service.ConvertAsync(new(1m, "USD"), "VND", default).WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private sealed class BlockingHandler(TaskCompletionSource started) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "vnotch-fx-tests-" + Guid.NewGuid().ToString("N"));
        internal DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        internal readonly NetworkPrivacy Privacy = new();
        internal readonly HttpClient Client;
        private CurrencyRateService? _service;
        internal CurrencyRateService Service => _service ??= NewService();
        internal int Calls;
        internal string? LastUri, Response;
        internal Fixture()
        {
            Privacy.Apply(new NotchSettings());
            Client = new(new Handler(request =>
            {
                Calls++; LastUri = request.RequestUri!.AbsoluteUri;
                return new(HttpStatusCode.OK) { Content = new StringContent(Response ?? $"{{\"base\":\"USD\",\"quote\":\"VND\",\"rate\":25000,\"date\":\"{Now:yyyy-MM-dd}\"}}") };
            }));
        }
        internal CurrencyRateService NewService() => new(Client, Privacy, _root, () => Now);
        public void Dispose() { Client.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
