"""Render the recorded benchmark runs without third-party Python packages."""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import statistics
from collections import defaultdict
from pathlib import Path


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def grouped(run):
    groups = defaultdict(list)
    for measurement in run["Measurements"]:
        groups[measurement["Area"], measurement["Workload"]].append(measurement)
    return groups


def time(us):
    return f"{us / 1000:,.3f} ms" if us >= 1000 else f"{us:,.3f} us"


def cell(measurement):
    return f"{time(measurement['MedianUs'])} / {time(measurement['P95Us'])}"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    root = args.output.resolve()
    if (root / "hotpath-results.json").is_file():
        from hotpath_report import render
        render(root)
        return
    runs = {"primary": load(root / "results.json")}
    for name, file in (("verification", "verification-results.json"), ("decoded512", "inference512-results.json")):
        if (root / file).is_file():
            runs[name] = load(root / file)
    primary = runs["primary"]
    env = primary["Environment"]
    groups = grouped(primary)
    verification = grouped(runs.get("verification", {"Measurements": []}))
    extra = grouped(runs.get("decoded512", {"Measurements": []}))

    fields = [key for key in primary["Measurements"][0] if key != "RawUs"]
    with (root / "measurements.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=["Run", *fields])
        writer.writeheader()
        for run, data in runs.items():
            for m in data["Measurements"]:
                writer.writerow({"Run": run, **{key: m[key] for key in fields}})
    with (root / "raw-samples.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.writer(stream)
        writer.writerow(["Run", "Area", "Workload", "Variant", "Sample", "Batch", "MicrosecondsPerOperation"])
        for run, data in runs.items():
            for m in data["Measurements"]:
                for index, sample in enumerate(m["RawUs"], start=1):
                    writer.writerow([run, m["Area"], m["Workload"], m["Variant"], index, m["Batch"], sample])

    out = [f"# Native pipeline benchmark — {primary['Timestamp'][:10]}", "",
           "Measured on this Windows workstation. The result is mixed: DirectML inference, native preprocessing and DSP improve; the complete Oklab palette and encrypted clipboard reads take longer. Large sparse-log scrubbing is inconclusive across the two runs.", "",
           "## Machine and comparison", "",
           f"- CPU: {env['Cpu'][0]['Name'].strip()}, {env['Cpu'][0]['NumberOfCores']} cores / {env['Cpu'][0]['NumberOfLogicalProcessors']} logical processors; {env['Computer'][0]['TotalPhysicalMemory'] / 2**30:.1f} GiB RAM.",
           f"- OS: {env['Os'][0]['Caption']} {env['Os'][0]['Version']}; {env['PowerScheme']}.",
           "- Installed GPUs: " + "; ".join(f"{gpu['Name']} (driver {gpu['DriverVersion']})" for gpu in env["Gpu"]) + ". DirectML requests DXGI adapter 0; its GPU identity was not independently queried.",
           f"- Native baseline: `{env['BaselineCommit']}`; current: frozen working-tree native sources. Both DLLs use the same MSVC Release flags. This is a comparison of selected pipeline stages, not two complete application builds.",
           "- Both managed and native CPU inference use ONNX Runtime **1.24.4** to isolate the wrapper/preprocessing changes. DirectML uses that runtime plus DirectML **1.15.4**. The former application's ORT 1.31.0 package was not benchmarked.",
           f"- Captured: {env['CapturedAtUtc']}; AVX2 enabled: {primary['NativeAvx2']}.", "",
           "## YOLOX-Nano", "",
           "Each cell is **median / p95**. Each backend has 63 timed operations in three rotated rounds, with independent sessions and 12 warmup operations per round. Timing includes bitmap copy, preprocessing, inference, output transfer and decode/NMS. Image decode, session creation, application caches, fingerprints, legacy heap trimming and UI rendering are excluded.", "",
           "| Input | Managed CPU | Native CPU | Native DirectML | Managed CPU / DirectML |",
           "| --- | ---: | ---: | ---: | ---: |"]
    inference_groups = {key: values for source in (groups, extra) for key, values in source.items() if key[0] == "YOLOX complete inference"}
    for (_, workload), values in inference_groups.items():
        variants = {m["Variant"]: m for m in values}
        a, b, c = (variants[name] for name in ("managed-cpu", "native-cpu", "directml"))
        out.append(f"| {workload} | {cell(a)} | {cell(b)} | {cell(c)} | {a['MedianUs'] / c['MedianUs']:.2f}x |")
    out += ["", "The decoded-512 cases match the 512 px decode cap in `MediaArtworkService`; the full-resolution cases are stress inputs. The two fixture images produced zero final detections on every backend, so these timings do not establish detection accuracy or model equivalence. The old WPF and new native bilinear preprocessing use different resamplers. A separate synthetic positive-output decode/NMS check is included below. The harness refuses to label a CPU fallback as DirectML.", "",
            "## Kernels, DSP, scrubber and storage", "",
            "Each timing cell is **median / p95**. Speedup = baseline median / new median: above 1 is faster, below 1 is slower. The check column comes from a second run of the same frozen DLLs, not a pooled estimate. Its CPU kernel times were often about twice the first run's; this was a shared desktop without fixed clocks or exclusive CPU use. Do not interpret these values as a latency guarantee.", "",
            "| Area / workload | Baseline | New | Speedup | Check speedup |",
            "| --- | ---: | ---: | ---: | ---: |"]
    pairs = {key: values for key, values in groups.items() if key[0] != "YOLOX complete inference"}
    for key, values in verification.items():
        if key not in pairs:
            pairs[key] = values
    for key, values in pairs.items():
        a, b = values
        checked = verification.get(key)
        repeat = f"{checked[0]['MedianUs'] / checked[1]['MedianUs']:.2f}x" if checked and key in groups else "—"
        suffix = " (check run only)" if key not in groups else ""
        out.append(f"| {key[0]}: {key[1]}{suffix} | {cell(a)} | {cell(b)} | {a['MedianUs'] / b['MedianUs']:.2f}x | {repeat} |")
    out += ["",
            "The FFT comparison is old radix-2/AVX2 versus fused radix-4/FMA. The palette comparison is old HSV selection versus new Oklab selection and therefore changes color-selection behavior. The Oklab conversion rows compare scalar and SIMD implementations of the same math; their speedup does not carry over to the complete palette.", "",
            "The scrubber uses preallocated native output buffers. The sparse input contains a 20 MiB run of `x` between two credentials, a trigger-heavy regression fixture; it is not a representative distribution of ordinary application logs. It regressed in the first pass and improved in the check pass with large p95 tails, so no stable large-log speedup is established. Exact sanitized outputs match. The dense case contains 22,020,096 UTF-16 characters, despite the approximate workload label.", "",
            "Audio timings cover DSP on synthetic 512-frame stereo float32 packets; buffers are reused on both paths. WASAPI device capture, callback frequency, scheduling latency and whole-application CPU usage are outside this measurement.", "",
            "Clipboard timings include C# JSON serialization/deserialization. The baseline is plaintext JSON with a durable flush and replacement; the new path additionally authenticates/encrypts records and publishes commits with separate durability barriers. This measures the cost of the new behavior, not equal security features. All documents are synthetic and stored under the artifact directory. In the original two-call reader, the size query opens/maps the file but does not decrypt; only the payload read authenticates. Later stack/pool readers attempt the read directly and retry if the buffer is too small.", "",
            "## Managed allocations", "",
            "Measured bytes allocated on the calling managed thread per operation, after warmup. Native heap allocations, GPU memory, retained buffers and allocations on other threads are excluded. Zero here does not mean zero total memory use. Native scrubber results exclude the application's string/output allocation.", "",
            "| Area / workload | Variant | Managed bytes/op |",
            "| --- | --- | ---: |"]
    for (area, workload), values in groups.items():
        if area == "YOLOX complete inference" or any(m["ManagedBytesPerOperation"] for m in values):
            for m in values:
                out.append(f"| {area}: {workload} | {m['Variant']} | {m['ManagedBytesPerOperation']:,.1f} |")

    cold = defaultdict(list)
    for file in sorted(root.glob("cold-*.json")):
        sample = load(file)
        cold[sample["Backend"]].append(sample)
    out += ["", "## Fresh-process startup and memory", "",
            "Three fresh processes per backend. The Windows file cache was not flushed. Session creation includes runtime initialization; artwork decode and benchmark DLL loading precede the timer. Ranges are min–max, not p95. Memory is the benchmark host's process-private/working-set increase from before session creation to after first inference; it is not application memory or VRAM.", "",
            "| Backend | Create median [range], ms | First inference median [range], ms | Private delta median, MiB | Working-set delta median, MiB |",
            "| --- | ---: | ---: | ---: | ---: |"]
    for backend in ("managed-cpu", "native-cpu", "directml"):
        samples = cold.get(backend, [])
        if not samples:
            continue
        def span(key):
            values = [s[key] for s in samples]
            return f"{statistics.median(values):.2f} [{min(values):.2f}–{max(values):.2f}]"
        private = statistics.median(s["PrivateBytesAfterFirstRun"] - s["PrivateBytesBefore"] for s in samples) / 2**20
        working = statistics.median(s["WorkingSetAfterFirstRun"] - s["WorkingSetBefore"] for s in samples) / 2**20
        out.append(f"| {backend} | {span('CreationMs')} | {span('FirstInferenceMs')} | {private:.2f} | {working:.2f} |")
    out += ["", "DirectML's first session in this run took substantially longer than the next two. Driver/compiler caching and other desktop activity were not controlled. Native inference's zero managed allocations do not establish lower process RAM: it also retains a full-resolution BGRA source buffer.", "",
            "## Clipboard startup and file footprint", "",
            "These are first-pass measurements only. The first encrypted write includes first-use key setup; subsequent writes use the same store context. Check-pass writes reuse the scratch directory and are excluded from this startup comparison.", ""]
    if any(d.get("IncludesDpapiKeyCreation") for d in runs.get("verification", {}).get("Details", [])):
        out += ["The check pass's old `IncludesDpapiKeyCreation` flag was incorrect for its reused directory. The runner now checks key existence; the first-pass startup observations below are unaffected.", ""]
    for detail in primary["Details"]:
        if detail["Kind"] == "FirstEncryptedWrite":
            out.append(f"- First encrypted {detail['PayloadBytes']:,}-byte document: {detail['Milliseconds']:.3f} ms; new DPAPI key: {detail['IncludesDpapiKeyCreation']}.")
        if detail["Kind"] == "StorageFootprint":
            out.append(f"- After the write sequence for {detail['PayloadBytes']:,}-byte payloads: plaintext {detail['PlainFileBytes']:,} bytes, encrypted log {detail['EncryptedLogBytes']:,} bytes.")
    out += ["", "File sizes are logical lengths after warmup and repeated updates, including retained obsolete versions and page alignment. They are not physical disk allocation or a single-record size; compaction makes them depend on the write sequence.", "",
            "## Validation and remaining limits", ""]
    fft = max(d["MaxAbsoluteError"] for d in primary["Details"] if d["Kind"] == "FFTValidation")
    oklab = max(d["MaxAbsoluteError"] for d in primary["Details"] if d["Kind"] == "OklabValidation")
    out += [f"- FFT baseline/current maximum absolute difference: {fft:.3g}; Oklab scalar/SIMD: {oklab:.3g}. The checked bounds were 1e-6 and 1e-5 respectively.",
            "- Scrubber output equality, nonempty synthetic YOLOX decode/NMS equality and encrypted/plaintext document round trips passed. Complete HSV/Oklab palette output equality is not expected.",
            "- Microkernels: 41 rotated/interleaved samples, calibrated batches targeting 8 ms; large logs: 15 single-operation samples; durable writes: 81 single-operation samples. JSON contains actual sample/batch counts and all raw observations. p95 is the nearest-rank statistic of measured batch averages, not a production tail-latency estimate.",
            "- The benchmark harness builds with zero warnings/errors. This benchmark does not rerun or replace the implementation's earlier 931-test regression selection.",
            "- Privacy/media idle CPU and event-notification latency were not measured. No production app was restarted and no actual clipboard history was read or migrated.",
            "- No GGUF model was present in the configured local translation-model directory. Translation token throughput, Q8/F16 KV-cache RAM/VRAM and quality were not measured. NPU providers were not tested.",
            "- The proposed 1.2 ms inference, 60% FFT cycle reduction, fourfold complete-palette speedup and 35% translation-memory reduction are not established by these measurements.", "",
            "The next performance work should investigate complete-palette overhead, repeated file opens/mappings and large-document serialization allocations; repeat the sparse-log case under a quiet, controlled load before changing its algorithm based on timing alone.", "",
            "## Artifacts and reproduction", "",
            f"Artifact directory: `{root.as_posix()}`. Machine-specific artifacts are ignored by Git; keep this directory when sharing raw evidence.", "",
            "- `results.json`, optional `verification-results.json` and `inference512-results.json`: statistics, raw samples and validation details.",
            "- `measurements.csv`, `raw-samples.csv`: summary and per-sample exports, with explicit run labels.",
            "- `cold-*.json`, `environment.json`: fresh-process observations, hardware, versions and binary/model/source SHA-256 hashes.",
            "- `baseline/Native`, `current/Native`, `native/`, `runtime/` and `build-*.log`: frozen inputs and build evidence.", "",
            "```powershell",
            f"./scripts/Invoke-NativePipelineBenchmark.ps1 -BaselineCommit {env['BaselineCommit']}",
            "# Re-render an existing artifact directory without rerunning the benchmarks:",
            "python ./Tools/NativePipelineBench/report.py ./artifacts/native-benchmark-20261102",
            "```", "",
            "A new invocation snapshots the then-current working tree. Use the saved native snapshots to reproduce the exact current binary input from this report. See `Tools/NativePipelineBench/README.md` for prerequisites and selective verification commands.", ""]
    (root / "REPORT.md").write_text("\n".join(out), encoding="utf-8")
    evidence = [root / "environment.json", root / "REPORT.md", root / "measurements.csv", root / "raw-samples.csv"]
    for pattern in ("*results.json", "cold-*.json", "native/*.dll", "runtime/*.dll", "baseline/Native/**/*.cpp", "baseline/Native/**/*.h", "current/Native/**/*.cpp", "current/Native/**/*.h"):
        evidence.extend(root.glob(pattern))
    manifest = [f"{hashlib.sha256(file.read_bytes()).hexdigest()}  {file.relative_to(root).as_posix()}" for file in sorted(set(evidence))]
    (root / "sha256.txt").write_text("\n".join(manifest) + "\n", encoding="utf-8")
    print(f"Wrote {root / 'REPORT.md'}, measurements.csv and raw-samples.csv")


if __name__ == "__main__":
    main()
