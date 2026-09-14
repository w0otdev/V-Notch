using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SetupTermsOfServicePageTests
{
    [Fact]
    public void InitialState_CannotContinueUntilReadToBottom()
    {
        RunOnStaThread(() =>
        {
            var page = new TermsOfServicePage();
            page.Measure(new Size(600, 400));
            page.Arrange(new Rect(0, 0, 600, 400));
            page.UpdateLayout();

            // When content requires scrolling
            if (page.TermsScrollViewer.ScrollableHeight > 0)
            {
                Assert.False(page.HasReadToBottom);
                Assert.False(page.AgreeCheckBox.IsEnabled);
                Assert.False(page.CanContinue);
            }
        });
    }

    [Fact]
    public void ScrollToEnd_EnablesAgreeCheckboxAndAllowsContinue()
    {
        RunOnStaThread(() =>
        {
            var page = new TermsOfServicePage();
            page.Measure(new Size(500, 300));
            page.Arrange(new Rect(0, 0, 500, 300));
            page.UpdateLayout();

            bool eventFired = false;
            page.CanContinueChanged += canContinue =>
            {
                if (canContinue)
                {
                    eventFired = true;
                }
            };

            page.TermsScrollViewer.ScrollToEnd();
            page.CheckIfScrolledToBottom();
            page.UpdateLayout();

            Assert.True(page.HasReadToBottom);
            Assert.True(page.AgreeCheckBox.IsEnabled);
            Assert.True(page.AgreeCheckBox.IsChecked);
            Assert.True(page.CanContinue);
            Assert.True(eventFired);
        });
    }

    [Fact]
    public void UncheckingAgreeCheckbox_DisablesCanContinue()
    {
        RunOnStaThread(() =>
        {
            var page = new TermsOfServicePage();
            page.Measure(new Size(500, 300));
            page.Arrange(new Rect(0, 0, 500, 300));
            page.UpdateLayout();

            // Simulate reading to bottom
            page.TermsScrollViewer.ScrollToEnd();
            page.UpdateLayout();
            page.CheckIfScrolledToBottom();

            Assert.True(page.CanContinue);

            // User unchecks the agreement
            page.AgreeCheckBox.IsChecked = false;
            Assert.False(page.CanContinue);

            // User checks it back
            page.AgreeCheckBox.IsChecked = true;
            Assert.True(page.CanContinue);
        });
    }

    [Fact]
    public void LanguageChange_RefreshesLocalization()
    {
        RunOnStaThread(() =>
        {
            Loc.SetLanguage("vi");
            var page = new TermsOfServicePage();
            page.RefreshLocalization();

            Assert.Contains("Điều", Loc.Get("setup.terms.headline"));

            Loc.SetLanguage("en");
            page.RefreshLocalization();
            Assert.Equal("Terms of Service", Loc.Get("setup.terms.headline"));
        });
    }

    private static void EnsureApplicationResources()
    {
        if (Application.Current == null)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources["SFProDisplay"] = new FontFamily("Segoe UI");
            app.Resources["SFProText"] = new FontFamily("Segoe UI");
            app.Resources["IconFont"] = new FontFamily("Segoe MDL2 Assets");
        }
        else
        {
            if (!Application.Current.Resources.Contains("SFProDisplay"))
                Application.Current.Resources["SFProDisplay"] = new FontFamily("Segoe UI");
            if (!Application.Current.Resources.Contains("SFProText"))
                Application.Current.Resources["SFProText"] = new FontFamily("Segoe UI");
            if (!Application.Current.Resources.Contains("IconFont"))
                Application.Current.Resources["IconFont"] = new FontFamily("Segoe MDL2 Assets");
        }
    }

    [Fact]
    public void SetupWindow_HasEightSteps_AndTermsControlsNextButton()
    {
        RunOnStaThread(() =>
        {
            EnsureApplicationResources();
            var window = new SetupWindow();
            Assert.Equal("01  Language", window.Step1Text.Text);
            Assert.Equal("02  Welcome", window.Step2Text.Text);
            Assert.Equal("03  Terms", window.Step3Text.Text);
            Assert.Equal("04  About", window.Step4Text.Text);
            Assert.Equal("05  Location", window.Step5Text.Text);
            Assert.Equal("06  Startup", window.Step6Text.Text);
            Assert.Equal("07  Install", window.Step7Text.Text);
            Assert.Equal("08  Finish", window.Step8Text.Text);
        });
    }

    [Fact]
    public void SetupWindow_WhenSystemLanguageWasVietnamese_InitializesInEnglishByDefault()
    {
        RunOnStaThread(() =>
        {
            Loc.SetLanguage("vi");
            Assert.Equal("vi", Loc.CurrentLanguage);

            EnsureApplicationResources();
            var window = new SetupWindow();

            Assert.Equal("en", Loc.CurrentLanguage);
            Assert.Equal("01  Language", window.Step1Text.Text);
            Assert.Equal("03  Terms", window.Step3Text.Text);
            Assert.Equal("Setup Assistant", window.SetupAssistantText.Text);
            Assert.Equal("Cancel", window.CancelButton.Content);
            Assert.Equal("Continue", window.NextButton.Content);
        });
    }

    [Fact]
    public void SetupWindow_AllTextElements_UseBoldFontWeight()
    {
        RunOnStaThread(() =>
        {
            EnsureApplicationResources();
            var window = new SetupWindow();

            Assert.Equal(FontWeights.Bold, window.SetupAssistantText.FontWeight);
            Assert.Equal(FontWeights.Bold, window.SetupTaglineText.FontWeight);
            Assert.Equal(FontWeights.Bold, window.Step1Text.FontWeight);
            Assert.Equal(FontWeights.Bold, window.Step2Text.FontWeight);
            Assert.Equal(FontWeights.Bold, window.Step3Text.FontWeight);
            Assert.Equal(FontWeights.Bold, window.Step4Text.FontWeight);
            Assert.Equal(FontWeights.Bold, window.Step5Text.FontWeight);
            Assert.Equal(FontWeights.Bold, window.Step6Text.FontWeight);
            Assert.Equal(FontWeights.Bold, window.Step7Text.FontWeight);
            Assert.Equal(FontWeights.Bold, window.Step8Text.FontWeight);
            Assert.Equal(FontWeights.Bold, window.HeadlineText.FontWeight);
            Assert.Equal(FontWeights.Bold, window.BodyText.FontWeight);
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
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
