using System.Windows.Media;

namespace VNotch.Tests;

internal static class WpfFrameWaiter
{
    internal static async Task UntilAsync(Func<bool> condition, string description, CancellationToken cancellationToken = default)
    {
        if (condition()) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRendering(object? sender, EventArgs args)
        {
            try { if (condition()) completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }
        CompositionTarget.Rendering += OnRendering;
        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Timed out waiting for {description} on a rendering event.", ex);
        }
        finally { CompositionTarget.Rendering -= OnRendering; }
    }

    internal static Task NextAsync(CancellationToken cancellationToken = default)
    {
        bool nextFrame = false;
        return UntilAsync(() => { bool ready = nextFrame; nextFrame = true; return ready; },
            "the next WPF frame", cancellationToken);
    }
}
