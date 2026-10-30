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
    [InlineData("de")]
    public void MinimumSizeKeepsActionsAndTheCurrentStepVisible(string language) => SharedStaTestRunner.RunAsync(async ct =>
    {
        LiquidGlassSpotlightTests.CreateApplicationResources();
        var window = new SetupWindow("visual fixture", language, false, (_, _) => Task.CompletedTask)
        { Width = 520, Height = 420 };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.Show();
            await ShowPage(window, 2, false, ct);
            window.UpdateLayout();
            Assert.False(window.NextButton.IsEnabled);
            var surface = Assert.IsType<Border>(window.NextButton.Template.FindName("border", window.NextButton));
            Assert.True(surface.Opacity < .6, "A locked Continue button must look disabled.");
            foreach (var button in new[] { window.CancelButton, window.BackButton, window.NextButton })
            {
                var position = button.TranslatePoint(new Point(), window);
                Assert.InRange(position.X, 0, window.ActualWidth - button.ActualWidth + 1);
                Assert.InRange(position.Y, 0, window.ActualHeight - button.ActualHeight + 1);
            }
            await ShowPage(window, 7, false, ct);
            await WpfFrameWaiter.UntilAsync(() =>
            {
                var position = window.Step8Text.TranslatePoint(new Point(), window.SidebarScrollViewer);
                return position.Y >= -1 && position.Y + window.Step8Text.ActualHeight <= window.SidebarScrollViewer.ActualHeight + 1;
            }, "current setup step visible in the short sidebar", ct);
            Assert.True(window.SidebarScrollViewer.ScrollableHeight > 0);
        }
        finally { window.Close(); Loc.SetLanguage("en"); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpdateSkipsFirstRunPagesOnlyForAnExistingInstallation(bool installed) => SharedStaTestRunner.RunAsync(async ct =>
    {
        LiquidGlassSpotlightTests.CreateApplicationResources();
        string directory = System.IO.Directory.CreateTempSubdirectory("vnotch-update-navigation-").FullName;
        if (installed) System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "V-Notch.exe"), "fixture executable");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupInstallOptions? captured = null;
        var window = new SetupWindow("fixture source", "vi", true, (options, _) =>
        {
            captured = options;
            return completion.Task;
        });
        try
        {
            Field<DirectoryPage>(window, "_directoryPage").InstallPath = directory;
            bool startup = Field<StartupOptionsPage>(window, "_startupOptionsPage").StartWithWindows;
            Invoke(window, "InitializeFirstPage");
            Assert.Equal("vi", Loc.CurrentLanguage);
            if (installed)
            {
                Assert.IsType<InstallProgressPage>(window.ContentPresenter.Content);
                Assert.Equal(6, Field<int>(window, "_currentPageIndex"));
                Assert.NotNull(captured);
                Assert.Equal(directory, captured.InstallDirectory);
                Assert.Equal("vi", captured.Language);
                Assert.Equal(startup, captured.StartWithWindows);
                Assert.Equal(Visibility.Collapsed, window.BackButton.Visibility);
                completion.SetResult();
                await WpfFrameWaiter.UntilAsync(() => Field<bool>(window, "_installationSucceeded"), "update installation", ct);
                await Settled(window, ct);
                Assert.IsType<FinishPage>(window.ContentPresenter.Content);
            }
            else
            {
                Assert.IsType<LanguagePage>(window.ContentPresenter.Content);
                Assert.Equal(0, Field<int>(window, "_currentPageIndex"));
                Assert.Null(captured);
            }
        }
        finally
        {
            completion.TrySetResult();
            window.Close();
            Loc.SetLanguage("en");
            System.IO.Directory.Delete(directory, true);
        }
    });

    [Theory]
    [InlineData("en")]
    public void WizardNavigatesPreparationStepsAndCanResumeAfterCancellation(string language) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previousReducedMotion = AnimationConfig.ReduceMotion;
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
                AnimationConfig.SetReduceMotion(index > 1);
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
        finally { AnimationConfig.SetReduceMotion(previousReducedMotion); window.Close(); Loc.SetLanguage("en"); }
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
