using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Services;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClipboardTrayAccentTransitionTests
{
    public static IEnumerable<object[]> AccentMotionCases => AccentTheme.Swatches
        .SelectMany(swatch => new[] { new object[] { swatch.Id, false }, new object[] { swatch.Id, true } });

    [Theory]
    [MemberData(nameof(AccentMotionCases))]
    public void SwitchingTabsKeepsTheSelectionPulseSubtle(string accent, bool reducedMotion)
        => RunWithTray(accent, reducedMotion, async (tray, ct) =>
        {
            var pulse = (Border)tray.FindName("CategorySelectionPulse");
            foreach (string category in new[] { "Text", "Links", "All" })
            {
                CategoryButton(tray, category).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(category, tray.CurrentCategory);
                await ObserveTransitionAsync(pulse, ct);
                Assert.Equal(AccentTheme.GetSwatch(accent).Color,
                    Assert.IsType<SolidColorBrush>(tray.CategorySelection.Background).Color);
            }
        });

    [Theory]
    [MemberData(nameof(AccentMotionCases))]
    public void SelectingAndLeavingAHoveredTabKeepsItsHighlightSubtle(string accent, bool reducedMotion)
        => RunWithTray(accent, reducedMotion, async (tray, ct) =>
        {
            var button = CategoryButton(tray, "Text");
            var hover = (Border)button.Template.FindName("CategoryHover", button);
            var mouseOverKey = (DependencyPropertyKey)typeof(UIElement)
                .GetField("IsMouseOverPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            button.SetValue(mouseOverKey, true);
            await Task.Delay(200, ct);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await ObserveTransitionAsync(hover, ct);

            button.SetValue(mouseOverKey, false);
            CategoryButton(tray, "Links").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await ObserveTransitionAsync(hover, ct);
        });

    private static async Task ObserveTransitionAsync(Border highlight, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            var color = Assert.IsType<SolidColorBrush>(highlight.Background).Color;
            Assert.InRange(color.A / 255d * highlight.Opacity, 0, 0.15);
            await WpfFrameWaiter.NextAsync(ct);
        } while (started.ElapsedMilliseconds < 380);
    }

    private static Button CategoryButton(ClipboardTray tray, string name)
    {
        var category = tray.CategoryPanel.Items.Cast<ClipboardCategoryViewModel>().Single(item => item.Name == name);
        var presenter = (ContentPresenter)tray.CategoryPanel.ItemContainerGenerator.ContainerFromItem(category);
        return (Button)VisualTreeHelper.GetChild(presenter, 0);
    }

    private static void RunWithTray(string accent, bool reducedMotion, Func<ClipboardTray, CancellationToken, Task> test)
        => SharedStaTestRunner.RunAsync(async ct =>
        {
            bool previousMotion = AnimationConfig.ReduceMotion;
            AccentTheme.Apply(accent);
            AnimationConfig.SetReduceMotion(reducedMotion);
            using var tray = new ClipboardTray();
            var window = new BackgroundWindow { Content = tray, Width = 1000, Height = 350 };
            try
            {
                window.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(1, tray.CategorySelection.Opacity);
                await test(tray, ct);
            }
            finally
            {
                window.Close();
                AnimationConfig.SetReduceMotion(previousMotion);
                AccentTheme.Apply("default");
            }
        });
}
