# Service optimization measurements — 2026-10-03

Local BenchmarkDotNet 0.14.0 ShortRun, Release x64, .NET 8.0.29 / SDK 8.0.423,
Windows 11 10.0.26200.9457, AVX2. BenchmarkDotNet could not identify the processor
model. Three warmups and three measured iterations per case, one launch.
Reference source: commit `896c463e2882ac5e0c6e9d5fc8753be81fd993c2`.

| Workload | Before, mean | After, mean | Before → after managed bytes/op |
|---|---:|---:|---:|
| Blur 128×128, radius 8 | 510.2 µs | 222.6 µs | 0 → 0 |
| Blur 128×128, radius 20 | 499.9 µs | 252.5 µs | 0 → 0 |
| Blur 256×256, radius 8 | 2,071.9 µs | 858.1 µs | 1 → 0 |
| Blur 256×256, radius 20 | 2,129.0 µs | 850.3 µs | 2 → 0 |
| Weather JSON, 1 KiB padding | 1.727 µs | 0.674 µs | 9,120 → 1,776 |
| Weather JSON, 64 KiB padding | 81.910 µs | 8.289 µs | 267,196 → 66,288 |
| City cleaning | 146.35 ns | 28.37 ns | 392 → 40 |
| URL string validation | 180.26 ns | 91.29 ns | 112 → 56 |
| Existing Uri validation | 88.93 ns | 0.67 ns | 56 → 0 |
| Color extraction, 64×64, repeated image | 44.926 µs | 2.731 µs | 33,416 → 0 |
| Color extraction, 256×256, repeated image | 299.664 µs | 229.016 µs | 18,440 → 736 |
| Color sampling/bucketing kernel | 32.872 µs | 1.882 µs | 30,976 → 0 |

The sub-nanosecond existing-Uri result uses a warmed, reused URI and inlining;
it is not a measurement of clicking a link or opening a browser. Full color
extraction measurements use warmed immutable-source conversion caches. WPF
resizing still allocates; JSON still allocates its buffer/document. Only the
pixel kernels are asserted to have zero steady-state managed allocation.
The 1–2 B/op baseline blur figures are amortized benchmark harness overhead.

The initial scalar multiply/shift blur was only 9–16% faster than the original.
The final AVX2 row traversal and SSE2 darkening produce the blur results above.
Both paths retain a scalar fallback and match the original output byte-for-byte.
The reciprocal is rounded up at 24 bits: the suggested floor-rounded 16-bit
reciprocal would change some pixel values.

Hardware counters were collected successfully for all eight blur cases via ETW:

| Size / radius | Cache misses/op, before → after | Branch mispredictions/op, before → after |
|---|---:|---:|
| 128 / 8 | 52,346 → 391 | 908 → 391 |
| 128 / 20 | 29,041 → 529 | 675 → 453 |
| 256 / 8 | 245,091 → 3,482 | 2,336 → 1,384 |
| 256 / 20 | 308,765 → 4,389 | 2,415 → 1,400 |

These are hardware-counter estimates for this run, not exact DRAM fetch counts.
Disassembly contains `vpmulld`, `vpsrld` and `vpmulhuw` in the optimized code;
the only variable division in each optimized blur pass initializes its
reciprocal outside the pixel loop.

Validation completed:

- 104 related xUnit cases passed in Release, including 46 optimization cases.
- All 46 optimization cases also passed with `DOTNET_EnableHWIntrinsic=0`.
- 24 benchmark cases measured; the final eight blur cases were remeasured after
  adding SIMD, with hardware counters enabled.
- All 12 baseline/optimized comparisons passed `check_results.py`.
- Final benchmark project build: zero warnings, zero errors.

Raw JSON, CSV, HTML, disassembly and ETW traces are in the local ignored directory
`artifacts/service-optimization/benchmarks`. The corresponding test TRX files
are in `artifacts/service-optimization`. See [README.md](README.md) for commands.

ShortRun confidence intervals are wide on this active desktop. These numbers
are local microbenchmark evidence; they do not establish a 4.2–6.8× speedup,
60/120 FPS, native-memory leak elimination, or end-to-end application latency.
