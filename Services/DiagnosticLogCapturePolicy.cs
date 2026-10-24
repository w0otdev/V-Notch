using System.Runtime.InteropServices;

namespace VNotch.Services;

internal sealed partial class DiagnosticLogCapturePolicy
{
    internal const int MaxEntriesPerSecond = 32;
    private long _windowAndCount = -1;

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_diagnostic_log_try_capture(ref long stateWindowAndCount, long tickCountMs, out byte outCaptured);

    internal bool TryCapture(long tickCountMs)
    {
        _ = vn_diagnostic_log_try_capture(ref _windowAndCount, tickCountMs, out byte captured);
        return captured != 0;
    }
}
