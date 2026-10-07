# Service performance benchmarks

Windows x64, .NET SDK pinned by the repository's global.json, BenchmarkDotNet
0.14.0. Run from the repository root:

```powershell
./Tools/VNotch.Benchmarks/run.ps1
```

Each wrapper run writes a fresh timestamped folder under
`artifacts/service-optimization` and a full run automatically checks its results.
To recheck a saved run, pass its `results` directory to `check_results.py`.

The wrapper first builds the actual application in Release. The benchmark project
references that DLL directly: BenchmarkDotNet's generated project otherwise
redirects WPF intermediate paths and rebuilds the entire application.
Rebuild the app before invoking `dotnet run` directly after service changes.

Baselines are copied from commit `896c463e2882ac5e0c6e9d5fc8753be81fd993c2`.
Only class/interface names, kernel accessibility, and extraction of the original
color sampling body were changed to allow side-by-side execution. Do not update
them together with the optimized implementations.

Measurements cover:

- Blur: two horizontal/vertical passes and darkening, at 128 and 256 pixels with
  radii 8 and 20. Identical input is copied before each invocation. Excludes WPF
  decode/resize, bitmap creation, and Task scheduling.
- Weather: bounded body read and JSON parsing of UTF-8 in-memory HTTP bodies.
  Excludes network latency and result-string creation. The document is disposed
  in both cases; this measures reduced allocation, not zero allocation.
- City: original LINQ cleaning versus the stack buffer, same Unicode input.
- URL: string and existing-Uri validation. Never launches external applications.
- Color: complete extraction from frozen PNG-decoded images at 64 and 256 pixels,
  with repeated-source caches warmed. The separate kernel isolates sampling and
  bucketing; only that kernel claims zero steady-state managed allocation.

All cases use MemoryDiagnoser. Blur, city, and color kernels also use
DisassemblyDiagnoser. Inspect `*-asm.md` for the multiply/shift instructions;
one division to initialize each blur pass's reciprocal is expected.

```powershell
./Tools/VNotch.Benchmarks/run.ps1 -Filter '*BlurBenchmarks*' -Counters
```

Hardware counters are optional and require a supported Windows machine with
an elevated ETW session. They cannot run through InProcessToolchain; missing
counter results do not demonstrate a cache improvement. See the official
[diagnoser restrictions](https://benchmarkdotnet.org/articles/configs/diagnosers.html).

For stronger timing evidence use `-Job medium` on an idle machine. The short job
is a local regression screen, not evidence of an application FPS improvement.
The results checker requires every configured comparison and rejects missing
statistics, >10% mean-time regression, allocation increases, and nonzero
allocation in the color kernel (blur allows BDN's 1 B/op harness noise; exact
zero allocation is asserted in xUnit). Correctness is separately covered by
`ServiceOptimizationTests`, including every supported blur sum, edges, alpha,
color equivalence, concurrency, bounded streaming, cancellation and URL policy.

```powershell
dotnet test Tests/VNotch.Tests.csproj -c Release --filter 'FullyQualifiedName~ServiceOptimizationTests|FullyQualifiedName~WeatherServiceTests|FullyQualifiedName~NetworkPrivacyTests|FullyQualifiedName~SecurityAuditTests'
```
