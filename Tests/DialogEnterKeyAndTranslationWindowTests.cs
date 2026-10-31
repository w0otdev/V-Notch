using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using VNotch.Services;
using VNotch.Windows;
using Xunit;

namespace VNotch.Tests;

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
    public void TranslationWindow_EnterKey_DoesNotTriggerTranslateWhenSecondaryButtonFocused()
    {
        RunOnSta(() =>
        {
            bool prevReduceMotion = AnimationConfig.ReduceMotion;
            AnimationConfig.SetReduceMotion(true);
            var window = new TranslationWindow("en", "vi");
            try
            {
                using var source = new HwndSource(new HwndSourceParameters("TranslationEnterTest") { Width = 1, Height = 1 });
                int translateCount = 0;
                window.TranslateRequested += () => translateCount++;
                window.CopyButton.IsEnabled = true;

                FocusManager.SetFocusedElement(window, window.CopyButton);
                window.CopyButton.Focus();
                var copyEnter = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent
                };
                window.RaiseEvent(copyEnter);
                Assert.Equal(0, translateCount);
                Assert.False(copyEnter.Handled);

                FocusManager.SetFocusedElement(window, window.CloseButton);
                window.CloseButton.Focus();
                var closeEnter = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent
                };
                window.RaiseEvent(closeEnter);
                Assert.Equal(0, translateCount);
                Assert.False(closeEnter.Handled);

                FocusManager.SetFocusedElement(window, window.TranslateButton);
                window.TranslateButton.Focus();
                var translateEnter = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent
                };
                window.RaiseEvent(translateEnter);
                Assert.Equal(1, translateCount);
                Assert.True(translateEnter.Handled);

                FocusManager.SetFocusedElement(window, window);
                window.Focus();
                var windowEnter = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent
                };
                window.RaiseEvent(windowEnter);
                Assert.Equal(2, translateCount);
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
    public void TranslationWindow_SelectionChipCloseButton_LocalizesAutomationName_And_HasNoLinearGradientBrush()
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
        string xaml = File.ReadAllText(xamlPath);
        Assert.DoesNotContain("LinearGradientBrush", xaml, StringComparison.OrdinalIgnoreCase);
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
