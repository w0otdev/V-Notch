# Translation cache latency

Completed translations can be read while another request is loading or using the model. The popup checks the cache before starting or awaiting model preparation. Cache misses still share one inference gate; cancellation and request versions prevent an older request from replacing a newer result.

Changing CPU threads, GPU layers, prompt batch size, timeout or model idle retention preserves completed results. Changes to sampling, humanizer, context/output limits or cache retention invalidate them. Cache entries remain specific to the prepared source text and both language codes, expire from creation time, and follow the configured LRU capacity.

Translations with changed numeric literals or named weekdays are available for review but are never cached. Cache disposal clears text immediately, including when inference is still running.

## Reproduce the pipeline benchmark

```powershell
dotnet build Tools/TranslationBench/TranslationBench.csproj --artifacts-path .artifacts/translation-cache-bench -p:RestoreLockedMode=true
dotnet .artifacts/translation-cache-bench/bin/TranslationBench/debug_win-x64/VNotch.TranslationBench.dll --cache-benchmark
```

This uses synthetic text and an echo engine. It loads no model and makes no network requests. It measures 1,000 cached requests across ten source-language codes, a cache hit during an artificial 500 ms inference, and six changes to runtime tuning. It does not measure model throughput or linguistic accuracy.

Measured on October 15, 2026 using .NET 8.0.29, with the same benchmark executable and the application assembly from before/after the cache change:

| Measurement | Before | After |
| --- | ---: | ---: |
| Cached result while inference is busy | 509.4462 ms | 0.2969 ms |
| Cache returned before inference finished | No | Yes |
| Idle cache-hit median | 0.0035 ms | 0.0035 ms |
| Idle cache-hit p95 | 0.0068 ms | 0.0064 ms |
| Model calls for 1,000 cache hits | 0 | 0 |
| Model calls after six runtime tuning changes | 6 | 0 |

These are single-run local samples. The artificial delay demonstrates removal of queue waiting; it is not a general translation speedup estimate. `TranslationCacheTests` checks concurrency, policy changes, cancellation, disposal, and English/Chinese/Japanese weekday changes. `CachedResultReachesPopupWhileModelPreparationIsStillRunning` checks the actual controller and popup path on the private Windows test desktop.
