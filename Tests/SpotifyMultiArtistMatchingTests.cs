using System.Text.Json;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotifyMultiArtistMatchingTests
{
    [Theory]
    [InlineData("Beyoncé", "JAY-Z", "Beyonce Jay Z")]
    [InlineData(" First ", "Second!", "First & Second")]
    [InlineData("!!!", "Second", "Second")]
    public void CombinedAndIndividualArtistNamesAreMatchedAfterNormalization(string first, string second, string expected)
        => Assert.Equal("1234567890abcdefghijkl", Match([first, second], expected));

    [Fact]
    public void LargeCombinedNamesUseTheSameMatchingRules()
    {
        string first = new('a', 400);
        string second = new('b', 400);
        Assert.Equal("1234567890abcdefghijkl", Match([first, second], first + " " + second));
        Assert.Null(Match([first, second], "Unrelated artist"));
    }

    private static string? Match(string[] names, string expected)
    {
        string json = JsonSerializer.Serialize(new
        {
            data = new
            {
                searchV2 = new
                {
                    tracksV2 = new
                    {
                        items = new[]
        {
            new { item = new { data = new { id = "1234567890abcdefghijkl", name = "Test song", artists = new { items = names.Select(name => new { profile = new { name } }).ToArray() } } } }
        }
                    }
                }
            }
        });
        return SpotifyTrackMatcher.ParsePathfinderTrackId(json, "Test song", expected);
    }
}
