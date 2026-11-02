# Palette and encrypted clipboard optimization — 2026-11-02

The optimized Oklab palette preserves its color-selection algorithm and removes per-pixel angle calculations on AVX2. Small encrypted clipboard reads now avoid the size-query round trip, mapping overhead and managed plaintext arrays. Encryption, authentication and the existing file format are retained.

Machine: AMD Ryzen 7 5800H with Radeon Graphics; Microsoft Windows 11 Pro 10.0.26300; 31.4 GiB RAM. AVX2: True.

HSV/plaintext baseline commit: `518824be4fc9e497ff0fee16933aba1d490f6664`. Previous Oklab/encrypted DLL SHA-256: `62FEFF7AD4190A7BB3BA34AC879EF2AB80F7CBF030332D6B6FE9505CB89097DF`. Current DLL SHA-256: `324E51F5388FA5D34FC91DF706683033E2DC628E84BD5EEA2E313EA21E385C5A`. Both native snapshots use the same MSVC Release flags. The previous DLL is the frozen current DLL from the earlier pipeline benchmark.

## Paired results

Median microseconds per operation; lower is better. Speedup compares previous Oklab/encrypted implementation with the optimized implementation. Each variant has 61 rotated/interleaved samples with warmup and equal calibrated batches. Repeat results use the same frozen binaries. This is a shared desktop with variable clocks and background load; compare paired ratios rather than absolute times from separate runs.

| Workload | Original HSV/plain JSON | Previous Oklab/encrypted | Optimized | Speedup | Repeat speedup |
| --- | ---: | ---: | ---: | ---: | ---: |
| Palette: 36x36 opaque seeded pixels | 17.169 | 47.403 | 16.171 | 2.93x | 2.95x |
| Palette: 64x64 opaque seeded pixels | 49.882 | 122.482 | 43.267 | 2.83x | 2.90x |
| Palette: 256x256 opaque seeded pixels | 1,526.833 | 2,112.400 | 670.667 | 3.15x | 3.17x |
| Clipboard read + deserialize: 1158 byte JSON document | 103.917 | 258.457 | 102.917 | 2.51x | 2.55x |
| Clipboard read + deserialize: 65670 byte JSON document | 155.690 | 336.510 | 243.455 | 1.38x | 1.49x |
| Clipboard read + deserialize: 300134 byte JSON document | 356.892 | 851.617 | 724.942 | 1.17x | 1.10x |

The original HSV palette chooses different colors; matching or beating its throughput does not imply output equality with HSV. The 36 px case represents the application's default palette sample size. Plain JSON has no record encryption/authentication; its column is a reference for the added security cost.

## All variants and tails

| Workload | Variant | Median, us | p95, us | Managed B/op |
| --- | --- | ---: | ---: | ---: |
| Palette: 36x36 opaque seeded pixels | HEAD HSV | 17.169 | 24.725 | 0.0 |
| Palette: 36x36 opaque seeded pixels | previous Oklab | 47.403 | 68.335 | 0.0 |
| Palette: 36x36 opaque seeded pixels | optimized Oklab | 16.171 | 26.205 | 0.0 |
| Palette: 64x64 opaque seeded pixels | HEAD HSV | 49.882 | 77.496 | 0.0 |
| Palette: 64x64 opaque seeded pixels | previous Oklab | 122.482 | 177.649 | 0.0 |
| Palette: 64x64 opaque seeded pixels | optimized Oklab | 43.267 | 66.555 | 0.0 |
| Palette: 256x256 opaque seeded pixels | HEAD HSV | 1,526.833 | 1,625.533 | 0.0 |
| Palette: 256x256 opaque seeded pixels | previous Oklab | 2,112.400 | 2,362.833 | 0.0 |
| Palette: 256x256 opaque seeded pixels | optimized Oklab | 670.667 | 878.667 | 0.0 |
| Clipboard read + deserialize: 1158 byte JSON document | HEAD plaintext FileStream | 103.917 | 115.736 | 2,672.3 |
| Clipboard read + deserialize: 1158 byte JSON document | previous encrypted two-call + array | 258.457 | 279.140 | 3,616.0 |
| Clipboard read + deserialize: 1158 byte JSON document | previous native + stack/pool JSON | 136.479 | 148.845 | 2,432.0 |
| Clipboard read + deserialize: 1158 byte JSON document | optimized native + stack/pool JSON | 102.917 | 116.457 | 2,432.0 |
| Clipboard read + deserialize: 65670 byte JSON document | HEAD plaintext FileStream | 155.690 | 187.458 | 131,711.1 |
| Clipboard read + deserialize: 65670 byte JSON document | previous encrypted two-call + array | 336.510 | 402.358 | 197,152.0 |
| Clipboard read + deserialize: 65670 byte JSON document | previous native + stack/pool JSON | 328.319 | 372.426 | 131,456.0 |
| Clipboard read + deserialize: 65670 byte JSON document | optimized native + stack/pool JSON | 243.455 | 284.542 | 131,456.0 |
| Clipboard read + deserialize: 300134 byte JSON document | HEAD plaintext FileStream | 356.892 | 526.992 | 601,684.3 |
| Clipboard read + deserialize: 300134 byte JSON document | previous encrypted two-call + array | 851.617 | 1,176.825 | 900,866.1 |
| Clipboard read + deserialize: 300134 byte JSON document | previous native + stack/pool JSON | 804.575 | 1,194.508 | 601,372.3 |
| Clipboard read + deserialize: 300134 byte JSON document | optimized native + stack/pool JSON | 724.942 | 1,173.075 | 601,444.0 |

The intermediate clipboard variant uses the old native DLL with the new stack/pool JSON reader. This separates the managed-call/allocation improvement from the native I/O change. Larger documents still require a capacity retry and remain slower than plain JSON. No claim is made that encryption or mapping is always faster than buffered plaintext I/O.

## Implementation

- Palette: AVX2 computes Oklab, chroma, radial weights and the same 36 angular sectors in blocks of eight. Sector membership uses tangent comparisons in each quadrant instead of `atan2`. Narrow rows and tails use a bounded padded copy; the 36 px case no longer runs four expensive scalar cube-root conversions per row. Unsupported CPUs retain the scalar implementation. No pixels are subsampled or colors quantized to a lower-resolution RGB grid.
- Clipboard: a 4 KiB stack buffer attempts the authenticated read immediately; larger payloads use a wiped pooled buffer. JSON is deserialized before wiping, without returning an intermediate managed byte array to the history store. Native reads fetch the commit/header pages and small ciphertext records with buffered `ReadFile`; records above 256 KiB retain the mapping path. Header/commit bounds and AES-GCM authentication are unchanged. Plaintext remains in a native temporary until authentication succeeds, and temporary buffers are wiped.
- Path normalization uses a stack buffer for typical paths and a bounded dynamic fallback for long paths. There is no plaintext cache and no persistent file/mapping cache that could hide subsequent changes or tampering.

## Validation and evidence

- 167 previous/optimized palette comparisons were byte-for-byte identical across seeded images, transparency, padded rows, narrow widths and solid colors. This is a tested corpus, not an exhaustive floating-point equivalence proof; values at sector boundaries can differ by rounding.
- 1,158-byte JSON document round trips matched across plaintext, previous encrypted, intermediate and optimized readers.
- 65,670-byte JSON document round trips matched across plaintext, previous encrypted, intermediate and optimized readers.
- 300,134-byte JSON document round trips matched across plaintext, previous encrypted, intermediate and optimized readers.
- Release regression selection: **687 passed, 0 failed** (687 total). Results: `tests/hotpath-final.trx`.
- Regression coverage includes tampered ciphertext, truncated headers, key loss, recovery, migration, pooled/mapped payloads and palette contrast. The production application and its real clipboard history were not restarted or migrated.
- All native/source SHA-256 hashes and build logs are retained. JSON contains actual sample/batch counts, raw observations and validation details; CSV exports separate primary and repeat runs. p95 is the nearest-rank statistic of batch averages, not a production tail guarantee. Managed allocation measurements cover the calling thread only.
- Artifact directory: `D:/CodeShii/V-Notch/artifacts/native-hotpath-final-20261102`.

```powershell
pwsh -File ./scripts/Invoke-NativePipelineBenchmark.ps1 `
  -BaselineCommit 518824be4fc9e497ff0fee16933aba1d490f6664 `
  -OutputPath ./artifacts/native-hotpath-new -HotPathsOnly -Verify `
  -PreviousNativeDll ./artifacts/native-benchmark-20261102/native/current.dll
```

The earlier full pipeline report remains a historical record. Its description incorrectly said the size query also authenticated the record; that text has been corrected. The size query opened/mapped the file but did not decrypt. Its recorded timings are unchanged.
