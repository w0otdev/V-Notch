using VNotch.Windows;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotifyLoginNavigationTests
{
    [Theory]
    [InlineData("https://accounts.spotify.com/en/login", true)]
    [InlineData("https://open.spotify.com/", true)]
    [InlineData("https://spotify.com/", true)]
    [InlineData("https://ACCOUNTS.SPOTIFY.COM/login", true)]
    [InlineData("https://evilspotify.com/login", false)]
    [InlineData("https://spotify.com.evil.example/login", false)]
    [InlineData("http://accounts.spotify.com/login", false)]
    [InlineData("https://accounts.spotify.com:444/login", false)]
    [InlineData("https://user@accounts.spotify.com/login", false)]
    [InlineData("file:///C:/payload.html", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("not a URL", false)]
    [InlineData(null, false)]
    public void LoginNavigation_RequiresAnHttpsSpotifyOrigin(string? value, bool allowed) =>
        Assert.Equal(allowed, SpotifyLoginWindow.IsAllowedNavigation(value));
}
