namespace VNotch.Services;

public static class TaskExtensions
{
#pragma warning disable S3168 // "async void" is intentional for top-level fire-and-forget task execution
    public static async void SafeFireAndForget(this Task task, string category = "FIRE-FORGET")
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the underlying task is cancelled; swallow safely.
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(category, ex, "Unhandled exception in fire-and-forget task");
            CrashReporter.LogCrash($"FireAndForget.{category}", ex, "Unhandled exception in fire-and-forget task", isTerminating: false);
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"[{category}] Fire-and-forget exception: {ex.Message}");
#endif
        }
    }
#pragma warning restore S3168
}
