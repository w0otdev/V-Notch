namespace VNotch.Services.Translation;

internal sealed record TranslationModelProfile(string Id, string Repository, string Revision,
    TranslationModelStore.Asset[] Assets, int Layers = 0, int HeadSize = 0, bool M2M = false, int Beams = 0)
{
    internal string DisplayName { get; init; } = "Qwen3 4B Instruct";
    internal TranslationPromptKind PromptKind { get; init; } = TranslationPromptKind.General;
    internal int MinimumRamGiB { get; init; } = 8;
    internal int GpuGiB { get; init; } = 4;
    internal string Tier { get; init; } = "light";
    internal string License { get; init; } = "Apache 2.0";
    internal string Category => !IsLite && PromptKind == TranslationPromptKind.General ? "general" : "specialist";
    // Editorial translation capability estimate, not an IQ or measured benchmark.
    // See docs/TRANSLATION_MODEL_RATINGS.md for the scale and rationale.
    internal int TranslationRating { get; init; }
    internal bool IsCustom { get; init; }
    internal bool IsLite { get; init; }
    internal static readonly TranslationModelProfile Small100 = new("small100-int8-v1", "solavr/small100-ctranslate2-int8",
        "3c5af8e17d5c4f35bda849b7ffceaa1dfc62eb84",
        [
            new("model.bin", "model.bin", 338727246, "24e8b51720b7267cfd8faacedce50128bf11c4327f039632a494deb5e1c421f6"),
            new("config.json", "config.json", 233, "72901fbd8abd89fb5cf4a388f26fc681f5c4c58a1e1a88b30b879f107270e7ee"),
            new("shared_vocabulary.json", "shared_vocabulary.json", 2924622, "18916ac68f9ac8fbd5250ca603dcb3861aac8672924d93a8dadc82eb3147f941"),
            new("sentencepiece.bpe.model", "sentencepiece.bpe.model", 2423393, "d8f7c76ed2a5e0822be39f0a4f95a55eb19c78f4593ce609e2edbc2aea4d380a"),
            new("LICENSE", "LICENSE", 1078, "84a84b62f07e8905c054b5c2f319a5a806a3c7441b6186944d9c5ab433a934b1"),
            new("NOTICE", "NOTICE", 663, "6377aadb590c5e0f8c0bb32f92f8aa6ca4c8b86e7aced0a86bd85ed0fb8193c7"),
        ]) { DisplayName = "SMaLL-100 INT8", IsLite = true, MinimumRamGiB = 4, GpuGiB = 0, License = "MIT", TranslationRating = 2 };
    internal static readonly TranslationModelProfile QwenInstruct = new("qwen3-4b-instruct-2507-q4km-v1", "unsloth/Qwen3-4B-Instruct-2507-GGUF",
        "a06e946bb6b655725eafa393f4a9745d460374c9",
        [new("Qwen3-4B-Instruct-2507-Q4_K_M.gguf", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", 2497281120, "3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597")], 0, 0, false, 0)
    { TranslationRating = 3 };
}
