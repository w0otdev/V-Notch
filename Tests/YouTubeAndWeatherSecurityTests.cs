using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class YouTubeAndWeatherSecurityTests
{
    [Theory]
    [InlineData("http://127.0.0.1:9999/caption")]
    [InlineData("https://evil.com/youtube.com")]
    [InlineData("https://www.youtube.com:8443/api/timedtext")]
    [InlineData("https://user@www.youtube.com/api/timedtext")]
    [InlineData("https://notyoutube.com/api/timedtext")]
    public void ParseCaptionTrackFromJson_RejectsUntrustedBaseUrls(string untrustedUrl)
    {
        string json = JsonSerializer.Serialize(new
        {
            baseUrl = untrustedUrl,
            languageCode = "en",
            vssId = ".en",
            name = new { simpleText = "English" }
        });

        using var doc = JsonDocument.Parse(json);
        var track = YouTubeSubtitleService.ParseCaptionTrackFromJson(doc.RootElement);

        Assert.False(YouTubeSubtitleService.IsAllowedCaptionBaseUrl(untrustedUrl));
        Assert.Null(track);
    }

    [Fact]
    public void ParseCaptionTrackFromJson_AcceptsValidYouTubeTimedTextUrl()
    {
        const string validUrl = "https://www.youtube.com/api/timedtext?v=abc";
        string json = JsonSerializer.Serialize(new
        {
            baseUrl = validUrl,
            languageCode = "en",
            vssId = ".en",
            name = new { simpleText = "English" }
        });

        using var doc = JsonDocument.Parse(json);
        var track = YouTubeSubtitleService.ParseCaptionTrackFromJson(doc.RootElement);

        Assert.True(YouTubeSubtitleService.IsAllowedCaptionBaseUrl(validUrl));
        Assert.NotNull(track);
        Assert.Equal(validUrl, track!.BaseUrl);
        Assert.Equal("en", track.LanguageCode);
    }

    [Fact]
    public void ParseTimedTextXml_WithDtdDeclaration_RejectsCleanlyWithoutThrowing()
    {
        const string maliciousXml =
            "<!DOCTYPE foo [<!ENTITY xxe \"test\">]>" +
            "<transcript><text start=\"0\" dur=\"1\">&xxe;</text></transcript>";

        var lines = YouTubeSubtitleService.ParseTimedTextXml(maliciousXml);

        Assert.NotNull(lines);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task ReadBoundedStringAsync_RejectsOversizedResponse()
    {
        const int maxBytes = 512 * 1024;
        byte[] oversizedPayload = new byte[maxBytes + 1024];
        Array.Fill(oversizedPayload, (byte)'a');

        using var handler = new DelegateHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(oversizedPayload, writable: false))
            };
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WeatherService.ReadBoundedStringAsync(client, "https://api.open-meteo.com/v1/forecast", maxBytes));
    }

    [Fact]
    public async Task WeatherService_GetCurrentWeatherAsync_RejectsOversizedResponse()
    {
        const int maxBytes = 512 * 1024;
        byte[] oversizedPayload = new byte[maxBytes + 1024];
        Array.Fill(oversizedPayload, (byte)' ');

        using var handler = new DelegateHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(oversizedPayload, writable: false))
            };
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        using var service = new WeatherService(client);

        var result = await service.GetCurrentWeatherAsync("London");

        Assert.Null(result);
    }

    private sealed class DelegateHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _sendAsync;

        public DelegateHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
        {
            _sendAsync = sendAsync;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _sendAsync(request, cancellationToken);
    }
}
