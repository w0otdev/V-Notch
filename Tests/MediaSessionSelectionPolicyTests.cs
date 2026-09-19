using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class MediaSessionSelectionPolicyTests
{
    // Equal app IDs still represent distinct browser tab sessions.
    private sealed record Session(string AppId);

    [Fact]
    public void SimultaneousPlayback_UsesExactOsSessionRegardlessOfEnumerationOrder()
    {
        var firstTab = new Session("chrome");
        var currentTab = new Session("chrome");

        Assert.Same(currentTab, MediaSessionSelectionPolicy.SelectNewlyPlayingSession(
            new[] { firstTab, currentTab }, currentTab));
        Assert.Same(currentTab, MediaSessionSelectionPolicy.SelectNewlyPlayingSession(
            new[] { currentTab, firstTab }, currentTab));
    }

    [Fact]
    public void SingleNewPlayback_WinsOverPreviouslyCurrentTab()
    {
        var oldTab = new Session("msedge");
        var newTab = new Session("msedge");

        Assert.Same(newTab, MediaSessionSelectionPolicy.SelectNewlyPlayingSession(
            new[] { newTab }, oldTab));
    }

    [Fact]
    public void AmbiguousPlayback_DoesNotInventRecencyFromEnumerationOrder()
    {
        var tabs = new[] { new Session("chrome"), new Session("chrome") };

        Assert.Null(MediaSessionSelectionPolicy.SelectNewlyPlayingSession(tabs, new Session("chrome")));
        Assert.Null(MediaSessionSelectionPolicy.SelectNewlyPlayingSession(tabs, null));
        Assert.Null(MediaSessionSelectionPolicy.SelectNewlyPlayingSession(Array.Empty<Session>(), tabs[0]));
    }
}
