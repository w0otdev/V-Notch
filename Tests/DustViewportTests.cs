using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class DustViewportTests
{
    [Fact]
    public void ClippedDustStopsItsSharedFrameWorkAndResumesAfterScrolling() => SharedStaTestRunner.RunAsync(async () =>
    {
        bool reducedMotion = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        var dust = new DustParticleOverlay { IsActive = true, Width = 100, Height = 40 };
        var content = new StackPanel();
        content.Children.Add(new Border { Height = 240 });
        content.Children.Add(dust);
        content.Children.Add(new Border { Height = 240 });
        var scroll = new ScrollViewer { Content = content, CanContentScroll = false };
        var window = new BackgroundWindow { Content = scroll, Width = 240, Height = 160 };
        var coordinator = typeof(DustParticleOverlay).GetNestedType("FrameCoordinator", BindingFlags.NonPublic)!;
        var active = (IList)coordinator.GetField("ActiveOverlays", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(dust.IsVisible); // WPF visibility alone does not account for clipping.
            Assert.False(dust.IsInParticleViewport);
            Assert.DoesNotContain(dust, active.Cast<object>());

            scroll.ScrollToVerticalOffset(220);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(dust.IsInParticleViewport);
            Assert.Contains(dust, active.Cast<object>());

            scroll.ScrollToTop();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(dust.IsInParticleViewport);
            Assert.DoesNotContain(dust, active.Cast<object>());
            window.Content = null;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.DoesNotContain(dust, active.Cast<object>());
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(reducedMotion); }
    });
}
