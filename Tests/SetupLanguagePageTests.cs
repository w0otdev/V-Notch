using System;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SetupLanguagePageTests
{
    [Fact]
    public void EveryLanguageFlagLoadsFromTheProductAssembly() => RunOnStaThread(() =>
    {
        BackgroundTestWindows.EnsureApplicationResources();
        var page = new LanguagePage();
        page.Measure(new Size(400, 400));
        page.Arrange(new Rect(0, 0, 400, 400));
        page.UpdateLayout();
        var images = Descendants(page).OfType<System.Windows.Controls.Image>().ToArray();
        Assert.Equal(17, images.Length);
        Assert.All(images, image =>
        {
            var bitmap = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(image.Source);
            Assert.True(bitmap.PixelWidth > 1 && bitmap.PixelHeight > 1, "A language flag must not use the empty fallback image.");
        });
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

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
