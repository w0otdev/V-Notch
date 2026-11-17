using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Xml.Linq;
using VNotch.Services;
using VNotch.Windows;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public class DialogEnterKeyAndTranslationWindowTests
{
    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null)
        {
            throw new InvalidOperationException("STA test execution failed.", failure);
        }
    }

    [Fact]
    public void ConfirmationDialog_EnterKey_RespectsFocusedCancelAndConfirmButtons()
    {
        RunOnSta(() =>
        {
            bool prevReduceMotion = AnimationConfig.ReduceMotion;
            AnimationConfig.SetReduceMotion(true);
            try
            {
                using var source = new HwndSource(new HwndSourceParameters("ConfirmEnterTest") { Width = 1, Height = 1 });

                var cancelDialog = new ConfirmationDialog();
                FocusManager.SetFocusedElement(cancelDialog, cancelDialog.CancelButton);
                cancelDialog.CancelButton.Focus();
                var cancelEnter = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
                {
                    RoutedEvent = UIElement.KeyDownEvent
                };
                cancelDialog.RaiseEvent(cancelEnter);
                Assert.False(cancelDialog.Confirmed);

                var confirmDialog = new ConfirmationDialog();
                FocusManager.SetFocusedElement(confirmDialog, confirmDialog.ConfirmButton);
                confirmDialog.ConfirmButton.Focus();
                var confirmEnter = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
                {
                    RoutedEvent = UIElement.KeyDownEvent
                };
                confirmDialog.RaiseEvent(confirmEnter);
                Assert.True(confirmDialog.Confirmed);

                var windowFocusedDialog = new ConfirmationDialog();
                FocusManager.SetFocusedElement(windowFocusedDialog, windowFocusedDialog);
                windowFocusedDialog.Focus();
                var windowEnter = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
                {
                    RoutedEvent = UIElement.KeyDownEvent
                };
                windowFocusedDialog.RaiseEvent(windowEnter);
                Assert.True(windowFocusedDialog.Confirmed);
            }
            finally
            {
                AnimationConfig.SetReduceMotion(prevReduceMotion);
            }
        });
    }

    [Fact]
    public void TranslationWindow_EnterKey_DoesNotTriggerSummaryWhenSecondaryButtonFocused()
    {
        RunOnSta(() =>
        {
            bool prevReduceMotion = AnimationConfig.ReduceMotion;
            AnimationConfig.SetReduceMotion(true);
            var window = new TranslationWindow("en", "vi");
            try
            {
                using var source = new HwndSource(new HwndSourceParameters("TranslationEnterTest") { Width = 1, Height = 1 });
                int summaryCount = 0;
                window.SummarizeRequested += () => summaryCount++;
                window.CopyButton.IsEnabled = true;

                FocusManager.SetFocusedElement(window, window.CopyButton);
                window.CopyButton.Focus();
                var copyEnter = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent
                };
                window.RaiseEvent(copyEnter);
                Assert.Equal(0, summaryCount);
                Assert.False(copyEnter.Handled);

                FocusManager.SetFocusedElement(window, window.CloseButton);
                window.CloseButton.Focus();
                var closeEnter = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent
                };
                window.RaiseEvent(closeEnter);
                Assert.Equal(0, summaryCount);
                Assert.False(closeEnter.Handled);

                FocusManager.SetFocusedElement(window, window.SummarizeButton);
                window.SummarizeButton.Focus();
                var summaryEnter = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent
                };
                window.RaiseEvent(summaryEnter);
                Assert.Equal(1, summaryCount);
                Assert.True(summaryEnter.Handled);

                FocusManager.SetFocusedElement(window, window);
                window.Focus();
                var windowEnter = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent
                };
                window.RaiseEvent(windowEnter);
                Assert.Equal(2, summaryCount);
                Assert.True(windowEnter.Handled);
            }
            finally
            {
                window.Close();
                AnimationConfig.SetReduceMotion(prevReduceMotion);
            }
        });
    }

    [Fact]
    public void TranslationWindow_SelectionChipCloseButton_LocalizesAutomationName_And_OnlyScrollFadesUseGradients()
    {
        RunOnSta(() =>
        {
            var window = new TranslationWindow("en", "vi");
            try
            {
                AutomationProperties.SetName(window.SelectionChipCloseButton, "unlocalized-placeholder");
                window.RefreshLocalization();
                Assert.Equal(Loc.Get("common.close"), AutomationProperties.GetName(window.SelectionChipCloseButton));
                Assert.Equal(Loc.Get("translation.close"), AutomationProperties.GetName(window.CloseButton));
            }
            finally
            {
                window.Close();
            }
        });

        string xamlPath = FindRepoFile(Path.Combine("Windows", "TranslationWindow.xaml"));
        var document = XDocument.Load(xamlPath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var gradients = document.Descendants(presentation + "LinearGradientBrush").ToArray();
        // The card and controls stay solid. Only the two scroll-edge opacity
        // masks use gradients, fading the existing surface without adding color.
        Assert.Equal(new[] { "ResultTopFade", "ResultBottomFade" }, gradients.Select(gradient =>
        {
            Assert.Equal(presentation + "Border.OpacityMask", gradient.Parent?.Name);
            Assert.Equal(presentation + "Border", gradient.Parent?.Parent?.Name);
            return (string?)gradient.Parent?.Parent?.Attribute(xaml + "Name");
        }));
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {relativePath} from {AppContext.BaseDirectory}.");
    }
}
