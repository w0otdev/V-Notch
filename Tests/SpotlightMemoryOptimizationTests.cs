using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SpotlightMemoryOptimizationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void HiddenTranscriptRestoresTheSamePageScrollAndPixels() => SharedStaTestRunner.RunAsync(async () =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        try
        {
            using var fixture = new SpotlightWindowFixture(new NotchSettings { NotchStyle = "Solid", HasSeenSpotlightIntro = true });
            var window = fixture.Window;
            window.ShowSpotlight();
            Invoke(window, "ToggleAiMode");
            var history = Field<List<SpotlightAiMessage>>(window, "_aiHistory");
            for (int i = 0; i < 40; i++)
                history.Add(new(i % 2 == 0 ? "user" : "assistant", i % 2 == 0 ? $"Question {i}" :
                    $"Reply {i}\n\n" + string.Concat(Enumerable.Repeat("Text with **bold**, `code`, and a paragraph.\n\n", 8))));
            Invoke(window, "RenderTranscriptPage", history.Take(4).ToArray(), 0);
            Invoke(window, "RefreshAiPanel");
            window.UpdateLayout();
            window.AiTranscriptScroll.ScrollToVerticalOffset(75);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            double offset = window.AiTranscriptScroll.VerticalOffset;
            Assert.True(offset > 0);
            var before = Capture(window.AiTranscriptScroll, "before");

            Invoke(window, "CompleteHide");
            Assert.Equal(4, window.AiTranscript.Children.Count); // Fast reopen keeps the warm view.
            Assert.True(Field<UiScheduledTimer>(window, "_aiTranscriptExpiry").IsEnabled);
            Invoke(window, "ReleaseHiddenAiTranscript"); // Exercise the 30-second expiry deterministically.
            Assert.Empty(window.AiTranscript.Children);
            Assert.Equal(40, history.Count);
            Assert.False(Field<UiScheduledTimer>(window, "_aiTranscriptExpiry").IsEnabled);

            window.ShowSpotlight();
            Invoke(window, "ToggleAiMode");
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            Assert.Equal(4, window.AiTranscript.Children.Count);
            Assert.Equal(0, Field<int>(window, "_transcriptPageOffset"));
            Assert.Equal(offset, window.AiTranscriptScroll.VerticalOffset, 3);
            Assert.Equal(before, Capture(window.AiTranscriptScroll, "after"));
            Assert.Equal(40, history.Count);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void ExpiryNeverDetachesAnActiveStreamOrVisibleTranscript() => SharedStaTestRunner.Run(() =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        try
        {
            using var fixture = new SpotlightWindowFixture(new NotchSettings { NotchStyle = "Solid", HasSeenSpotlightIntro = true });
            var window = fixture.Window;
            window.ShowSpotlight();
            Field<List<SpotlightAiMessage>>(window, "_aiHistory").Add(new("assistant", "**Still here**"));
            Invoke(window, "RenderAiHistory");
            var view = window.AiTranscript.Children[0];
            Invoke(window, "ReleaseHiddenAiTranscript");
            Assert.Same(view, window.AiTranscript.Children[0]);
            Invoke(window, "CompleteHide");
            using var request = new CancellationTokenSource();
            typeof(SpotlightWindow).GetField("_aiRequest", Private)!.SetValue(window, request);
            try
            {
                Invoke(window, "ReleaseHiddenAiTranscript");
                Assert.Same(view, window.AiTranscript.Children[0]);
                Assert.False(request.IsCancellationRequested);
                Assert.True(Field<UiScheduledTimer>(window, "_aiTranscriptExpiry").IsEnabled);
            }
            finally { typeof(SpotlightWindow).GetField("_aiRequest", Private)!.SetValue(window, null); }
            Invoke(window, "ReleaseHiddenAiTranscript");
            Assert.Empty(window.AiTranscript.Children);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    private static byte[] Capture(FrameworkElement element, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),
            (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "ram-visual-check"));
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
        return pixels;
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
}
