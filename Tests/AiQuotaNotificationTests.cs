using System.Collections.Concurrent;
using VNotch.Models;
using VNotch.Services.AiUsage;
using Xunit;

namespace VNotch.Tests;

public sealed class AiQuotaNotificationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000000);

    private static AiAccountUsage Usage(double used, DateTimeOffset? now = null, bool hasReset = true) =>
        new("Codex", AiUsageState.Ready, now ?? Now,
            [new("aiUsage.primary", used, 300, hasReset ? (now ?? Now).AddHours(1) : null, "codex")],
            AccountLabel: "first@example.test");

    private static List<AiQuotaAlert> Evaluate(AiQuotaNotifications tracker, AiAccountUsage usage, DateTimeOffset? now = null)
    {
        List<AiQuotaAlert> alerts = [];
        tracker.Evaluate(usage, now ?? Now, alerts);
        return alerts;
    }

    [Theory]
    [InlineData(59)]
    [InlineData(90)]
    public void StartupBaselineDoesNotRepeatExistingLowQuotaAlerts(double used)
    {
        // Restarting creates a fresh tracker, but must not replay existing low quota.
        for (int restart = 0; restart < 2; restart++)
        {
            using var tracker = new AiQuotaNotifications();
            Assert.Empty(Evaluate(tracker, Usage(used)));
            Assert.Empty(Evaluate(tracker, Usage(used)));
            if (used < 80)
                Assert.Equal(20, Assert.Single(Evaluate(tracker, Usage(81))).ThresholdPercent);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 0)]
    [InlineData(50.01, 50)]
    [InlineData(80, 50)]
    [InlineData(80.01, 20)]
    [InlineData(100, 20)]
    public void FreshThresholdCrossingsUseStrictRemainingPercentages(double used, int threshold)
    {
        using var tracker = new AiQuotaNotifications();
        Assert.Empty(Evaluate(tracker, Usage(0)));
        var alerts = Evaluate(tracker, Usage(used));
        if (threshold == 0) Assert.Empty(alerts);
        else Assert.Equal(threshold, Assert.Single(alerts).ThresholdPercent);
    }

    [Fact]
    public void PollingAndFluctuationsNotifyEachLevelOnlyOncePerKnownCycle()
    {
        using var tracker = new AiQuotaNotifications();
        Assert.Empty(Evaluate(tracker, Usage(50)));
        Assert.Equal(50, Assert.Single(Evaluate(tracker, Usage(51))).ThresholdPercent);
        Assert.Empty(Evaluate(tracker, Usage(51)));
        Assert.Empty(Evaluate(tracker, Usage(49)));
        Assert.Empty(Evaluate(tracker, Usage(60)));
        Assert.Empty(Evaluate(tracker, Usage(80)));
        Assert.Equal(20, Assert.Single(Evaluate(tracker, Usage(81))).ThresholdPercent);
        Assert.Empty(Evaluate(tracker, Usage(79)));
        Assert.Empty(Evaluate(tracker, Usage(100)));
    }

    [Fact]
    public void JumpBelowTwentyDoesNotAlsoSendFifty()
    {
        using var tracker = new AiQuotaNotifications();
        Assert.Empty(Evaluate(tracker, Usage(10)));
        Assert.Equal(20, Assert.Single(Evaluate(tracker, Usage(95))).ThresholdPercent);
        Assert.Empty(Evaluate(tracker, Usage(60)));
        Assert.Empty(Evaluate(tracker, Usage(95)));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    [InlineData(101)]
    public void InvalidPercentagesDoNotConsumeAlerts(double used)
    {
        using var tracker = new AiQuotaNotifications();
        Assert.Empty(Evaluate(tracker, Usage(0)));
        Assert.Empty(Evaluate(tracker, Usage(used)));
        Assert.Equal(20, Assert.Single(Evaluate(tracker, Usage(95))).ThresholdPercent);
    }

    [Fact]
    public void StaleMissingFutureExpiredAndNonReadySamplesDoNotConsumeAlerts()
    {
        using var tracker = new AiQuotaNotifications();
        Assert.Empty(Evaluate(tracker, Usage(0)));
        var usage = Usage(90);
        Assert.Empty(Evaluate(tracker, usage with { UpdatedAt = Now.AddMinutes(-11) }));
        Assert.Empty(Evaluate(tracker, usage with { UpdatedAt = null }));
        Assert.Empty(Evaluate(tracker, usage with { UpdatedAt = Now.AddMinutes(2) }));
        Assert.Empty(Evaluate(tracker, usage with { Windows = [usage.Windows![0] with { ResetsAt = Now }] }));
        foreach (var state in Enum.GetValues<AiUsageState>().Where(state => state != AiUsageState.Ready))
            Assert.Empty(Evaluate(tracker, usage with { State = state }));
        Assert.Equal(20, Assert.Single(Evaluate(tracker, usage)).ThresholdPercent);
    }

    [Fact]
    public void NewCycleRearmsOnlyAfterFreshProviderDataArrives()
    {
        using var tracker = new AiQuotaNotifications();
        Assert.Empty(Evaluate(tracker, Usage(0)));
        var usage = Usage(90);
        Assert.Equal(20, Assert.Single(Evaluate(tracker, usage)).ThresholdPercent);
        var later = Now.AddHours(2);
        Assert.Empty(Evaluate(tracker, usage with { UpdatedAt = later }, later));
        Assert.Equal(50, Assert.Single(Evaluate(tracker, Usage(60, later), later)).ThresholdPercent);
        Assert.Equal(20, Assert.Single(Evaluate(tracker, Usage(90, later), later)).ThresholdPercent);
    }

    [Fact]
    public void ResetlessProvidersRearmAfterFreshRecovery()
    {
        using var tracker = new AiQuotaNotifications();
        Assert.Empty(Evaluate(tracker, Usage(0, hasReset: false)));
        Assert.Equal(20, Assert.Single(Evaluate(tracker, Usage(90, hasReset: false))).ThresholdPercent);
        Assert.Empty(Evaluate(tracker, Usage(90, hasReset: false)));
        Assert.Empty(Evaluate(tracker, Usage(40, hasReset: false)));
        Assert.Equal(50, Assert.Single(Evaluate(tracker, Usage(60, hasReset: false))).ThresholdPercent);
    }

    [Fact]
    public void ProviderAccountBucketAndWindowHistoriesAreIndependent()
    {
        using var tracker = new AiQuotaNotifications();
        var usage = Usage(90);
        AiAccountUsage[] histories =
        [
            usage,
            usage with { Provider = "Claude" },
            usage with { AccountLabel = "second@example.test" },
            usage with { Windows = [usage.Windows![0] with { Bucket = "codex-mini" }] },
            usage with { Windows = [usage.Windows![0] with { LabelKey = "aiUsage.secondary", DurationMinutes = 10080 }] }
        ];
        foreach (var sample in histories)
            Assert.Empty(Evaluate(tracker, sample with { Windows = [sample.Windows![0] with { UsedPercent = 0 }] }));
        foreach (var sample in histories) Assert.Single(Evaluate(tracker, sample));
        foreach (var sample in histories) Assert.Empty(Evaluate(tracker, sample));
    }

    [Fact]
    public void OlderSamplesCannotRearmOrConsumeANewerCycle()
    {
        using var tracker = new AiQuotaNotifications();
        Assert.Empty(Evaluate(tracker, Usage(0)));
        Assert.Single(Evaluate(tracker, Usage(90, Now.AddSeconds(20)), Now.AddSeconds(20)));
        Assert.Empty(Evaluate(tracker, Usage(40), Now.AddSeconds(20)));
        Assert.Empty(Evaluate(tracker, Usage(90, Now.AddSeconds(30)), Now.AddSeconds(30)));
    }

    [Fact]
    public async Task BackgroundMonitorBatchesWindowsAndKeepsLocalClaudeAlertsInLocalOnlyMode()
    {
        var usage = Usage(90) with
        {
            Provider = "Claude",
            Windows = [Usage(0).Windows![0], new("aiUsage.sevenDay", 0, 10080, Now.AddDays(1))]
        };
        await using var monitor = new AiAccountUsageMonitor(new FakeCodex(_ => Task.FromResult(Usage(90))), () => usage, () => Now);
        var received = new TaskCompletionSource<IReadOnlyList<AiQuotaAlert>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new ConcurrentQueue<IReadOnlyList<AiQuotaAlert>>();
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Updated += (_, _) => { if (monitor.Claude.State == AiUsageState.Ready) initialized.TrySetResult(); };
        monitor.QuotaLow += alerts => { batches.Enqueue(alerts); received.TrySetResult(alerts); };
        await monitor.RefreshAsync();
        Assert.Empty(batches);
        monitor.ApplySettings(new() { EnableAiUsageMonitoring = true, EnableLocalOnlyMode = true });
        await initialized.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(batches);
        usage = usage with { Windows = [Usage(90).Windows![0], new("aiUsage.sevenDay", 60, 10080, Now.AddDays(1))] };
        await monitor.RefreshAsync();
        var alerts = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { 20, 50 }, alerts.Select(alert => alert.ThresholdPercent));
        Assert.All(alerts, alert => Assert.Equal("Claude", alert.Provider));
        await monitor.RefreshAsync();
        Assert.Single(batches);
        monitor.ApplySettings(new());
        await monitor.RefreshAsync();
        Assert.Single(batches);
    }

    [Fact]
    public async Task StartingMonitorAtFortyOnePercentIsQuietButLaterCrossingsStillNotify()
    {
        var now = Now;
        var usage = Usage(59) with
        {
            Windows = [Usage(59).Windows![0], new("aiUsage.sevenDay", 59, 10080, Now.AddDays(1), "codex")]
        };
        await using var monitor = new AiAccountUsageMonitor(new FakeCodex(_ => Task.FromResult(usage)), utcNow: () => now);
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new ConcurrentQueue<IReadOnlyList<AiQuotaAlert>>();
        monitor.QuotaLow += batches.Enqueue;
        monitor.Updated += (_, _) => { if (monitor.Codex.State == AiUsageState.Ready) initialized.TrySetResult(); };
        monitor.ApplySettings(new() { EnableAiUsageMonitoring = true, TrackClaudeUsage = false });
        await initialized.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(batches);

        now = now.AddMinutes(3);
        usage = usage with { UpdatedAt = now, Windows = [usage.Windows![0] with { UsedPercent = 81 }, usage.Windows[1]] };
        await monitor.RefreshAsync();
        var alert = Assert.Single(Assert.Single(batches));
        Assert.Equal(20, alert.ThresholdPercent);
        Assert.Equal("aiUsage.primary", alert.Window.LabelKey);
    }

    [Fact]
    public async Task DisablingMonitoringDiscardsALateCodexRead()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AiAccountUsage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new AiAccountUsageMonitor(new FakeCodex(_ =>
        { entered.TrySetResult(); return release.Task; }), utcNow: () => Now);
        var batches = new ConcurrentQueue<IReadOnlyList<AiQuotaAlert>>();
        monitor.QuotaLow += batches.Enqueue;
        monitor.ApplySettings(new() { EnableAiUsageMonitoring = true, TrackClaudeUsage = false });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        monitor.ApplySettings(new());
        release.SetResult(Usage(90));
        await monitor.DisposeAsync();
        Assert.Empty(batches);
    }

    private sealed class FakeCodex(Func<CancellationToken, Task<AiAccountUsage>> read) : ICodexAccountUsageReader
    {
        public Task<AiAccountUsage> ReadAsync(string executable, CancellationToken token) => read(token);
    }
}
