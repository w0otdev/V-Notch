"""Report for the palette/clipboard optimization comparison (stdlib only)."""
import csv
import hashlib
import json
import xml.etree.ElementTree as ET
from collections import defaultdict


def render(root):
    runs = {"primary": json.loads((root / "hotpath-results.json").read_text(encoding="utf-8-sig"))}
    if (root / "hotpath-repeat-results.json").exists():
        runs["repeat"] = json.loads((root / "hotpath-repeat-results.json").read_text(encoding="utf-8-sig"))
    data = runs["primary"]
    env = data["Environment"]
    groups = defaultdict(list)
    repeats = defaultdict(list)
    for m in data["Measurements"]:
        groups[m["Area"], m["Workload"]].append(m)
    for m in runs.get("repeat", {}).get("Measurements", []):
        repeats[m["Area"], m["Workload"]].append(m)
    fields = [k for k in data["Measurements"][0] if k != "RawUs"]
    with (root / "measurements.csv").open("w", encoding="utf-8", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=["Run", *fields])
        writer.writeheader()
        for run, results in runs.items():
            for m in results["Measurements"]:
                writer.writerow({"Run": run, **{k: m[k] for k in fields}})
    with (root / "raw-samples.csv").open("w", encoding="utf-8", newline="") as f:
        writer = csv.writer(f)
        writer.writerow(["Run", "Area", "Workload", "Variant", "Sample", "Batch", "MicrosecondsPerOperation"])
        for run, results in runs.items():
            for m in results["Measurements"]:
                for i, value in enumerate(m["RawUs"], 1):
                    writer.writerow([run, m["Area"], m["Workload"], m["Variant"], i, m["Batch"], value])
    previous = next(d for d in data["Details"] if d["Kind"] == "PreviousNative")
    text = [f"# Palette and encrypted clipboard optimization — {data['Timestamp'][:10]}", "",
            "The optimized Oklab palette preserves its color-selection algorithm and removes per-pixel angle calculations on AVX2. Small encrypted clipboard reads now avoid the size-query round trip, mapping overhead and managed plaintext arrays. Encryption, authentication and the existing file format are retained.", "",
            f"Machine: {env['Cpu'][0]['Name'].strip()}; {env['Os'][0]['Caption']} {env['Os'][0]['Version']}; {env['Computer'][0]['TotalPhysicalMemory'] / 2**30:.1f} GiB RAM. AVX2: {data['NativeAvx2']}.", "",
            f"HSV/plaintext baseline commit: `{env['BaselineCommit']}`. Previous Oklab/encrypted DLL SHA-256: `{previous['Sha256']}`. Current DLL SHA-256: `{env['CurrentDllSha256']}`. Both native snapshots use the same MSVC Release flags. The previous DLL is the frozen current DLL from the earlier pipeline benchmark.", "",
            "## Paired results", "",
            "Median microseconds per operation; lower is better. Speedup compares previous Oklab/encrypted implementation with the optimized implementation. Each variant has 61 rotated/interleaved samples with warmup and equal calibrated batches. Repeat results use the same frozen binaries. This is a shared desktop with variable clocks and background load; compare paired ratios rather than absolute times from separate runs.", "",
            "| Workload | Original HSV/plain JSON | Previous Oklab/encrypted | Optimized | Speedup | Repeat speedup |",
            "| --- | ---: | ---: | ---: | ---: | ---: |"]
    for key, values in groups.items():
        original, before, after = values[0], values[1], values[-1]
        repeat = repeats.get(key)
        ratio = f"{repeat[1]['MedianUs'] / repeat[-1]['MedianUs']:.2f}x" if repeat else "—"
        text.append(f"| {key[0]}: {key[1]} | {original['MedianUs']:,.3f} | {before['MedianUs']:,.3f} | {after['MedianUs']:,.3f} | {before['MedianUs'] / after['MedianUs']:.2f}x | {ratio} |")
    text += ["", "The original HSV palette chooses different colors; matching or beating its throughput does not imply output equality with HSV. The 36 px case represents the application's default palette sample size. Plain JSON has no record encryption/authentication; its column is a reference for the added security cost.", "",
             "## All variants and tails", "",
             "| Workload | Variant | Median, us | p95, us | Managed B/op |",
             "| --- | --- | ---: | ---: | ---: |"]
    for m in data["Measurements"]:
        text.append(f"| {m['Area']}: {m['Workload']} | {m['Variant']} | {m['MedianUs']:,.3f} | {m['P95Us']:,.3f} | {m['ManagedBytesPerOperation']:,.1f} |")
    text += ["", "The intermediate clipboard variant uses the old native DLL with the new stack/pool JSON reader. This separates the managed-call/allocation improvement from the native I/O change. Larger documents still require a capacity retry and remain slower than plain JSON. No claim is made that encryption or mapping is always faster than buffered plaintext I/O.", "",
             "## Implementation", "",
             "- Palette: AVX2 computes Oklab, chroma, radial weights and the same 36 angular sectors in blocks of eight. Sector membership uses tangent comparisons in each quadrant instead of `atan2`. Narrow rows and tails use a bounded padded copy; the 36 px case no longer runs four expensive scalar cube-root conversions per row. Unsupported CPUs retain the scalar implementation. No pixels are subsampled or colors quantized to a lower-resolution RGB grid.",
             "- Clipboard: a 4 KiB stack buffer attempts the authenticated read immediately; larger payloads use a wiped pooled buffer. JSON is deserialized before wiping, without returning an intermediate managed byte array to the history store. Native reads fetch the commit/header pages and small ciphertext records with buffered `ReadFile`; records above 256 KiB retain the mapping path. Header/commit bounds and AES-GCM authentication are unchanged. Plaintext remains in a native temporary until authentication succeeds, and temporary buffers are wiped.",
             "- Path normalization uses a stack buffer for typical paths and a bounded dynamic fallback for long paths. There is no plaintext cache and no persistent file/mapping cache that could hide subsequent changes or tampering.", "",
             "## Validation and evidence", ""]
    for detail in data["Details"]:
        if detail["Kind"] == "PaletteValidation":
            text.append(f"- {detail['Palettes']} previous/optimized palette comparisons were byte-for-byte identical across seeded images, transparency, padded rows, narrow widths and solid colors. This is a tested corpus, not an exhaustive floating-point equivalence proof; values at sector boundaries can differ by rounding.")
        if detail["Kind"] == "StorageValidation":
            text.append(f"- {detail['PayloadBytes']:,}-byte JSON document round trips matched across plaintext, previous encrypted, intermediate and optimized readers.")
    trx = root / "tests/hotpath-final.trx"
    if trx.exists():
        counters = ET.parse(trx).getroot().find(".//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters")
        text.append(f"- Release regression selection: **{counters.get('passed')} passed, {counters.get('failed')} failed** ({counters.get('total')} total). Results: `tests/hotpath-final.trx`.")
    text += ["- Regression coverage includes tampered ciphertext, truncated headers, key loss, recovery, migration, pooled/mapped payloads and palette contrast. The production application and its real clipboard history were not restarted or migrated.",
             "- All native/source SHA-256 hashes and build logs are retained. JSON contains actual sample/batch counts, raw observations and validation details; CSV exports separate primary and repeat runs. p95 is the nearest-rank statistic of batch averages, not a production tail guarantee. Managed allocation measurements cover the calling thread only.",
             f"- Artifact directory: `{root.as_posix()}`.", "",
             "```powershell",
             "pwsh -File ./scripts/Invoke-NativePipelineBenchmark.ps1 `",
             f"  -BaselineCommit {env['BaselineCommit']} `",
             "  -OutputPath ./artifacts/native-hotpath-new -HotPathsOnly -Verify `",
             "  -PreviousNativeDll ./artifacts/native-benchmark-20261102/native/current.dll",
             "```", "",
             "The earlier full pipeline report remains a historical record. Its description incorrectly said the size query also authenticated the record; that text has been corrected. The size query opened/mapped the file but did not decrypt. Its recorded timings are unchanged.", ""]
    (root / "REPORT.md").write_text("\n".join(text), encoding="utf-8")
    evidence = [root / name for name in ("REPORT.md", "environment.json", "measurements.csv", "raw-samples.csv")]
    for pattern in ("hotpath*results.json", "native/*.dll", "current/Native/**/*.cpp", "current/Native/**/*.h"):
        evidence.extend(root.glob(pattern))
    (root / "sha256.txt").write_text("\n".join(
        f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.relative_to(root).as_posix()}" for path in sorted(set(evidence))) + "\n", encoding="utf-8")
    print(f"Wrote {root / 'REPORT.md'}, measurements.csv and raw-samples.csv")
