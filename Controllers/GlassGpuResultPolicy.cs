namespace VNotch.Controllers;

internal enum GlassGpuRenderOutcome { Submit, KeepPrevious, UseBackdropFallback, Fail }

// E_PENDING means the per-window compositor cannot provide this crop yet. Only that
// route may fall back to Magnifier/composition. Desktop/fence failures need recovery;
// successful-but-empty frames must retain the existing surface and pending geometry.
internal static class GlassGpuResultPolicy
{
    internal const int Pending = unchecked((int)0x8000000A);

    internal static GlassGpuRenderOutcome Classify(bool exactWindows, int hresult) =>
        exactWindows && hresult == Pending ? GlassGpuRenderOutcome.UseBackdropFallback :
        hresult < 0 ? GlassGpuRenderOutcome.Fail :
        hresult == 0 ? GlassGpuRenderOutcome.Submit : GlassGpuRenderOutcome.KeepPrevious;
}
