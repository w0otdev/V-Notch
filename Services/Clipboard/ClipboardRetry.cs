using System.Runtime.InteropServices;

namespace VNotch.Services.Clipboard;

internal static partial class ClipboardRetry
{
    internal const int RetryCount = 10;

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_clipboard_retry_delay_ms(int retry, out int outDelayMs);

    internal static int DelayMilliseconds(int retry)
    {
        _ = vn_clipboard_retry_delay_ms(retry, out int delayMs);
        return delayMs;
    }

    internal static async Task RunAsync(Action operation, Action validate, Func<int, Task>? delay = null)
    {
        delay ??= milliseconds => Task.Delay(milliseconds);
        for (int retry = 0; retry <= RetryCount; retry++)
        {
            validate();
            try { operation(); return; }
            catch (ExternalException) when (retry < RetryCount)
            { await delay(DelayMilliseconds(retry)); }
        }
    }
}
