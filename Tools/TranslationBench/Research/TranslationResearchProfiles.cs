namespace VNotch.Services.Translation;

internal static class TranslationResearchProfiles
{
    internal static readonly TranslationModelProfile QwenInstruct = new("qwen3-4b-instruct-2507-q4km-v1", "unsloth/Qwen3-4B-Instruct-2507-GGUF",
        "a06e946bb6b655725eafa393f4a9745d460374c9",
        [new("Qwen3-4B-Instruct-2507-Q4_K_M.gguf", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", 2497281120, "3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597")], 0, 0, false, 0);
    internal static readonly TranslationModelProfile Qwen = new("qwen3-4b-q4km-v1", "Qwen/Qwen3-4B-GGUF",
        "bc640142c66e1fdd12af0bd68f40445458f3869b",
        [new("Qwen3-4B-Q4_K_M.gguf", "Qwen3-4B-Q4_K_M.gguf", 2497280256, "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5")], 0, 0, false, 0);
    internal static readonly TranslationModelProfile Madlad = new("madlad400-3b-int8-v1", "Kutalia/madlad400-3b-mt-onnx",
        "066656188a5390d1d0af6e824707a12cf28fc309",
        [
            new("encoder.onnx", "onnx/encoder_model_quantized.onnx", 1337248347, "c23711455735ade2631cae688cb0213574b177392633efbe7b9a2e7c9269f410"),
            new("decoder.onnx", "onnx/decoder_model_merged_quantized.onnx", 1870804817, "578ecf418be3121133547ead9f122038a63902d6c46f05da6f01350b2a51112a"),
            new("tokenizer.json", "tokenizer.json", 16613995, "03f5d7dc88da0cb4bb6b7a1d9d66ee62f5bd339ef0aaaf6e89d74829df5830c0"),
            new("config.json", "config.json", 850, "a7f208db9a60aad30bdbbdd6beb1de5c92a14e1a4c85ce37de7201ea6c3712d9")
        ], 32, 128, false, 1);

    // Retained solely for reproducible model comparisons; production selects MADLAD.
    internal static readonly TranslationModelProfile M2M100 = new("m2m100-418m-int8-v1", "Xenova/m2m100_418M",
        "9c374f0b7aca709787cea97b047bfbbd1559d177",
        [
            new("encoder.onnx", "onnx/encoder_model_quantized.onnx", 287856370, "13a94e354a9140764eb81102d77d3ec6952d796e6f113c651eeb3c3443da0386"),
            new("decoder.onnx", "onnx/decoder_model_merged_quantized.onnx", 344128178, "007654bcabb6cea6fd3bde34ce933137b431330b3755781145d7b6906270b45a"),
            new("tokenizer.json", "tokenizer.json", 7988527, "03d9e111731c2d71f39a2c2a88499743e4c251385d07f0384b4349a23ba54363"),
            new("config.json", "config.json", 908, "1dbdf77ddc7809acd4c54ccf0eab46f840b40174afb1b6f6de8787244e832938")
        ], 12, 64, true, 5);
}

