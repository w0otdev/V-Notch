using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SetupWizardNavigationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    [Theory]
    [InlineData("en")]
    [InlineData("vi")]
    [InlineData("ja")]
    public void WizardNavigatesPreparationStepsAndCanResumeAfterCancellation(string language) => SharedStaTestRunner.RunAsync(async ct =>
    {
        LiquidGlassSpotlightTests.CreateApplicationResources();
        var window = new SetupWindow(initialLanguage: language);
        try
        {
            await ShowPage(window, 0, false, ct);
            Assert.IsType<LanguagePage>(window.ContentPresenter.Content);
            Assert.Equal(ScrollBarVisibility.Disabled, window.PageScrollViewer.VerticalScrollBarVisibility);
            Assert.Equal(Visibility.Collapsed, window.BackButton.Visibility);
            for (int index = 1; index <= 5; index++)
            {
                Invoke(window, "NextButton_Click", window.NextButton, new RoutedEventArgs());
                Invoke(window, "NextButton_Click", window.NextButton, new RoutedEventArgs());
                await Settled(window, ct);
                Assert.Equal(index, Field<int>(window, "_currentPageIndex"));
                Assert.True(window.CancelButton.IsEnabled);
                Assert.Equal(Visibility.Visible, window.BackButton.Visibility);
                if (index == 1)
                {
                    Assert.Null(window.ContentPresenter.Content);
                    Assert.Equal(Visibility.Visible, window.HeadlineText.Visibility);
                }
                else if (index == 2)
                {
                    var terms = Assert.IsType<TermsOfServicePage>(window.ContentPresenter.Content);
                    Assert.False(window.NextButton.IsEnabled);
                    Invoke(window, "NextButton_Click", window.NextButton, new RoutedEventArgs());
                    Assert.Equal(2, Field<int>(window, "_currentPageIndex"));
                    terms.Measure(new Size(500, 300));
                    terms.Arrange(new Rect(0, 0, 500, 300));
                    terms.UpdateLayout();
                    terms.TermsScrollViewer.ScrollToEnd();
                    terms.UpdateLayout();
                    terms.CheckIfScrolledToBottom();
                    terms.AgreeCheckBox.IsChecked = true;
                    Assert.True(window.NextButton.IsEnabled);
                }
                else if (index == 4)
                {
                    var directory = Assert.IsType<DirectoryPage>(window.ContentPresenter.Content);
                    directory.InstallPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vnotch-navigation-test");
                    Assert.True((bool)Invoke(window, "CommitCurrentStep")!);
                }
            }
            for (int index = 4; index >= 0; index--)
            {
                Invoke(window, "BackButton_Click", window.BackButton, new RoutedEventArgs());
                await Settled(window, ct);
                Assert.Equal(index, Field<int>(window, "_currentPageIndex"));
                if (index is 1 or 3)
                {
                    Invoke(window, "CancelButton_Click", window.CancelButton, new RoutedEventArgs());
                    Invoke(window, "ShowCancelSetupPage");
                    await Settled(window, ct);
                    Assert.True(Field<bool>(window, "_isShowingCancelSetupPage"));
                    Assert.IsType<CancelSetupPage>(window.ContentPresenter.Content);
                    Assert.Equal(Loc.Get("setup.btn.cancelSetup"), window.NextButton.Content);
                    Assert.Equal(Visibility.Collapsed, window.CancelButton.Visibility);
                    Invoke(window, "BackButton_Click", window.BackButton, new RoutedEventArgs());
                    await Settled(window, ct);
                    Assert.False(Field<bool>(window, "_isShowingCancelSetupPage"));
                    Assert.Equal(index, Field<int>(window, "_currentPageIndex"));
                }
            }
            Invoke(window, "ShowPage", -1, Direction(false));
            Invoke(window, "ShowPage", 8, Direction(false));
            Assert.Equal(0, Field<int>(window, "_currentPageIndex"));
            Invoke(window, "UpdateNavigationButtons", 6);
            Assert.Equal(Visibility.Collapsed, window.NextButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.CancelButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.BackButton.Visibility);
            await ShowPage(window, 7, false, ct);
            Assert.IsType<FinishPage>(window.ContentPresenter.Content);
            Assert.Equal(Loc.Get("setup.btn.finish"), window.NextButton.Content);
            Invoke(window, "NextButton_Click", window.NextButton, new RoutedEventArgs());
            Assert.Equal(0, window.ResultExitCode);
        }
        finally { window.Close(); Loc.SetLanguage("en"); }
    }, timeoutSeconds: 90);

    [Fact]
    public void ChangingLanguageRefreshesAllStepsAndCancelLabels() => SharedStaTestRunner.RunAsync(async ct =>
    {
        LiquidGlassSpotlightTests.CreateApplicationResources();
        var window = new SetupWindow();
        try
        {
            await ShowPage(window, 0, false, ct);
            foreach (string language in new[] { "vi", "ja", "en" })
            {
                Invoke(window, "OnSetupLanguageChanged", language);
                Assert.Equal(language, Loc.CurrentLanguage);
                Assert.Equal(Loc.Get("setup.step.language"), window.Step1Text.Text);
                Assert.Equal(Loc.Get("setup.welcome.headline"), window.HeadlineText.Text);
                Assert.Equal(Loc.Get("setup.btn.continue"), window.NextButton.Content);
            }
            Invoke(window, "CloseButton_Click", window.CloseSetupButton, new RoutedEventArgs());
            await Settled(window, ct);
            Assert.Equal(Loc.Get("setup.btn.keepSetup"), window.BackButton.Content);
            Invoke(window, "NextButton_Click", window.NextButton, new RoutedEventArgs());
            Assert.Equal(1, window.ResultExitCode);
        }
        finally { window.Close(); Loc.SetLanguage("en"); }
    });

    private static async Task ShowPage(SetupWindow window, int index, bool backward, CancellationToken ct)
    {
        Invoke(window, "ShowPage", index, Direction(backward));
        await Settled(window, ct);
    }
    private static Task Settled(SetupWindow window, CancellationToken ct) => WpfFrameWaiter.UntilAsync(
        () => !Field<bool>(window, "_isTransitioning"), "setup navigation transition", ct);
    private static object Direction(bool backward) => Enum.ToObject(typeof(SetupWindow).GetNestedType("NavigationDirection", Private)!, backward ? 1 : 0);
    private static T Field<T>(SetupWindow window, string name) => (T)typeof(SetupWindow).GetField(name, Private)!.GetValue(window)!;
    private static object? Invoke(SetupWindow window, string name, params object?[] args) => typeof(SetupWindow).GetMethod(name, Private)!.Invoke(window, args);
}
