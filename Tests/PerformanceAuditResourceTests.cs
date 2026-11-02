using System.Reflection;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Effects;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class PerformanceAuditResourceTests
{
    [Fact]
    public void IdleBlurDetachesAndReattachesTheSameAnimatedEffect() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var element = new Border();
        var effect = new BlurEffect { Radius = 0 };
        ActiveBlur.SetBlur(element, new BlurDefinition { Effect = effect });
        Assert.Null(element.Effect);
        effect.Radius = 12;
        await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background, ct);
        Assert.Same(effect, element.Effect);
        effect.Radius = 0;
        await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background, ct);
        Assert.Null(element.Effect);
        effect.BeginAnimation(BlurEffect.RadiusProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 8, TimeSpan.FromMilliseconds(120)));
        await WpfFrameWaiter.UntilAsync(() => ReferenceEquals(element.Effect, effect) && effect.Radius > 1, "animated blur attached", ct);
        effect.BeginAnimation(BlurEffect.RadiusProperty, new System.Windows.Media.Animation.DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(120)));
        await WpfFrameWaiter.UntilAsync(() => element.Effect == null, "animated blur detached at rest", ct);
        Assert.Null(element.Effect);
    });

    [Fact]
    public void StreamingMarkdownRetainsCompletedBlocksAndDoesNoWorkWithoutNewWords() => SharedStaTestRunner.Run(() =>
    {
        var renderer = new AiMarkdown.StreamRenderer();
        Assert.True(renderer.Advance("First paragraph.\n\nSecond paragraph.", 100, 0));
        var first = renderer.Document.Blocks.FirstBlock;
        Assert.True(renderer.Advance("First paragraph.\n\nSecond paragraph.\n\n```cs\nvar answer = 42;\n```\n\nEnd.", 100, 0));
        Assert.Same(first, renderer.Document.Blocks.FirstBlock);
        var last = renderer.Document.Blocks.LastBlock;
        Assert.True(renderer.Advance("First paragraph.\n\nSecond paragraph.\n\n```cs\nvar answer = 42;\n```\n\nEnd.", 100, 0));
        Assert.Same(last, renderer.Document.Blocks.LastBlock);
        var text = new TextRange(renderer.Document.ContentStart, renderer.Document.ContentEnd).Text;
        Assert.Contains("var answer = 42;", text);
        Assert.Contains("Second paragraph.", text);
        Assert.DoesNotContain("```", text);
    });

    [Fact]
    public void PackagePidCacheRetainsOtherLiveEntriesWhenFull()
    {
        var probe = typeof(PrivacyIndicatorService).GetNestedType("ConsumerProcessProbe", BindingFlags.NonPublic)!;
        var cache = (System.Collections.Concurrent.ConcurrentDictionary<uint, (string Family, long ExpireTicks)>)
            probe.GetField("_packageFamilyByPidCache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var add = probe.GetMethod("CachePackageFamilyForPid", BindingFlags.NonPublic | BindingFlags.Static)!;
        cache.Clear();
        try
        {
            for (uint i = 1; i <= 2050; i++) add.Invoke(null, [i, "fixture", 10000L + i, 0L]);
            Assert.Equal(2048, cache.Count);
            Assert.True(cache.ContainsKey(500));
            Assert.True(cache.ContainsKey(2050));
        }
        finally { cache.Clear(); }
    }
}
