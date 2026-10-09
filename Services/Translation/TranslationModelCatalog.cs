namespace VNotch.Services.Translation;

internal enum TranslationPromptKind { General, TranslateGemma, Hunyuan }
internal static class TranslationModelCatalog
{
    internal static readonly TranslationModelProfile[] All =
    [
        TranslationModelProfile.QwenInstruct,
        new("translategemma-4b", "bullerwins/translategemma-4b-it-GGUF", "7c938465a870d8624bcfa98a8e4a3510053c19a8",
        [
            new("translategemma-4b-it-Q4_K_M.gguf", "translategemma-4b-it-Q4_K_M.gguf", 2489909312L, "7f7357c14abd9da4eb200b38b05da502cd6e10d7e1d403fbc9f78c19f3209b72"),
        ]) { DisplayName = "TranslateGemma 4B", PromptKind = TranslationPromptKind.TranslateGemma, MinimumRamGiB = 8, GpuGiB = 4, Tier = "light", License = "Gemma" },
        new("hunyuan-mt-7b", "mradermacher/Hunyuan-MT-7B-GGUF", "6d6882aea2529efcfc898e54091ffa912744a3df",
        [
            new("Hunyuan-MT-7B.Q4_K_M.gguf", "Hunyuan-MT-7B.Q4_K_M.gguf", 4624950272L, "08b4dd8f25002592526194defd2481febcc9008fe37e67accde9bbe29d28cecf"),
        ]) { DisplayName = "Hunyuan-MT 7B", PromptKind = TranslationPromptKind.Hunyuan, MinimumRamGiB = 16, GpuGiB = 8, Tier = "balanced", License = "Tencent Hunyuan" },
        new("translategemma-12b", "bullerwins/translategemma-12b-it-GGUF", "d7d1d8cc4ff53d4bc883ef33eae3894f07833b63",
        [
            new("translategemma-12b-it-Q4_K_M.gguf", "translategemma-12b-it-Q4_K_M.gguf", 7300793664L, "9196d728812afbf5efc10b539298585725edc3a4ecc092c22fdde5bbaf41879e"),
        ]) { DisplayName = "TranslateGemma 12B", PromptKind = TranslationPromptKind.TranslateGemma, MinimumRamGiB = 24, GpuGiB = 12, Tier = "powerful", License = "Gemma" },
        new("qwen25-14b", "Qwen/Qwen2.5-14B-Instruct-GGUF", "b466e1f8c07172155743e8e1307507d8a4f91fbd",
        [
            new("qwen2.5-14b-instruct-q4_k_m-00001-of-00003.gguf", "qwen2.5-14b-instruct-q4_k_m-00001-of-00003.gguf", 3991999872L, "a09ea5e7b1eafb1b30b241726c3cc3c905c96f14ad41e246ffa5f44e53904f68"),
            new("qwen2.5-14b-instruct-q4_k_m-00002-of-00003.gguf", "qwen2.5-14b-instruct-q4_k_m-00002-of-00003.gguf", 3989373504L, "21b9457d079680d284e90ef69607c4b2d8ef64a09d4729cb7b5e1357bdba41ae"),
            new("qwen2.5-14b-instruct-q4_k_m-00003-of-00003.gguf", "qwen2.5-14b-instruct-q4_k_m-00003-of-00003.gguf", 1006737120L, "c8d37006760a387a35216e070e6664d7da927f10be8eb870fef2e3d4833d9976"),
        ]) { DisplayName = "Qwen2.5 14B Instruct", PromptKind = TranslationPromptKind.General, MinimumRamGiB = 24, GpuGiB = 12, Tier = "powerful", License = "Apache 2.0" },
        new("qwen3-14b", "Qwen/Qwen3-14B-GGUF", "530227a7d994db8eca5ab5ced2fb692b614357fd",
        [
            new("Qwen3-14B-Q4_K_M.gguf", "Qwen3-14B-Q4_K_M.gguf", 9001752960L, "500a8806e85ee9c83f3ae08420295592451379b4f8cf2d0f41c15dffeb6b81f0"),
        ]) { DisplayName = "Qwen3 14B", PromptKind = TranslationPromptKind.General, MinimumRamGiB = 24, GpuGiB = 12, Tier = "powerful", License = "Apache 2.0" },
        new("qwen35-27b", "unsloth/Qwen3.5-27B-GGUF", "3221f178a6b842d04f1fb42f1c413534adcc0a6a",
        [
            new("Qwen3.5-27B-Q4_K_M.gguf", "Qwen3.5-27B-Q4_K_M.gguf", 16740812704L, "84b5f7f112156d63836a01a69dc3f11a6ba63b10a23b8ca7a7efaf52d5a2d806"),
        ]) { DisplayName = "Qwen3.5 27B", PromptKind = TranslationPromptKind.General, MinimumRamGiB = 32, GpuGiB = 24, Tier = "workstation", License = "Apache 2.0" },
        new("gemma4-26b", "google/gemma-4-26B-A4B-it-qat-q4_0-gguf", "d1c082be9cf3c8a514acf63b8761f4b41935842e",
        [
            new("gemma-4-26B_q4_0-it.gguf", "gemma-4-26B_q4_0-it.gguf", 14439363584L, "3eca3b8f6d7baf218a7dd6bba5fb59a56ee25fe2d567b6f5f589b4f697eca51d"),
        ]) { DisplayName = "Gemma 4 26B A4B", PromptKind = TranslationPromptKind.General, MinimumRamGiB = 32, GpuGiB = 20, Tier = "workstation", License = "Apache 2.0" },
        new("gemma4-31b", "google/gemma-4-31B-it-qat-q4_0-gguf", "59dde24573e7e61570dba08b18a2e1fe246955ed",
        [
            new("gemma-4-31B_q4_0-it.gguf", "gemma-4-31B_q4_0-it.gguf", 17651001568L, "179cfb99212709597eae5929112cfca677e1bbf566178b479ae1da0c4772874b"),
        ]) { DisplayName = "Gemma 4 31B", PromptKind = TranslationPromptKind.General, MinimumRamGiB = 48, GpuGiB = 24, Tier = "workstation", License = "Apache 2.0" },
    ];
    internal static TranslationModelProfile Find(string? id) => All.FirstOrDefault(p => p.Id == id) ?? TranslationModelProfile.QwenInstruct;
    internal static string Normalize(string? id) => Find(id).Id;
}
