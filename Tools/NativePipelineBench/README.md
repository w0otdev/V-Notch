# Native pipeline benchmark

Compares an isolated native snapshot of a Git commit with selected current
working-tree sources. It does not rebuild or launch V-Notch, change settings,
or use the real clipboard history. Scratch documents, runtime DLLs, source
snapshots and all observations stay in the output directory.

Requires Windows x64, PowerShell 7, .NET 8 SDK, Python 3 (standard library only),
and MSVC 2022 C++ tools/Windows SDK. Restore the application's pinned ONNX Runtime
DirectML 1.24.4 and DirectML 1.15.4 packages first. The script finds vcvars64 from
`obj/native/vcvars64.cache`; pass `-VcVars64Path` if that cache is unavailable.

```powershell
pwsh -File ./scripts/Invoke-NativePipelineBenchmark.ps1 `
  -BaselineCommit 518824be4fc9e497ff0fee16933aba1d490f6664 `
  -OutputPath ./artifacts/native-benchmark-new -Verify
```

Use a new output directory for each full run; existing results are protected
against accidental replacement. `-Verify` repeats kernels, scrubbing, positive
decode/NMS and storage against the same frozen DLLs. The baseline commit must
contain the former exports used by the harness. Both CPU inference paths use
ORT 1.24.4; this controls the runtime version rather than measuring the old
application's ORT 1.31.0 package.

After a successful run, inspect `REPORT.md`, `measurements.csv` and
`raw-samples.csv`. JSON retains every timed sample, allocations, validation
results and sample/batch counts. `environment.json` records hardware, source
and binary hashes. Saved native sources and build response files allow the
same inputs to be rebuilt later. Source snapshots of this runner are saved
under `harness-source/` on new full runs.

Selective commands reuse the existing DLLs without touching their snapshots:

```powershell
$repo = (Get-Location).Path
$out = Join-Path $repo 'artifacts/native-benchmark-20261102'
$runtime = Join-Path $out 'runtime'
$runner = Join-Path $out 'harness/NativePipelineBench.dll'
$env:DOTNET_TieredCompilation = '0'
dotnet $runner verify $repo $out $runtime
dotnet $runner inference512 $repo $out $runtime
python ./Tools/NativePipelineBench/report.py $out
```

Selective commands replace their respective `verification-results.json` or
`inference512-results.json`; copy those files first if retaining multiple
repeats. `run` includes the full-resolution and decoded-512 artwork cases.
`cold` requires two more arguments: backend (`managed-cpu`, `native-cpu`, or
`directml`) and the destination JSON path. The main script invokes cold mode
in nine independent processes, rotating backend order across three rounds.

Benchmarks use warmup, interleaved variants, calibrated microkernel batches
and GC collection outside timing. Inference sessions are measured independently
to avoid competing idle ORT thread pools. A DirectML fallback causes failure
instead of producing misleading GPU numbers. Checks cover FFT tolerance,
scalar/SIMD Oklab tolerance, exact scrubber output, positive decode/NMS equality
and encrypted/plaintext storage round trips. Palette behavior and image
resampling differ, so those comparisons are not output-equivalence tests.

The shared desktop is not isolated from background work or thermal/clock
changes. Clipboard writes have different security/durability behavior. The
audio workload measures DSP rather than actual WASAPI latency. No translation
model is downloaded, and whole-app idle CPU, VRAM and translation quality are
not inferred from kernel measurements. The generated report records these
limits alongside regressions.

To compare the palette/clipboard optimizations with the saved pre-optimization
native DLL, use the focused mode (it also keeps the original HSV/plaintext
reference):

```powershell
pwsh -File ./scripts/Invoke-NativePipelineBenchmark.ps1 `
  -BaselineCommit 518824be4fc9e497ff0fee16933aba1d490f6664 `
  -OutputPath ./artifacts/native-hotpath-new -HotPathsOnly -Verify `
  -PreviousNativeDll ./artifacts/native-benchmark-20261102/native/current.dll
```

This records `hotpath-results.json` and `hotpath-repeat-results.json` and
renders a focused report/CSV. It validates unchanged Oklab palette outputs
against the previous DLL before timing. Clipboard variants separate the
managed stack/pool reader from the native I/O improvement. New small reads
avoid a size query; large reads retry with a wiped pooled buffer. The main
pipeline runner also uses this updated reader for subsequent full runs.
