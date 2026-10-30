using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SetupProgressLayoutTests
{
    [Theory]
    [InlineData(544)]
    [InlineData(264)]
    public void StatusSharesTheHeadingAndProgressBarLeadingEdge(double width) => SharedStaTestRunner.Run(() =>
    {
        BackgroundTestWindows.EnsureApplicationResources();
        var page = new InstallProgressPage();
        var elements = page.GetAnimatedElements();
        var heading = Assert.IsType<TextBlock>(elements[0]);
        var status = Assert.IsType<TextBlock>(elements[1]);
        var progress = Assert.IsType<ProgressBar>(elements[2]);
        foreach (string text in new[] { "Preparing...", "Installing runtimes/win-x64/native/vulkan/ggml-vulkan.dll..." })
        {
            page.ShowIndeterminate(text);
            Arrange(page, width);
            Assert.Equal(heading.TranslatePoint(new Point(), page).X, status.TranslatePoint(new Point(), page).X, 2);
            Assert.Equal(progress.TranslatePoint(new Point(), page).X, status.TranslatePoint(new Point(), page).X, 2);
            Assert.InRange(status.ActualWidth, 1, width);
        }
    });

    [Fact]
    public void ProgressBarStaysInPlaceWhenTheStatusWrapsOntoASecondLine() => SharedStaTestRunner.Run(() =>
    {
        BackgroundTestWindows.EnsureApplicationResources();
        var page = new InstallProgressPage();
        var elements = page.GetAnimatedElements();
        var status = Assert.IsType<TextBlock>(elements[1]);
        var progress = Assert.IsType<ProgressBar>(elements[2]);
        page.ShowIndeterminate("Preparing...");
        Arrange(page, 544);
        double initialY = progress.TranslatePoint(new Point(), page).Y;
        page.SetProgress(1, 2, "Installing Assets/Translation/OfflineRuntime/Models/TranslateGemma/Configuration/translation-runtime-config.json...");
        Arrange(page, 544);
        Assert.Equal(initialY, progress.TranslatePoint(new Point(), page).Y, 2);
        Assert.True(progress.TranslatePoint(new Point(), page).Y >= status.TranslatePoint(new Point(), page).Y + status.ActualHeight);
    });

    private static void Arrange(FrameworkElement page, double width)
    {
        page.Measure(new Size(width, 400));
        page.Arrange(new Rect(0, 0, width, 400));
        page.UpdateLayout();
    }
}
