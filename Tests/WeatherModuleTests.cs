using System.Threading;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

public sealed class WeatherModuleTests
{
    [Fact]
    public void EnablingAfterHostStart_RefreshesImmediatelyWithPreviewSettings()
    {
        var persistedSettings = new NotchSettings
        {
            EnableWeather = false,
            ManualCity = string.Empty
        };
        var weatherService = new FakeWeatherService(CreateWeather("Hanoi"));
        using var module = new WeatherModule(weatherService, new FakeSettingsService(persistedSettings));

        WeatherInfo? update = null;
        module.WeatherUpdated += (_, e) => update = e.Weather;

        // The lifecycle host starts every module, even when weather is disabled.
        module.Start();
        Assert.Equal(0, weatherService.CallCount);

        module.OnSettingsChanged(new NotchSettings
        {
            EnableWeather = true,
            ManualCity = "Hanoi"
        });

        Assert.Equal(1, weatherService.CallCount);
        Assert.Equal("Hanoi", weatherService.LastManualCity);
        Assert.Equal("Hanoi", update?.City);
    }

    [Fact]
    public void ChangingManualCity_RefreshesOnce_AndIgnoresUnrelatedSettings()
    {
        var initialSettings = new NotchSettings
        {
            EnableWeather = true,
            ManualCity = "Hanoi"
        };
        var weatherService = new FakeWeatherService(CreateWeather("Hanoi"));
        using var module = new WeatherModule(weatherService, new FakeSettingsService(initialSettings));

        module.Start();
        Assert.Equal(1, weatherService.CallCount);

        var changedSettings = initialSettings.Clone();
        changedSettings.ManualCity = "Da Nang";
        module.OnSettingsChanged(changedSettings);

        Assert.Equal(2, weatherService.CallCount);
        Assert.Equal("Da Nang", weatherService.LastManualCity);

        changedSettings.Opacity = 0.8;
        module.OnSettingsChanged(changedSettings);

        Assert.Equal(2, weatherService.CallCount);
    }

    [Fact]
    public void ProviderFailure_RaisesNullUpdateForUnavailableState()
    {
        var settings = new NotchSettings { EnableWeather = true };
        var weatherService = new FakeWeatherService(null);
        using var module = new WeatherModule(weatherService, new FakeSettingsService(settings));

        WeatherUpdateEventArgs? update = null;
        module.WeatherUpdated += (_, e) => update = e;

        module.Start();

        Assert.NotNull(update);
        Assert.Null(update.Weather);
    }

    [Fact]
    public void LocaleRefresh_ReplaysCachedWeatherWithoutAnotherProviderRequest()
    {
        var settings = new NotchSettings { EnableWeather = true };
        var weatherService = new FakeWeatherService(CreateWeather("Ho Chi Minh City"));
        using var module = new WeatherModule(weatherService, new FakeSettingsService(settings));

        module.Start();
        WeatherInfo? replayed = null;
        module.WeatherUpdated += (_, e) => replayed = e.Weather;

        module.RefreshLocalization();

        Assert.Equal("Ho Chi Minh City", replayed?.City);
        Assert.Equal(1, weatherService.CallCount);
    }

    private static WeatherInfo CreateWeather(string city) => new()
    {
        City = city,
        Temperature = 30,
        High = 33,
        Low = 27,
        WeatherCode = 2,
        Condition = "Partly cloudy",
        IsDay = true
    };

    private sealed class FakeWeatherService(WeatherInfo? result) : IWeatherService
    {
        public int CallCount { get; private set; }
        public string? LastManualCity { get; private set; }

        public Task<WeatherInfo?> GetCurrentWeatherAsync(
            string? manualCity = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastManualCity = manualCity;
            return Task.FromResult(result);
        }
    }
}
