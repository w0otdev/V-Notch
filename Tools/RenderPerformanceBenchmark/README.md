# Hidden render comparison

This runner loads a supplied, prebuilt application DLL with its adjacent dependencies.
It does not rebuild or launch V-Notch, open windows, start audio capture, initialize
the application services, or change user settings. Use separate processes for the
before and after assemblies so both can retain the same assembly identity.

```powershell
dotnet build Tools/RenderPerformanceBenchmark -c Release
dotnet Tools/RenderPerformanceBenchmark/bin/Release/net8.0-windows10.0.19041.0/RenderPerformanceBenchmark.dll artifacts/baseline-app/V-Notch.dll artifacts/before.json
dotnet Tools/RenderPerformanceBenchmark/bin/Release/net8.0-windows10.0.19041.0/RenderPerformanceBenchmark.dll bin/Release/net8.0-windows10.0.19041.0/win-x64/V-Notch.dll artifacts/after.json
dotnet Tools/RenderPerformanceBenchmark/bin/Release/net8.0-windows10.0.19041.0/RenderPerformanceBenchmark.dll compare artifacts/before.json artifacts/after.json
```

Build the actual before revision into the isolated baseline directory before editing
source; an old binary found on disk is not evidence of the starting revision. Record
the revision, build options, assembly hash and changed files alongside the reports.
Keep the machine idle during measurement. Repeat a matched pair in reverse order
to expose temporal noise; timing from builds/tests running concurrently is unreliable.

The visualizer is configured through compiled reflection delegates. These delegates
do not box the per-frame doubles/TimeSpans or allocate reflection argument arrays.
The baseline and current implementations use the same immediate drawing structure.
Drawing timing includes the same deterministic input generation on both sides, but
excludes audio FFT, animation state machines, UI layout, dispatcher timing and
WPF/DWM presentation.
The progress cases simulate 60/144 updates per media second without waiting, using
the public Render method on a model with a fixed duration and advancing position.
Current builds also call NeedsDrawingUpdate when available before
recording. The "snapped" scenario alternates distinct amplitudes whose rounded pixel
bounds stay identical at constant opacity; it measures exact duplicate drawing
suppression. Other scenarios vary opacity every frame and require a new drawing.

Each case warms for 500 ms and records nine round means plus allocation bytes using
GC.GetAllocatedBytesForCurrentThread. JSON preserves every round. Time summaries are
the median of those round means; no forced GC or working-set trimming is performed.
The raster cases add synchronous RenderTargetBitmap.Clear/Render. This measures
offscreen WPF rasterization, not hardware GPU presentation or display FPS.

864 pixel cases use the same control across resize, four DPI scales, translucent and
opaque brushes, bar/icon blends, check shapes, blended play/pause shapes and the
transition back to bars. Pixel capture forces drawing; separate regression tests
verify the duplicate-drawing guard. SHA256 is computed from raw premultiplied BGRA
pixels, never from image metadata.
Every image must contain visible pixels. Comparison exits nonzero on missing cases,
dimensions or differing pixels. It also reports every timing result, including
regressions; reductions in allocation do not establish lower whole-app working set.

For whole-app RAM, GPU and displayed frame pacing, separately profile the same
workload on the same display/refresh rate using process private bytes/working set,
per-process GPU engine counters and presentation traces. This hidden runner cannot
substitute for those measurements.
