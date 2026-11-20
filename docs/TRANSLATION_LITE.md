# Lightweight offline translation

SMaLL-100 INT8 is the default for new settings. Existing model selections remain unchanged.
Select **SMaLL-100 INT8** in Settings → Translation and download or import its model files once.
Translation then runs entirely on the CPU, without accounts, API keys, IP quotas or network requests.

The catalog pins six assets from `solavr/small100-ctranslate2-int8` at revision
`3c5af8e17d5c4f35bda849b7ffceaa1dfc62eb84`, with exact sizes and SHA-256 checks.
The weights are 338.7 MB; the complete model download is approximately 344.1 MB (decimal).
The source model is [SMaLL-100](https://huggingface.co/alirezamsh/small100).
The [INT8 conversion](https://huggingface.co/solavr/small100-ctranslate2-int8) includes its license and notice.

## Coverage and behavior

- The app exposes exactly the 100 language tokens in the pinned model vocabulary, including Asturian.
  Filipino and Northern Sotho use the model's `tl` and `ns` tokens; do not substitute GGUF prompt aliases.
- SMaLL-100 uses a target-language token on the source. Input language is handled by the model;
  no English pivot or separate English–Vietnamese model is involved.
- C++ handles SentencePiece tokenization, bounded chunks, CPU INT8 inference and cancellation.
  Long selections split at sentence endings when available, then clause/word boundaries, within the
  same 192-token budget. This avoids joining translations of two halves of an ordinary sentence.
  Decimal numbers, common abbreviations and closing quotes are handled conservatively; line endings,
  indentation, repeated lines and every input token are preserved. Chinese/Japanese output does not
  gain artificial spaces between chunks.
- **Humanizer on:** four-beam decoding compares several candidate translations, one unique chunk at
  a time. **Humanizer off:** greedy decoding batches up to four unique chunks for lower latency.
  Both use the same INT8 weights and at most four concurrent decoder hypotheses; no additional model,
  dictionary substitutions, rewriting pass, download or network request is used. The existing cache
  is invalidated when this option changes.
- Cancellation stops waiting for beam search promptly. CTranslate2 cannot interrupt a beam-search
  step through its greedy token callback, so the single in-flight batch finishes in the background;
  another request waits for it rather than starting overlapping inference. Greedy mode retains token
  cancellation. No callback or caller-owned memory survives a cancelled native call.
- Weights are warmed in the background and stay resident until the configured model idle timeout.
  Zero keeps them loaded. The next uncached request reloads an idle model automatically.
  Disabling translation, switching models, replacement/removal and disposal also release weights.
- The managed facade reuses a pooled output buffer and clears it after each request, avoiding a
  new 128 KiB large-object allocation for every translation.
- Completed results use the existing bounded memory cache. Lightweight results skip the staged text reveal.
  Existing integrity checks still reject unsafe replacements; a nonempty output is not proof of accuracy.
- Coverage does not mean every language in the world, and quality is uneven, especially for low-resource
  languages. Beam search is not a human-level rewrite: the pinned model still mistranslates some
  idioms, slang and polite expressions. Longer passages are slower. Larger existing models remain
  available for better quality, but the lightweight model remains selected unless the user changes it.

## Reproducible runtime build

A normal Windows build runs `scripts/Build-TranslationRuntime.ps1`. It fetches pinned, SHA-256-verified
CTranslate2 4.9.0 headers and its official Windows wheel, and SentencePiece 0.2.3 source. CMake also
checks the Abseil, Protobuf, protoc and FlatBuffers archive hashes. It links only
the native CTranslate2 DLL (Python is neither required nor shipped) and statically links SentencePiece.
Visual Studio 2022 C++ x64 tools and CMake are required. The first build needs network access; subsequent
builds reuse `.cache/opus-runtime` and an input signature. `-p:SkipTranslationRuntime=true` skips rebuilding.

The runtime is a separate `translation/` folder containing `VNotch.Translation.dll`, CTranslate2,
OpenMP, the MSVC runtime and third-party license files. MSBuild includes it in both build and publish
outputs, including single-file deployments. Models are never included in the app package.
`LiteTranslationRuntime.cpp` is excluded from the main native compile; its small C ABI loader remains
inside `VNotch.Native.dll` and resolves the runtime from the adjacent folder.

## Validation

Run native inference and cancellation/bounds/formatting checks against locally available model assets:

```powershell
pwsh -NoProfile -File Tools/TranslationRuntime/Test-Runtime.ps1 -ModelDirectory <model-folder> -AllLanguages -CheckIdle
```

Use `-Facade -RuntimeDll <path-to-VNotch.Native.dll>` without `-CheckIdle` to check the main DLL loader.
Add `-Fast` to exercise greedy mode. `Compare-Fluency.ps1` records translations and latency for a
small multilingual conversation/idiom set; it supports the same `-Fast` switch. Its output needs
human review and is not an automatic quality score. The native `VNotchTranslationChunkTests` CMake
target checks sentence/quote/decimal/abbreviation boundaries and lossless token coverage without a model.
The 100-language sweep checks nonempty output, not linguistic quality.

The managed integration test imports and verifies all six real assets, compares the app's language
list to the model vocabulary, translates across multiple scripts, checks cache hits and releases files:

```powershell
$env:VNOTCH_LITE_MODEL_PATH = '<model-folder>'
dotnet test Tests/VNotch.Tests.csproj --filter FullyQualifiedName~LiteTranslationTests
```

Without that environment variable, the real-model test is explicitly skipped; other tests do not
download weights. The updated managed-service probe measured 162–250 ms on five sample language
pairs with Humanizer enabled; subsequent cache lookups took 0.036–0.070 ms. These are local
observations, not end-to-end UI latency guarantees or a quality benchmark.

The updated regression run passed all 59 engine, startup, cache and popup tests, including real-model
integration and changing Humanizer without reloading weights. The native chunk tests passed 3,464
checks. Locale validation previously passed for all 17 UI languages. Build/publish
item validation confirmed all six runtime DLLs and both license files retain their relative paths.

## Optimization measurements (2.4.1)

The repeatable comparison script records median latency, CPU time, working set and the full output:

```powershell
pwsh -NoProfile -File Tools/TranslationRuntime/Measure-Runtime.ps1 -ModelDirectory <model-folder> -RuntimeDll <runtime-dll> -OutputJson <results.json> -Repetitions 3
```

Pass `-BaselineJson <earlier-results.json>` for per-case comparisons and `-Fast` for Humanizer off.
Run versions sequentially with no concurrent builds or inference. On a Ryzen 7 5800H with four
inference threads, three-run medians with Humanizer enabled were 176–231 ms for the short samples,
8.07–9.51 s for the two long samples (about 1,900 characters), 1.08 s for eight distinct lines and
150 ms for eight repeated lines. Compared with the previous greedy configuration, this is slower:
the extra candidate search trades latency for possible wording improvements, not guaranteed quality.

With Humanizer enabled, the native probe added approximately 369 MiB of working set with a warmed
model and peaked at 411 MiB above its baseline (previous greedy configuration: 368/405 MiB).
After unloading, the residual increase was approximately 16 MiB (runtime DLLs/allocator overhead).
Idle CPU was below 0.01% of total machine capacity. This measures the translation runtime in a
PowerShell host, not the complete WPF application's RAM. All timings and RAM values are sample-specific.

In the reviewed fixtures, `Save your changes.` now produced `Lưu lại những thay đổi.` instead of
remaining in English, and a Japanese polite request no longer became an English sentence about a
discount. However, English idioms such as `running out of time` still translated literally. Retaining
the small model means this limitation remains; do not present the Humanizer switch as a cure for it.
