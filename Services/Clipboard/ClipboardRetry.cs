using System.Runtime.InteropServices;

namespace VNotch.Services.Clipboard;

internal static class ClipboardRetry
{
    internal const int RetryCount = 10;
    internal static int DelayMilliseconds(int retry) => Math.Min(30 << Math.Min(retry, 3), 240);

    internal static async Task RunAsync(Action operation, Action validate, Func<int, Task>? delay = null)
    {
        delay ??= milliseconds => Task.Delay(milliseconds);
        for (int retry = 0; ; retry++)
        {
            validate();
            try { operation(); return; }
            catch (ExternalException) when (retry < RetryCount)
            { await delay(DelayMilliseconds(retry)); }
        }
    }
}
