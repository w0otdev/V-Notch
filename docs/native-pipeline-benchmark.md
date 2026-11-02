# Native pipeline benchmark — 2026-11-02

Measured on this Windows workstation. The result is mixed: DirectML inference, native preprocessing and DSP improve; the complete Oklab palette and encrypted clipboard reads take longer. Large sparse-log scrubbing is inconclusive across the two runs.

## Machine and comparison

- CPU: AMD Ryzen 7 5800H with Radeon Graphics, 8 cores / 16 logical processors; 31.4 GiB RAM.
- OS: Microsoft Windows 11 Pro 10.0.26300; Power Scheme GUID: 2f7c08cf-d4fc-4701-9afe-cfc901c12cc3  (Performance).
- Installed GPUs: AMD Radeon(TM) Graphics (driver 31.0.21925.1001); NVIDIA GeForce RTX 3050 Laptop GPU (driver 32.0.16.1664). DirectML requests DXGI adapter 0; its GPU identity was not independently queried.
- Native baseline: `518824be4fc9e497ff0fee16933aba1d490f6664`; current: frozen working-tree native sources. Both DLLs use the same MSVC Release flags. This is a comparison of selected pipeline stages, not two complete application builds.
- Both managed and native CPU inference use ONNX Runtime **1.24.4** to isolate the wrapper/preprocessing changes. DirectML uses that runtime plus DirectML **1.15.4**. The former application's ORT 1.31.0 package was not benchmarked.
- Captured: 2026-11-02T08:33:20.7178410Z; AVX2 enabled: True.

## YOLOX-Nano

Each cell is **median / p95**. Each backend has 63 timed operations in three rotated rounds, with independent sessions and 12 warmup operations per round. Timing includes bitmap copy, preprocessing, inference, output transfer and decode/NMS. Image decode, session creation, application caches, fingerprints, legacy heap trimming and UI rendering are excluded.

| Input | Managed CPU | Native CPU | Native DirectML | Managed CPU / DirectML |
| --- | ---: | ---: | ---: | ---: |
| white-mat-artwork.png 2097x1182 | 22.116 ms / 37.266 ms | 20.444 ms / 27.024 ms | 9.001 ms / 11.238 ms | 2.46x |
| dark-textured-artwork.png 1280x640 | 16.951 ms / 18.480 ms | 16.256 ms / 18.435 ms | 7.872 ms / 9.680 ms | 2.15x |
| white-mat-artwork.png@decoded512 512x288 | 27.712 ms / 65.898 ms | 24.946 ms / 41.386 ms | 10.482 ms / 22.087 ms | 2.64x |
| dark-textured-artwork.png@decoded512 512x256 | 19.812 ms / 30.807 ms | 19.608 ms / 24.320 ms | 7.653 ms / 9.262 ms | 2.59x |

The decoded-512 cases match the 512 px decode cap in `MediaArtworkService`; the full-resolution cases are stress inputs. The two fixture images produced zero final detections on every backend, so these timings do not establish detection accuracy or model equivalence. The old WPF and new native bilinear preprocessing use different resamplers. A separate synthetic positive-output decode/NMS check is included below. The harness refuses to label a CPU fallback as DirectML.

## Kernels, DSP, scrubber and storage

Each timing cell is **median / p95**. Speedup = baseline median / new median: above 1 is faster, below 1 is slower. The check column comes from a second run of the same frozen DLLs, not a pooled estimate. Its CPU kernel times were often about twice the first run's; this was a shared desktop without fixed clocks or exclusive CPU use. Do not interpret these values as a latency guarantee.

| Area / workload | Baseline | New | Speedup | Check speedup |
| --- | ---: | ---: | ---: | ---: |
| FFT: 512 samples + Hamming | 2.801 us / 2.925 us | 2.047 us / 2.119 us | 1.37x | 1.44x |
| FFT: 2048 samples + Hamming | 12.399 us / 12.702 us | 8.911 us / 9.278 us | 1.39x | 1.47x |
| FFT: 4096 samples + Hamming | 26.263 us / 26.883 us | 19.177 us / 19.783 us | 1.37x | 1.49x |
| Palette: 64x64 opaque seeded pixels | 46.379 us / 47.601 us | 114.621 us / 119.365 us | 0.40x | 0.30x |
| Oklab conversion: 64x64 same math | 245.561 us / 252.412 us | 21.418 us / 21.909 us | 11.47x | 14.04x |
| Palette: 256x256 opaque seeded pixels | 1.444 ms / 1.461 ms | 1.949 ms / 1.998 ms | 0.74x | 0.50x |
| Oklab conversion: 256x256 same math | 3.971 ms / 4.116 ms | 339.250 us / 358.200 us | 11.70x | 14.86x |
| Preprocess: white-mat-artwork.png 2097x1182 | 4.062 ms / 4.245 ms | 1.324 ms / 1.738 ms | 3.07x | 3.19x |
| Scrubber native: ordinary log 1 KiB | 13.206 us / 13.787 us | 11.326 us / 11.663 us | 1.17x | 1.21x |
| Scrubber native: credential fields ~64 KiB | 743.940 us / 763.810 us | 708.640 us / 725.250 us | 1.05x | 1.13x |
| Scrubber native: sparse 20 MiB ASCII / 40 MiB UTF16 | 361.704 ms / 386.338 ms | 505.235 ms / 515.326 ms | 0.72x | 1.07x |
| Scrubber native: dense ~20 MiB ASCII / 40 MiB UTF16 | 206.247 ms / 220.219 ms | 205.634 ms / 254.710 ms | 1.00x | 1.09x |
| Audio DSP: 512 stereo float32 frames / 11.61 ms audio | 11.686 us / 12.588 us | 8.138 us / 8.907 us | 1.44x | — |
| Clipboard durable write: 1158 byte JSON document | 983.200 us / 1.240 ms | 1.038 ms / 1.541 ms | 0.95x | 0.96x |
| Clipboard read + deserialize: 1158 byte JSON document | 71.664 us / 78.787 us | 200.369 us / 213.797 us | 0.36x | 0.36x |
| Clipboard durable write: 65670 byte JSON document | 1.027 ms / 1.281 ms | 1.165 ms / 1.910 ms | 0.88x | 0.88x |
| Clipboard read + deserialize: 65670 byte JSON document | 130.837 us / 143.790 us | 289.877 us / 297.763 us | 0.45x | 0.45x |
| YOLOX decode + NMS: 3549x85 output, 32 candidates (check run only) | 53.737 us / 68.213 us | 58.185 us / 75.247 us | 0.92x | — |
| YOLOX decode + NMS: 3549x85 output, 300 candidates (check run only) | 121.713 us / 129.606 us | 113.037 us / 122.351 us | 1.08x | — |

The FFT comparison is old radix-2/AVX2 versus fused radix-4/FMA. The palette comparison is old HSV selection versus new Oklab selection and therefore changes color-selection behavior. The Oklab conversion rows compare scalar and SIMD implementations of the same math; their speedup does not carry over to the complete palette.

The scrubber uses preallocated native output buffers. The sparse input contains a 20 MiB run of `x` between two credentials, a trigger-heavy regression fixture; it is not a representative distribution of ordinary application logs. It regressed in the first pass and improved in the check pass with large p95 tails, so no stable large-log speedup is established. Exact sanitized outputs match. The dense case contains 22,020,096 UTF-16 characters, despite the approximate workload label.

Audio timings cover DSP on synthetic 512-frame stereo float32 packets; buffers are reused on both paths. WASAPI device capture, callback frequency, scheduling latency and whole-application CPU usage are outside this measurement.

Clipboard timings include C# JSON serialization/deserialization. The baseline is plaintext JSON with a durable flush and replacement; the new path additionally authenticates/encrypts records and publishes commits with separate durability barriers. This measures the cost of the new behavior, not equal security features. All documents are synthetic and stored under the artifact directory. In the original two-call reader, the size query opens/maps the file but does not decrypt; only the payload read authenticates. Later stack/pool readers attempt the read directly and retry if the buffer is too small.

## Managed allocations

Measured bytes allocated on the calling managed thread per operation, after warmup. Native heap allocations, GPU memory, retained buffers and allocations on other threads are excluded. Zero here does not mean zero total memory use. Native scrubber results exclude the application's string/output allocation.

| Area / workload | Variant | Managed bytes/op |
| --- | --- | ---: |
| Preprocess: white-mat-artwork.png 2097x1182 | HEAD WPF resize + scalar planar | 736.0 |
| Preprocess: white-mat-artwork.png 2097x1182 | native bilinear/AVX2 + bitmap copy | 0.0 |
| Clipboard durable write: 1158 byte JSON document | HEAD JSON serialize + Flush + replace | 4,514.6 |
| Clipboard durable write: 1158 byte JSON document | JSON serialize + native AES-GCM append | 1,184.0 |
| Clipboard read + deserialize: 1158 byte JSON document | HEAD JSON FileStream | 2,672.3 |
| Clipboard read + deserialize: 1158 byte JSON document | mapped authenticated record, 2 native calls | 3,616.0 |
| Clipboard durable write: 65670 byte JSON document | HEAD JSON serialize + Flush + replace | 4,514.6 |
| Clipboard durable write: 65670 byte JSON document | JSON serialize + native AES-GCM append | 65,696.0 |
| Clipboard read + deserialize: 65670 byte JSON document | HEAD JSON FileStream | 131,697.7 |
| Clipboard read + deserialize: 65670 byte JSON document | mapped authenticated record, 2 native calls | 197,152.0 |
| YOLOX complete inference: white-mat-artwork.png 2097x1182 | managed-cpu | 2,072.0 |
| YOLOX complete inference: white-mat-artwork.png 2097x1182 | native-cpu | 0.0 |
| YOLOX complete inference: white-mat-artwork.png 2097x1182 | directml | 0.0 |
| YOLOX complete inference: dark-textured-artwork.png 1280x640 | managed-cpu | 2,072.0 |
| YOLOX complete inference: dark-textured-artwork.png 1280x640 | native-cpu | 0.0 |
| YOLOX complete inference: dark-textured-artwork.png 1280x640 | directml | 0.0 |

## Fresh-process startup and memory

Three fresh processes per backend. The Windows file cache was not flushed. Session creation includes runtime initialization; artwork decode and benchmark DLL loading precede the timer. Ranges are min–max, not p95. Memory is the benchmark host's process-private/working-set increase from before session creation to after first inference; it is not application memory or VRAM.

| Backend | Create median [range], ms | First inference median [range], ms | Private delta median, MiB | Working-set delta median, MiB |
| --- | ---: | ---: | ---: | ---: |
| managed-cpu | 139.66 [127.06–192.94] | 45.13 [44.57–50.55] | 24.03 | 33.77 |
| native-cpu | 106.91 [100.39–128.01] | 21.94 [21.33–22.47] | 32.88 | 40.96 |
| directml | 410.00 [372.11–2376.88] | 13.90 [13.03–14.30] | 105.91 | 110.83 |

DirectML's first session in this run took substantially longer than the next two. Driver/compiler caching and other desktop activity were not controlled. Native inference's zero managed allocations do not establish lower process RAM: it also retains a full-resolution BGRA source buffer.

## Clipboard startup and file footprint

These are first-pass measurements only. The first encrypted write includes first-use key setup; subsequent writes use the same store context. Check-pass writes reuse the scratch directory and are excluded from this startup comparison.

The check pass's old `IncludesDpapiKeyCreation` flag was incorrect for its reused directory. The runner now checks key existence; the first-pass startup observations below are unaffected.

- First encrypted 1,158-byte document: 9.665 ms; new DPAPI key: True.
- After the write sequence for 1,158-byte payloads: plaintext 1,158 bytes, encrypted log 110,592 bytes.
- First encrypted 65,670-byte document: 5.291 ms; new DPAPI key: False.
- After the write sequence for 65,670-byte payloads: plaintext 65,670 bytes, encrypted log 151,552 bytes.

File sizes are logical lengths after warmup and repeated updates, including retained obsolete versions and page alignment. They are not physical disk allocation or a single-record size; compaction makes them depend on the write sequence.

## Validation and remaining limits

- FFT baseline/current maximum absolute difference: 7.45e-09; Oklab scalar/SIMD: 7.11e-07. The checked bounds were 1e-6 and 1e-5 respectively.
- Scrubber output equality, nonempty synthetic YOLOX decode/NMS equality and encrypted/plaintext document round trips passed. Complete HSV/Oklab palette output equality is not expected.
- Microkernels: 41 rotated/interleaved samples, calibrated batches targeting 8 ms; large logs: 15 single-operation samples; durable writes: 81 single-operation samples. JSON contains actual sample/batch counts and all raw observations. p95 is the nearest-rank statistic of measured batch averages, not a production tail-latency estimate.
- The benchmark harness builds with zero warnings/errors. This benchmark does not rerun or replace the implementation's earlier 931-test regression selection.
- Privacy/media idle CPU and event-notification latency were not measured. No production app was restarted and no actual clipboard history was read or migrated.
- No GGUF model was present in the configured local translation-model directory. Translation token throughput, Q8/F16 KV-cache RAM/VRAM and quality were not measured. NPU providers were not tested.
- The proposed 1.2 ms inference, 60% FFT cycle reduction, fourfold complete-palette speedup and 35% translation-memory reduction are not established by these measurements.

The next performance work should investigate complete-palette overhead, repeated file opens/mappings and large-document serialization allocations; repeat the sparse-log case under a quiet, controlled load before changing its algorithm based on timing alone.

## Artifacts and reproduction

Artifact directory: `D:/CodeShii/V-Notch/artifacts/native-benchmark-20261102`. Machine-specific artifacts are ignored by Git; keep this directory when sharing raw evidence.

- `results.json`, optional `verification-results.json` and `inference512-results.json`: statistics, raw samples and validation details.
- `measurements.csv`, `raw-samples.csv`: summary and per-sample exports, with explicit run labels.
- `cold-*.json`, `environment.json`: fresh-process observations, hardware, versions and binary/model/source SHA-256 hashes.
- `baseline/Native`, `current/Native`, `native/`, `runtime/` and `build-*.log`: frozen inputs and build evidence.

```powershell
./scripts/Invoke-NativePipelineBenchmark.ps1 -BaselineCommit 518824be4fc9e497ff0fee16933aba1d490f6664
# Re-render an existing artifact directory without rerunning the benchmarks:
python ./Tools/NativePipelineBench/report.py ./artifacts/native-benchmark-20261102
```

A new invocation snapshots the then-current working tree. Use the saved native snapshots to reproduce the exact current binary input from this report. See `Tools/NativePipelineBench/README.md` for prerequisites and selective verification commands.
