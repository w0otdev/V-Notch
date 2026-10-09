# Translation development checks

This Windows-only console runner references the production translation service and includes earlier ONNX candidates for comparison. `Research/` and its Tokenizers.DotNet dependencies are excluded from the production application. Do not redistribute research model folders as app payload.

Build into a separate directory when V-Notch is running:

```powershell
dotnet build Tools/TranslationBench/TranslationBench.csproj -p:OutputPath=D:/CodeShii/V-Notch/.artifacts/translation-bench/
dotnet .artifacts/translation-bench/VNotch.TranslationBench.dll .artifacts/translation-benchmark/qwen-instruct --instruct --gpu
```

The folder must contain the pinned GGUF and `verified.txt` marker. Add `--download` **explicitly** to download into the development folder; this never installs a model into the user's app. Without `--gpu`, the runner uses CPU. Raw translations and timing are written as `benchmark.json` beside the model folder. `Results/` keeps measured samples, including failures. `LiteralsPreserved` checks observable numeric/code/link literals; `WeekdaysPreserved` checks named weekdays where supported. Neither is a semantic quality score.

```powershell
dotnet .artifacts/translation-bench/VNotch.TranslationBench.dll --preview
dotnet .artifacts/translation-bench/VNotch.TranslationBench.dll --uia-smoke
dotnet .artifacts/translation-bench/VNotch.TranslationBench.dll .artifacts/translation-benchmark/qwen-instruct --instruct --gpu --engine-smoke
```

Preview renders a human-authored UI fixture; it does not establish model quality. UIA smoke temporarily focuses a separate WPF text fixture, captures a selection, replaces only that text, rejects a stale request, closes its own host and restores the previous foreground window. Run while not typing into other apps. No user text is modified.

Engine smoke checks cancellation and subsequent inference using the real local model. Publish this runner with `-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --no-self-contained` to exercise the native DLL sidecar layout. Run one model benchmark at a time; concurrent models can exhaust VRAM and distort timings.

The earlier M2M tokenizer and MADLAD tokenizer parity fixtures are retained for reproduction. MADLAD's unsigned affine-weight conversion was a research workaround for CPU saturation and did not make that model suitable for this feature. Its profile hashes identify the prepared artifacts rather than a distributable upstream download; use local prepared research files.
