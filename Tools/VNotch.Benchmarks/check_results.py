"""Fail closed on missing BDN results, timing regressions, or allocation growth."""
import json
import math
from pathlib import Path
import sys


def check(directory):
    expected = {
        "BlurBenchmarks": {f"Size={size}&Radius={radius}" for size in (128, 256) for radius in (8, 20)},
        "CityBenchmarks": {""},
        "ColorBenchmarks": {"Size=64", "Size=256"},
        "ColorKernelBenchmarks": {""},
        "UrlBenchmarks": {""},
        "WeatherJsonBenchmarks": {"PayloadBytes=1024", "PayloadBytes=65536"},
    }
    failures = []
    comparisons = 0
    for suite, parameters in expected.items():
        path = directory / f"VNotch.Benchmarks.{suite}-report-full-compressed.json"
        if not path.is_file():
            failures.append(f"Missing report: {path.name}")
            continue
        report = json.loads(path.read_text(encoding="utf-8-sig"))
        rows = report.get("Benchmarks", [])
        keys = [(row["Method"], row.get("Parameters", "")) for row in rows]
        if len(set(keys)) != len(keys):
            failures.append(f"{suite}: duplicate results; use one job per report")
            continue
        indexed = dict(zip(keys, rows))
        pairs = [("StringBaseline", "StringOptimized"), ("UriBaseline", "UriOptimized")] if suite == "UrlBenchmarks" else [("Baseline", "Optimized")]
        for parameter in sorted(parameters):
            for baseline_name, optimized_name in pairs:
                label = f"{suite} {parameter} {optimized_name}".strip()
                baseline = indexed.get((baseline_name, parameter))
                optimized = indexed.get((optimized_name, parameter))
                if baseline is None or optimized is None:
                    failures.append(f"{label}: missing baseline/optimized result")
                    continue
                means = [(row.get("Statistics") or {}).get("Mean") for row in (baseline, optimized)]
                allocated = [(row.get("Memory") or {}).get("BytesAllocatedPerOperation") for row in (baseline, optimized)]
                if any(value is None or not math.isfinite(value) or value <= 0 for value in means):
                    failures.append(f"{label}: invalid/missing timing statistics")
                    continue
                if any(value is None or not math.isfinite(value) or value < 0 for value in allocated):
                    failures.append(f"{label}: invalid/missing memory statistics")
                    continue
                comparisons += 1
                ratio = means[1] / means[0]
                print(f"{label}: {ratio:.3f}x time; {allocated[0]:g} -> {allocated[1]:g} B/op")
                if ratio > 1.10:
                    failures.append(f"{label}: mean time regressed by more than 10%")
                if allocated[1] > allocated[0]:
                    failures.append(f"{label}: managed allocation increased")
                # BDN can amortize harness allocations to 1 B/op for slow blur
                # cases. Exact zero-allocation assertions also run in xUnit.
                if suite == "BlurBenchmarks" and allocated[1] > 1:
                    failures.append(f"{label}: blur allocation exceeds harness tolerance")
                if suite == "ColorKernelBenchmarks" and allocated[1] != 0:
                    failures.append(f"{label}: color kernel allocated")
    for failure in failures:
        print(f"FAIL: {failure}", file=sys.stderr)
    print(f"{comparisons} comparisons; {len(failures)} failures")
    return 1 if failures else 0


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: python check_results.py <BenchmarkDotNet results directory>")
    raise SystemExit(check(Path(sys.argv[1])))
