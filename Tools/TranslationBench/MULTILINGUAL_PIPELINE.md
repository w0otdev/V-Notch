# Shared multilingual translation behavior

Production improvements belong in the shared pipeline so every source/target pair supported by the selected model receives them:

- `TranslationMeaningIntegrity.Instruction` supplies the same meaning, compound-expression, negation, uncertainty, sentence-type, mixed-language and factual-preservation rules to general models and specialist models. The shared wording is compact to limit prompt processing. Source text is treated as content to translate, including when it contains instructions.
- `TranslationLanguages` uses canonical Unicode composition for both language hints and the conservative same-language shortcut. Original input and protected content are not normalized in place.
- `TranslationTextIntegrity` recognizes Unicode currency symbols through `\p{Sc}` and decimal digits through `\d`. Quantity comparisons accept equivalent decimal digits across writing systems and Arabic decimal/grouping separators. URLs, email addresses and identifiers retain exact comparisons. Displayed source/output text, signs and currency symbols remain unchanged.
- `LocalTranslationService` shares caching, cancellation, expiry and result-retention behavior across every language pair. Cache keys include both language codes. Runtime tuning keeps completed translations; output-affecting changes invalidate them. Cached results do not wait for model inference or preparation.

Language-specific terminology hints supplement these shared rules. They are not the basis of multilingual support. Deterministic sentence and weekday guards cover only the language patterns explicitly implemented; shared prompts and literal checks do not establish semantic accuracy in every language. Model capability still limits the supported pairs and translation quality.

The current changes were reviewed statically and compiled. Tests and benchmarks were not run after the user's instruction to stop them; the earlier cache timing report predates these multilingual changes.

## Resource use in the shared pipeline

The structured-text layer reuses the source Markdown tree for both prose detection and protected-content extraction. It parses again if social-icon normalization changes the text, so offsets and Markdown interpretation remain tied to the correct input. The tree stays inside a synchronous helper; only the compact signature is retained during inference.

Emoji scanning uses spans over complete Unicode text elements. Ordinary characters do not create temporary strings, and emoji sequences retain the same rune ranges and grapheme boundaries. Protected signatures sort their existing list in place and reuse the shortcode regex.

The inference engine dispatches option application to a worker only when options have actually changed. Changes that can unload model weights still execute away from the UI thread. Specialist models share an immutable stop-sequence collection, and suffix detection compares spans instead of allocating every candidate prefix.

These changes apply across supported language pairs without reducing context/output limits or changing weights, quantization, sampling, thread settings or integrity rules. Compilation checks source/API compatibility; runtime resource savings and translation behavior have not been measured after the stop-tests instruction.

Literal validation now uses one `Inspect` operation to obtain both exact-literal preservation and the protected-facts fallback used for reviewing numeric differences. Each source/result text is normalized once and its facts are scanned once for both decisions. Exact links, addresses and identifiers share their original strings between comparisons; texts with no matching facts allocate no fact lists. Existing `Verify` and `CanPresentUnverifiedNumbers` entry points retain their respective predicates.

Placeholder checking bypasses regex work when the reserved prefix is absent. Hostname-suffix and numeric-grouping patterns use cached generated regexes with the original patterns and comparison rules. The service also reuses one source-language hint for its sentence-type and weekday checks, while keeping the user's selected language codes in results and cache keys.

Idle cleanup runs on a worker and coalesces timer/settings triggers. Disposed services skip cleanup. Native weight disposal is always dispatched to a worker, including when its semaphore is immediately available; a successor model still waits for the full previous-disposal chain, preserving serialized model residency.

The controller reads completed cache entries before attempting language detection. On a miss, strong same-language detection runs on one shared background CPU lane. Queued cancelled requests do not start profile initialization or full-text classification, and cancellation is checked between sentence checks. Single-paragraph text is not classified twice as both a passage and its only sentence. The original 40/12-letter thresholds, 0.995 confidence threshold and mixed-language sentence checks remain in place.

When the controller has already checked the exact prepared text and language pair, it uses `TranslateAfterLanguageCheckAsync` to avoid repeating that work in the service. Direct service callers still receive the same-language check through the normal `TranslateAsync` entry point. Request cancellation and version checks continue to suppress obsolete results.

## Request-local token counts

The GGUF engine returns fitted text together with its measured token count and tokenizer identity. The service carries that value directly into inference rather than tokenizing the same input again. It is local to the current request; no extra text history or cross-request token cache is kept.

The count is reused only for the exact fitted text and the same loaded weights instance. A reload, model replacement or GPU-to-CPU fallback changes that identity and triggers a fresh count. Raw `TranslateAsync` callers are still counted normally, and other engine implementations retain their original fit/translate behavior through default interface methods.

Token fitting retains the existing grapheme boundaries, binary-search decisions and 512-token limit. It carries the measured count of the selected prefix, recounts if sanitation changes that prefix, and observes cancellation before each native tokenization. Context/output budgets and post-translation token checks are unchanged. These edits compiled successfully without tests, benchmarks or model inference.
