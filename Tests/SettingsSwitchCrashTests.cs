using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SettingsSwitchCrashTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpeningSettingsAndTogglingSwitchesUsesMutableInstanceTransforms(bool reduceMotion) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previousMotion = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reduceMotion);
        Loc.SetLanguage("en");
        var settings = new NotchSettings { Language = "en", AutoCheckUpdates = false, EnableWeather = false };
        var window = new SettingsWindow(settings, new FakeSettingsService(settings));
        try
        {
            window.Show();
            await WpfFrameWaiter.UntilAsync(() => window.DynamicIslandModeCheck.IsLoaded, "settings switches loaded", ct);

            var switches = Descendants(window).OfType<CheckBox>()
                .Where(check => check.IsLoaded && check.Template.FindName("SwitchThumb", check) is Border)
                .ToArray();
            Assert.True(switches.Length >= 2);
            var thumbs = switches.Select(Thumb).ToArray();
            var caps = switches.Select(ThumbCapScale).ToArray();
            Assert.All(thumbs, thumb => Assert.False(thumb.IsFrozen));
            Assert.All(caps, cap => Assert.False(cap.IsFrozen));
            Assert.Equal(thumbs.Length, thumbs.Distinct().Count());
            Assert.Equal(caps.Length, caps.Distinct().Count());

            var check = window.DynamicIslandModeCheck;
            var thumb = Thumb(check);
            var cap = ThumbCapScale(check);
            var other = switches.First(value => value != check);
            double otherPosition = Thumb(other).X;
            foreach (bool on in new[] { true, false, true, false }) check.IsChecked = on;
            await WpfFrameWaiter.UntilAsync(() => Math.Abs(thumb.X) < .001 && Math.Abs(cap.ScaleX - 1.0) < .001, "switch settles after rapid toggles", ct);

            Assert.False(thumb.IsFrozen);
            Assert.False(cap.IsFrozen);
            Assert.Equal(otherPosition, Thumb(other).X);
        }
        finally
        {
            window.Close();
            AnimationConfig.SetReduceMotion(previousMotion);
        }
    });

    private static TranslateTransform Thumb(CheckBox check) => Assert.IsType<TranslateTransform>(
        Assert.IsType<Border>(check.Template.FindName("SwitchThumb", check)).RenderTransform);

    private static ScaleTransform ThumbCapScale(CheckBox check) => Assert.IsType<ScaleTransform>(
        Assert.IsType<Border>(check.Template.FindName("SwitchThumbCap", check)).RenderTransform);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
