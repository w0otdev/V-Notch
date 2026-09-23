using Xunit;

namespace VNotch.Tests;

public class DesktopLayerInteractionTests
{
    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, false, false, true)]
    [InlineData(true, true, true, true, true)]
    public void ShouldKeepDesktopPromotion_WhenAnyInteractionIsActive(
        bool pointerInHoverZone,
        bool pointerOverNotch,
        bool inputCapturedWithin,
        bool keyboardFocusWithin,
        bool ownedWindowInteractionActive)
    {
        bool keepPromoted = MainWindow.ShouldKeepDesktopPromotion(
            pointerInHoverZone,
            pointerOverNotch,
            inputCapturedWithin,
            keyboardFocusWithin,
            ownedWindowInteractionActive);

        Assert.True(keepPromoted);
    }

    [Fact]
    public void ShouldKeepDesktopPromotion_WhenInteractionEnds_ReturnsFalse()
    {
        bool keepPromoted = MainWindow.ShouldKeepDesktopPromotion(
            pointerInHoverZone: false,
            pointerOverNotch: false,
            inputCapturedWithin: false,
            keyboardFocusWithin: false,
            ownedWindowInteractionActive: false);

        Assert.False(keepPromoted);
    }

    [Theory]
    [InlineData(false, true, true, true, false)] // Collapsed -> always false
    [InlineData(false, true, false, false, false)] // Collapsed -> always false
    [InlineData(true, false, true, true, false)] // Not foreground -> false
    [InlineData(true, false, false, false, false)] // Not foreground -> false
    [InlineData(true, true, true, false, true)] // Expanded, foreground, keyboard input enabled -> true
    [InlineData(true, true, false, true, true)] // Expanded, foreground, text box focused -> true
    [InlineData(true, true, false, false, false)] // Expanded, foreground, but no text box or input enabled -> false
    public void DetermineActiveKeyboardFocusInteraction_ValidatesStateCorrectly(
        bool isExpanded,
        bool isForeground,
        bool isKeyboardInputEnabled,
        bool isTextBoxFocused,
        bool expected)
    {
        bool result = MainWindow.DetermineActiveKeyboardFocusInteraction(
            isExpanded,
            isForeground,
            isKeyboardInputEnabled,
            isTextBoxFocused);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(true, 0, true)]   // Greeting is active -> hold active regardless of time
    [InlineData(true, -10, true)] // Greeting is active -> hold active even if timestamp expired
    [InlineData(false, 3, true)]  // Greeting false, but hold until is 3s in future -> hold active
    [InlineData(false, -1, false)] // Greeting false, hold expired 1s ago -> hold inactive
    [InlineData(false, 0, false)]  // Greeting false, hold at exactly current time -> hold inactive
    public void IsStartupHoldActive_ValidatesStateCorrectly(
        bool isGreetingActive,
        int secondsFromNow,
        bool expected)
    {
        var now = new System.DateTime(2026, 9, 22, 12, 0, 0, System.DateTimeKind.Utc);
        var holdUntil = now.AddSeconds(secondsFromNow);

        bool result = MainWindow.IsStartupHoldActive(isGreetingActive, holdUntil, now);

        Assert.Equal(expected, result);
    }
}
