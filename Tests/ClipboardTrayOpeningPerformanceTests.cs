using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClipboardTrayOpeningPerformanceTests(ITestOutputHelper output)
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void PreparedMaskingProtectsNewAndRecycledCardsAndReusesUnchangedResults() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var tray = new ClipboardTray();
        var ordinary = new ClipboardEntry { Title = "Project document", Preview = "Project document" };
        var secret = new ClipboardEntry { Title = "password: synthetic-value", Preview = "password: synthetic-value" };
        var prepare = typeof(ClipboardTray).GetMethod("PrepareCardMaskingAsync", Private)!;
        var reconcile = typeof(ClipboardTray).GetMethod("ReconcilePreparedCards", Private)!;
        var cards = (IReadOnlyList<VNotch.ViewModels.ClipboardCardViewModel>)typeof(ClipboardTray).GetField("_cards", Private)!.GetValue(tray)!;
        async Task Publish(ClipboardEntry[] entries)
        {
            var masking = await (Task<Dictionary<Guid, bool>>)prepare.Invoke(tray, [entries, ct])!;
            reconcile.Invoke(tray, [entries, masking]);
        }
        await Publish([ordinary, secret]);
        var ordinaryCard = cards[0];
        var secretCard = cards[1];
        Assert.Equal(false, ordinaryCard.GetCachedMasking(ordinary));
        Assert.Equal(true, secretCard.GetCachedMasking(secret));
        Assert.Equal(ordinary.Preview, ordinaryCard.DisplayPreview);
        Assert.Equal(VNotch.Services.Clipboard.ClipboardSensitiveData.Mask, secretCard.Preview);
        Assert.Equal(VNotch.Services.Clipboard.ClipboardSensitiveData.CreateLayoutMask(secret.Preview), secretCard.DisplayPreview);
        Assert.Equal(VNotch.Services.Clipboard.ClipboardSensitiveData.Mask, secretCard.Title);
        // Pinning has no effect on classification and needs no new worker scan.
        var pinned = ordinary with { IsPinned = true };
        var cached = (Task<Dictionary<Guid, bool>>)prepare.Invoke(tray, [new[] { pinned, secret }, ct])!;
        Assert.True(cached.IsCompletedSuccessfully);
        Assert.False((await cached)[ordinary.Id]);
        await Publish([pinned with { SourceApp = "Bitwarden" }, secret with { Title = ordinary.Title, Preview = ordinary.Preview }]);
        Assert.Same(ordinaryCard, cards[0]);
        Assert.Same(secretCard, cards[1]);
        Assert.True(ordinaryCard.IsMasked);
        Assert.Equal(VNotch.Services.Clipboard.ClipboardSensitiveData.Mask, ordinaryCard.Preview);
        Assert.Equal(VNotch.Services.Clipboard.ClipboardSensitiveData.CreateLayoutMask(ordinary.Preview), ordinaryCard.DisplayPreview);
        Assert.False(secretCard.IsMasked);
        Assert.Equal(ordinary.Preview, secretCard.DisplayPreview);
        // Non-query callers also invalidate the cached flag when content changes.
        secretCard.UpdateEntry(secret);
        Assert.True(secretCard.IsMasked);
    });

    [Fact]
    public void OpeningKeepsTheCardViewportStableWhileTheShellResizes() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        var samples = new List<object>();
        int maxViewportResizes = 0;
        try
        {
            await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
                settings => { settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true; },
                services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
            var window = fixture.Window;
            var tray = window.ClipboardTrayView;
            var coordinator = (NotchTransitionCoordinator)typeof(MainWindow).GetField("_transitionCoordinator", Private)!.GetValue(window)!;
            window.Show();
            await WpfFrameWaiter.NextAsync(ct);
            // Warm the template and animation path before comparing repeated openings.
            coordinator.RequestView(NotchView.Secondary, "TrayBenchmarkWarmup");
            await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "warm tray entrance", ct);
            coordinator.RequestCollapse("TrayBenchmarkWarmup");
            await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "warm tray exit", ct);

            foreach (int count in new[] { 8, 120, 5000 })
            {
                var entries = Enumerable.Range(0, count).Select(i => new ClipboardEntry
                { Title = $"Project document {i}.pdf", Preview = "Project document", Kind = ClipboardKind.File }).ToArray();
                var preparation = (Task<Dictionary<Guid, bool>>)typeof(ClipboardTray)
                    .GetMethod("PrepareCardMaskingAsync", Private)!.Invoke(tray, [entries, ct])!;
                var masking = await preparation;
                typeof(ClipboardTray).GetMethod("ReconcilePreparedCards", Private)!.Invoke(tray, [entries, masking]);
                typeof(ClipboardTray).GetField("_queryDirty", Private)!.SetValue(tray, false);
                ((UiScheduledTimer)typeof(ClipboardTray).GetField("_searchTimer", Private)!.GetValue(tray)!).Stop();
                AnimationConfig.SetReduceMotion(count > 8);
                int runs = count == 8 ? 2 : 1;
                for (int run = 0; run < runs; run++)
                {
                    int viewportResizes = 0;
                    var cards = (FrameworkElement)tray.FindName("Cards");
                    SizeChangedEventHandler resized = (_, _) => viewportResizes++;
                    var frames = new List<double>();
                    long lastFrame = Stopwatch.GetTimestamp();
                    EventHandler rendered = (_, _) =>
                    {
                        long now = Stopwatch.GetTimestamp();
                        frames.Add(Stopwatch.GetElapsedTime(lastFrame, now).TotalMilliseconds);
                        lastFrame = now;
                    };
                    cards.SizeChanged += resized;
                    CompositionTarget.Rendering += rendered;
                    long allocated = GC.GetAllocatedBytesForCurrentThread();
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        Assert.True(coordinator.RequestView(NotchView.Secondary, "TrayOpeningBenchmark"));
                        double requestMs = watch.Elapsed.TotalMilliseconds;
                        await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "measured tray entrance", ct);
                        double openingMs = watch.Elapsed.TotalMilliseconds;
                        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                        var gaps = frames.Order().ToArray();
                        double p95 = gaps.Length == 0 ? 0 : gaps[(int)((gaps.Length - 1) * .95)];
                        var sample = new
                        {
                            Count = count,
                            Run = run,
                            RequestMs = requestMs,
                            OpeningMs = openingMs,
                            FrameP95Ms = p95,
                            FrameMaxMs = gaps.Length == 0 ? 0 : gaps[^1],
                            ViewportResizes = viewportResizes,
                            AllocatedBytes = bytes
                        };
                        samples.Add(sample);
                        maxViewportResizes = Math.Max(maxViewportResizes, viewportResizes);
                        output.WriteLine(JsonSerializer.Serialize(sample));
                    }
                    finally
                    {
                        cards.SizeChanged -= resized;
                        CompositionTarget.Rendering -= rendered;
                    }
                    coordinator.RequestCollapse("TrayOpeningBenchmark");
                    await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "measured tray exit", ct);
                }
            }
            Assert.InRange(maxViewportResizes, 0, 2);
        }
        finally
        {
            AnimationConfig.SetReduceMotion(previous);
            string? report = Environment.GetEnvironmentVariable("VNOTCH_TRAY_BENCHMARK_REPORT");
            if (!string.IsNullOrEmpty(report))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
                File.WriteAllText(report, JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }, timeoutSeconds: 90);
}
