using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;
using VNotch.ViewModels;

namespace VNotch.Tests;

/// <summary>
/// Condition-based waiting helpers that replace fixed sleeps in tests. Prefer
/// <see cref="WpfFrameWaiter"/> when the condition advances with WPF rendering; use
/// <see cref="UntilAsync"/> for state that changes on timers, background work, or
/// dispatcher callbacks that do not need a rendered frame.
/// </summary>
internal static class TestWait
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds or <paramref name="timeout"/> elapses.
    /// On a dispatcher thread the awaits yield to the dispatcher, so queued timers and
    /// animations keep running while the test waits.
    /// </summary>
    internal static async Task UntilAsync(Func<bool> condition, string description,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed >= limit)
                throw new TimeoutException($"Timed out after {limit.TotalSeconds:0.#}s waiting for {description}.");
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// Waits until <paramref name="condition"/> has held on <paramref name="frames"/> consecutive
    /// WPF rendering frames. A single matching frame can be a spring or BackEase crossing its
    /// target mid-flight; requiring a short streak proves the motion actually settled.
    /// </summary>
    internal static Task UntilStableAsync(Func<bool> condition, string description, int frames = 3,
        CancellationToken cancellationToken = default)
    {
        int streak = 0;
        return WpfFrameWaiter.UntilAsync(() =>
        {
            streak = condition() ? streak + 1 : 0;
            return streak >= frames;
        }, description, cancellationToken);
    }

    /// <summary>
    /// Asserts <paramref name="invariant"/> on every rendered frame for at least
    /// <paramref name="window"/>. Use for negative checks ("nothing changes after X") where a
    /// fixed sleep used to stand in for the observation period.
    /// </summary>
    internal static Task HoldsForAsync(Action invariant, TimeSpan window, string description,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        int observed = 0;
        return WpfFrameWaiter.UntilAsync(() =>
        {
            invariant();
            observed++;
            return observed >= 3 && clock.Elapsed >= window;
        }, description, cancellationToken);
    }

    /// <summary>Rounds like xunit's <c>Assert.Equal(double, double, int precision)</c>.</summary>
    internal static bool Near(double expected, double actual, int precision = 3)
        => Math.Abs(Math.Round(expected, precision) - Math.Round(actual, precision)) < 1e-9;

    /// <summary>
    /// Expires the transport throttle in <see cref="MediaViewModel"/> so the next
    /// play/pause/next/previous request is accepted right away. The view model rejects
    /// commands issued within 200 ms of the previous one; tests used to sleep 300 ms to clear it.
    /// </summary>
    internal static void ExpireMediaThrottle(MediaViewModel viewModel)
    {
        var field = typeof(MediaViewModel).GetField("_lastAction", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(MediaViewModel), "_lastAction");
        field.SetValue(viewModel, DateTime.UtcNow.AddSeconds(-5));
    }

    /// <summary>Runs pending dispatcher work at or above <paramref name="priority"/>.</summary>
    internal static async Task DrainDispatcherAsync(DispatcherPriority priority = DispatcherPriority.Background)
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, priority);
    }
}
