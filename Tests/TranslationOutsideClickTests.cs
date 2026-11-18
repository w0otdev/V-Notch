using System.Windows;
using System.Windows.Controls.Primitives;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class TranslationOutsideClickTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OutsideClickDismissesInactiveLoadingAndResultWindows(bool loading, bool reduced) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reduced);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        int dismissed = 0;
        window.Dismissed += () => dismissed++;
        try
        {
            uint openingClick = unchecked((uint)Environment.TickCount);
            window.ShowSelection(new(1, "Original text", new Rect(200, 200, 100, 20), IntPtr.Zero, false), true);
            await window.WaitForEntranceAsync(ct);
            if (!loading) window.ShowResult(new("Translated result", "en", "vi", true));
            Assert.False(window.IsActive);
            Assert.True(InputMonitorService.IsStarted);
            var inside = window.PopupSurface.PointToScreen(new Point(50, 50));
            var outside = window.PopupSurface.PointToScreen(new Point(-10, -10));
            Click(outside, openingClick);
            Click(inside);
            Assert.Equal(0, dismissed);
            Assert.True(window.IsVisible);

            Click(outside);
            Assert.Equal(1, dismissed);
            Assert.False(window.LoadingIndicator.IsAnimating);
            await WpfFrameWaiter.UntilAsync(() => !window.IsVisible, "outside click completes the normal dismiss animation", ct);
            Assert.False(InputMonitorService.IsStarted);
            Click(outside);
            Assert.Equal(1, dismissed);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LanguageDropdownClicksSurviveBothBeforeAndAfterTheMenuCloses(bool sourceLanguage) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.ShowClipboard("Original text");
            await window.WaitForEntranceAsync(ct);
            var combo = sourceLanguage ? window.SourceLanguageCombo : window.TargetLanguageCombo;
            combo.IsDropDownOpen = true;
            var popup = Assert.IsType<Popup>(combo.Template.FindName("PART_Popup", combo));
            var child = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
            await WpfFrameWaiter.UntilAsync(() => child.IsVisible && child.ActualHeight > 0, "language menu opens", ct);
            var inside = child.PointToScreen(new Point(child.ActualWidth / 2, child.ActualHeight - 8));
            Click(inside);
            Assert.True(window.IsVisible);
            Assert.True(combo.IsDropDownOpen);

            uint menuClick = unchecked((uint)Environment.TickCount);
            combo.IsDropDownOpen = false;
            Click(inside, menuClick);
            Assert.True(window.IsVisible);
            await WpfFrameWaiter.NextAsync(ct);
            combo.IsDropDownOpen = true;
            await WpfFrameWaiter.UntilAsync(() => child.IsVisible, "language menu reopens", ct);
            Click(window.PopupSurface.PointToScreen(new Point(-40, -40)));
            await WpfFrameWaiter.UntilAsync(() => !window.IsVisible, "outside both menu and window dismisses translation", ct);
            Assert.False(combo.IsDropDownOpen);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void OldClicksCannotDismissANewPresentationAndHiddenWindowsUnsubscribe() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.ShowSelection(new(1, "First selection", new Rect(200, 200, 100, 20), IntPtr.Zero, false), true);
            await window.WaitForEntranceAsync(ct);
            uint oldClick = unchecked((uint)Environment.TickCount);
            await WpfFrameWaiter.NextAsync(ct);
            window.ShowSelection(new(2, "New selection", new Rect(300, 200, 100, 20), IntPtr.Zero, false), true);
            await window.WaitForEntranceAsync(ct);
            Click(window.PopupSurface.PointToScreen(new Point(-10, -10)), oldClick);
            Assert.True(window.IsVisible);
            Assert.Equal("New selection", window.OriginalText.Text);
            window.Hide();
            Assert.False(InputMonitorService.IsStarted);
            window.Show();
            await WpfFrameWaiter.NextAsync(ct);
            Assert.True(InputMonitorService.IsStarted);
            Click(window.PopupSurface.PointToScreen(new Point(-10, -10)));
            await WpfFrameWaiter.UntilAsync(() => !window.IsVisible, "reopened window handles outside clicks", ct);
            Assert.False(InputMonitorService.IsStarted);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); }
    });

    // A Rendering callback can run in the same TickCount tick as Show/ShowSelection.
    // Unspecified timestamps represent a subsequent click, not the opening click.
    private static void Click(Point point, uint? timestamp = null) => InputMonitorService.DispatchClick(
        new InputMonitorService.POINT { x = (int)point.X, y = (int)point.Y },
        timestamp ?? unchecked((uint)Environment.TickCount + 1));
}
