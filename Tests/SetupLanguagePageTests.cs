using System;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace VNotch.Tests;

public sealed class SetupLanguagePageTests
{
    [Fact]
    public void HindiRemainsReachableWhenTheSetupContentAreaIsShort()
    {
        RunOnStaThread(() =>
        {
            var page = new LanguagePage();
            page.Measure(new Size(400, 250));
            page.Arrange(new Rect(0, 0, 400, 250));
            page.UpdateLayout();

            Assert.True(page.HasLanguageOption("hi"));
            Assert.Equal(System.Windows.Controls.ScrollBarVisibility.Auto,
                page.LanguageListScrollViewer.VerticalScrollBarVisibility);
            Assert.True(page.LanguageListScrollViewer.ScrollableHeight > 0);

            page.LanguageListScrollViewer.ScrollToEnd();
            page.UpdateLayout();
            Assert.True(page.LanguageListScrollViewer.VerticalOffset > 0);
        });
    }

    [Fact]
    public void SetupFonts_SFProDisplayFont_ResolvesToSFProDisplay()
    {
        RunOnStaThread(() =>
        {
            BackgroundTestWindows.EnsureApplicationResources();
            var font = SetupFonts.SFProDisplayFont;
            Assert.Contains("SF Pro Display", font.FamilyNames.Values);
        });
    }

    // Application and native tray controls share the suite's persistent dispatcher.
    private static void RunOnStaThread(Action action) => SharedStaTestRunner.Run(action);
}
