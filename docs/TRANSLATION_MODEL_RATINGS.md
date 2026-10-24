# Translation capability estimates

The model picker uses an editorial 1–5 estimate for quick comparison. These are
not IQ scores, measured benchmarks, percentages, or publisher-assigned scores.
They do not affect hardware recommendations or inference settings.

Scale: 1 = basic, 2 = limited everyday translation, 3 = everyday translation,
4 = strong translation potential, 5 = highest expected capability in this catalog.
No current model is assigned 1 or 2. Ties are intentional; 5 does not mean perfect.

Initial editorial assignments (2026-10-11):

| Model | Estimate | Editorial rationale |
| --- | --- | --- |
| Qwen3 4B Instruct 2507 | 3 | Compact general multilingual baseline. |
| TranslateGemma 4B | 4 | Translation specialization. |
| Hunyuan-MT 7B | 4 | Translation specialization; language coverage differs. |
| TranslateGemma 12B | 5 | Larger specialist in the same translation family. |
| Qwen2.5 14B Instruct | 4 | Larger general multilingual model. |
| Qwen3 14B | 4 | Larger general multilingual model, in the same broad tier. |
| Qwen3.5 27B | 5 | Upper-tier general capability estimate. |
| Gemma 4 26B A4B | 4 | General mixture-of-experts model; total parameters alone do not determine quality. |
| Gemma 4 31B | 5 | Upper-tier dense general capability estimate. |

These are product judgments informed by model family, intended task and published
descriptions, not a normalization of incompatible evaluations. The pinned quantized
builds have not been evaluated together on a common translation benchmark.
Language pair, domain, prompting and quantization can change the ranking.

Publisher references:

- https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507
- https://huggingface.co/google/translategemma-4b-it
- https://huggingface.co/tencent/Hunyuan-MT-7B
- https://huggingface.co/Qwen/Qwen2.5-14B-Instruct
- https://huggingface.co/Qwen/Qwen3-14B
- https://huggingface.co/Qwen/Qwen3.5-27B
- https://huggingface.co/google/gemma-4-26B-A4B-it
- https://huggingface.co/google/gemma-4-31B-it

Update each profile's explicit `TranslationRating` when reviewing these estimates.
Do not derive the rating from RAM, VRAM, file size or hardware tier.
