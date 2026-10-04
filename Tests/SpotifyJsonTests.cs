using System.Text.Json;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotifyJsonTests
{
    [Theory]
    [InlineData("name", "duration")]
    [InlineData("NAME", "DURATION")]
    public void RecursiveLookupsRetainCaseInsensitiveProviderCompatibility(string titleKey, string durationKey)
    {
        using var document = JsonDocument.Parse($$"""{"nested":[{"{{titleKey}}":"Caption","{{durationKey}}":153}]}""");
        Assert.Equal("Caption", SpotifyJson.FindStringProperty(document.RootElement, "name", 0));
        Assert.Equal(153d, SpotifyJson.FindNumberProperty(document.RootElement, "duration", 0));
        var item = document.RootElement.GetProperty("nested")[0];
        Assert.True(SpotifyJson.TryGetProperty(item, "name", out var name));
        Assert.Equal("Caption", name.GetString());
    }

    [Fact]
    public void ExactPropertyLookupsDoNotAllocateStringsForUnrelatedKeys()
    {
        using var document = JsonDocument.Parse("{\"unrelated\":\"ignored\",\"name\":\"Caption\"}");
        var element = document.RootElement;
        SpotifyJson.TryGetProperty(element, "name", out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) SpotifyJson.TryGetProperty(element, "name", out _);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
