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
    internal string Category => PromptKind == TranslationPromptKind.General ? "general" : "specialist";
    internal static readonly TranslationModelProfile QwenInstruct = new("qwen3-4b-instruct-2507-q4km-v1", "unsloth/Qwen3-4B-Instruct-2507-GGUF",
        "a06e946bb6b655725eafa393f4a9745d460374c9",
        [new("Qwen3-4B-Instruct-2507-Q4_K_M.gguf", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", 2497281120, "3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597")], 0, 0, false, 0);
}
