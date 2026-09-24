# Report benchmark

Run from the repository root:

```powershell
dotnet run --project Tools/ReportBenchmark/ReportBenchmark.csproj -c Release -- artifacts/performance-report/recheck.json
```

This standalone STA console runner invokes production methods; it does not run xUnit or launch the application UI. Seven rounds follow warmup. Results record median/min/max per-operation mean time and current-thread managed allocations. The desktop scan forces cache expiry by reflection; its timings depend on open windows.

Baseline color/blur sources are pinned to commit 907cc3bf1053b848225cd057bbcd03b1c99d53f3 with only namespace/import changes. `Measurements/before.json` captures production code before this change; `Measurements/final.json` captures the delivered version. See the repository PERFORMANCE_REPORT.md for methodology, limitations, and changes not adopted.
