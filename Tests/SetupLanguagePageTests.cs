using System;
using System.Runtime.ExceptionServices;
using System.Threading;
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
            if (Application.Current == null) new Application();
            var font = SetupFonts.SFProDisplayFont;
            Assert.Contains("SF Pro Display", font.FamilyNames.Values);
        });
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error != null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
