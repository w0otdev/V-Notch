using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class WeatherServiceTests
{
    [Fact]
    public async Task CancelledBeforeRequest_ReturnsNull_AndSendsZeroRequests()
    {
        var handler = new StubHandler(_ => throw new Xunit.Sdk.XunitException("HTTP request was sent despite pre-cancellation"));
        var service = new WeatherService(new HttpClient(handler));

        var result = await service.GetCurrentWeatherAsync("   ", new CancellationToken(canceled: true));

        Assert.Null(result);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task ManualCity_UsesHttpsOnly_AndReturnsNullForInvalidLocation()
    {
        var handler = new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"results\":[]}", Encoding.UTF8, "application/json")
        });
        var service = new WeatherService(new HttpClient(handler));

        Assert.Null(await service.GetCurrentWeatherAsync("NonExistentCity12345XYZ"));
        Assert.Equal("https", handler.LastUri!.Scheme);
        Assert.Equal("geocoding-api.open-meteo.com", handler.LastUri.Host);
    }

    [Fact]
    public async Task CancelledRequest_ReturnsNull()
    {
        var handler = new StubHandler(_ => throw new OperationCanceledException());
        var service = new WeatherService(new HttpClient(handler));

        Assert.Null(await service.GetCurrentWeatherAsync("Hanoi", new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task ManualCity_ParsesGeocodingAndForecastResponses()
    {
        var handler = new StubHandler(request =>
        {
            string json = request.RequestUri!.Host == "geocoding-api.open-meteo.com"
                ? "{\"results\":[{\"name\":\"Hanoi\",\"latitude\":21.0245,\"longitude\":105.84117}]}"
                : "{\"current\":{\"temperature_2m\":30.6,\"weather_code\":2,\"is_day\":1}," +
                  "\"daily\":{\"temperature_2m_max\":[34.5],\"temperature_2m_min\":[27.6]}}";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        var service = new WeatherService(new HttpClient(handler));

        var weather = await service.GetCurrentWeatherAsync("Hanoi");

        Assert.NotNull(weather);
        Assert.Equal(2, handler.Requests);
        Assert.Equal("Hanoi", weather!.City);
        Assert.Equal(31, weather.Temperature);
        Assert.Equal(35, weather.High);
        Assert.Equal(28, weather.Low);
        Assert.Equal("Partly cloudy", weather.Condition);
        Assert.True(weather.IsDay);
    }

    [Fact]
    public async Task SubZeroTemperatures_AreRoundedAndPreservedProperly()
    {
        var handler = new StubHandler(request =>
        {
            string json = request.RequestUri!.Host == "geocoding-api.open-meteo.com"
                ? "{\"results\":[{\"name\":\"Harbin\",\"latitude\":45.75,\"longitude\":126.65}]}"
                : "{\"current\":{\"temperature_2m\":-18.4,\"weather_code\":71,\"is_day\":0}," +
                  "\"daily\":{\"temperature_2m_max\":[-14.2],\"temperature_2m_min\":[-22.8]}}";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        var service = new WeatherService(new HttpClient(handler));

        var weather = await service.GetCurrentWeatherAsync("Harbin");

        Assert.NotNull(weather);
        Assert.Equal("Harbin", weather!.City);
        Assert.Equal(-18, weather.Temperature);
        Assert.Equal(-14, weather.High);
        Assert.Equal(-23, weather.Low);
        Assert.Equal("Snow", weather.Condition);
        Assert.False(weather.IsDay);
    }

    [Fact]
    public async Task GeocodingHttpError_ReturnsNullWithoutThrowing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var service = new WeatherService(new HttpClient(handler));

        var result = await service.GetCurrentWeatherAsync("Tokyo");

        Assert.Null(result);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task ForecastHttpError_ReturnsNullWithoutThrowing()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "geocoding-api.open-meteo.com")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"results\":[{\"name\":\"Tokyo\",\"latitude\":35.68,\"longitude\":139.76}]}", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var service = new WeatherService(new HttpClient(handler));

        var result = await service.GetCurrentWeatherAsync("Tokyo");

        Assert.Null(result);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task MalformedForecastJson_ReturnsNullWithoutCrashing()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "geocoding-api.open-meteo.com")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"results\":[{\"name\":\"Paris\",\"latitude\":48.85,\"longitude\":2.35}]}", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"missing_current_property\": true}", Encoding.UTF8, "application/json")
            };
        });
        var service = new WeatherService(new HttpClient(handler));

        var result = await service.GetCurrentWeatherAsync("Paris");

        Assert.Null(result);
        Assert.Equal(2, handler.Requests);
    }

    [Theory]
    [InlineData(0, "Clear")]
    [InlineData(1, "Mainly clear")]
    [InlineData(2, "Partly cloudy")]
    [InlineData(3, "Cloudy")]
    [InlineData(45, "Fog")]
    [InlineData(61, "Rain")]
    [InlineData(71, "Snow")]
    [InlineData(95, "Thunderstorm")]
    [InlineData(999, "—")]
    public void DescribeWeatherCode_MapsStandardCodes(int code, string expected)
    {
        Assert.Equal(expected, WeatherService.DescribeWeatherCode(code));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            LastUri = request.RequestUri;
            return Task.FromResult(reply(request));
        }
    }
}
