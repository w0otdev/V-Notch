using System.Text.Json;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotifyTrackMatcherRegressionTests
{
    private const string Id = "3OHfY25tqY28d16oZczHc8";

    [Theory]
    [InlineData("A", "Alan Walker", false)]
    [InlineData("I", "Kill Bill", false)]
    [InlineData("In", "In The End", false)]
    [InlineData("Go", "Gone", false)]
    [InlineData("Rain", "Rainbow", false)]
    [InlineData("A", "A", true)]
    [InlineData("Go", "Go", true)]
    [InlineData("Kill Bill", "Kill Bill (Live)", true)]
    [InlineData("Beyoncé", "Beyonce", true)]
    public void TitleMatchingRejectsShortAndPartialWordsInBothDirections(string title, string expected, bool match)
    {
        foreach (var (candidate, wanted) in new[] { (title, expected), (expected, title) })
        {
            string json = JsonSerializer.Serialize(new { id = Id, name = candidate, artist = "SZA" });
            Assert.Equal(match ? Id : null, SpotifyTrackMatcher.ParseTrackId(json, wanted, "SZA", TimeSpan.Zero));
            Assert.Equal(match ? Id : null, SpotifyTrackMatcher.ParsePathfinderTrackId(Pathfinder(candidate, "SZA"), wanted, "SZA"));
        }
    }

    [Theory]
    [InlineData("A", "Alan Walker", false)]
    [InlineData("SZA", "SZARD", false)]
    [InlineData("SZA", "SZA", true)]
    [InlineData("Alan Walker", "Alan Walker feat. SZA", true)]
    public void ArtistMatchingUsesTheSameWordBoundaries(string candidate, string expected, bool match)
    {
        Assert.Equal(match ? Id : null, SpotifyTrackMatcher.ParsePathfinderTrackId(Pathfinder("Faded", candidate), "Faded", expected));
    }

    private static string Pathfinder(string title, string artist) => JsonSerializer.Serialize(new
    {
        data = new
        {
            searchV2 = new
            {
                tracksV2 = new
                {
                    items = new[] { new { item = new { data = new { id = Id, name = title, artists = new { items = new[] { new { profile = new { name = artist } } } } } } } }
                }
            }
        }
    });
}
