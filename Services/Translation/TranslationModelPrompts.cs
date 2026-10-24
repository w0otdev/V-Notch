using System.Runtime.InteropServices;

namespace VNotch.Services.Translation;

internal static partial class TranslationModelPrompts
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_transprompts_is_hunyuan_supported(
        ReadOnlySpan<ushort> source, int sourceLen,
        ReadOnlySpan<ushort> target, int targetLen,
        out byte outSupported);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_transprompts_sanitize_control_tokens(
        ReadOnlySpan<ushort> text, int textLen,
        Span<ushort> outBuf, int outCap, out int outLen);

    private static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        ReadOnlySpan<ushort> sp = MemoryMarshal.Cast<char, ushort>(text.AsSpan());
        Span<char> buf = stackalloc char[512];
        int rc = vn_transprompts_sanitize_control_tokens(sp, sp.Length, MemoryMarshal.Cast<char, ushort>(buf), buf.Length, out int outLen);
        if (rc == 0) return outLen > 0 ? new string(buf[..outLen]) : string.Empty;
        if (rc == -2 && outLen > 0)
        {
            char[] arr = new char[outLen];
            if (vn_transprompts_sanitize_control_tokens(sp, sp.Length, MemoryMarshal.Cast<char, ushort>(arr.AsSpan()), arr.Length, out int actual) == 0)
                return new string(arr, 0, actual);
        }
        return text;
    }

    internal static string Build(TranslationModelProfile model, string text, string source, string target, bool enableHumanizer = true)
    {
        // Do not let text containing model control tokens break out of the user turn.
        text = Sanitize(text);
        if (source == "auto")
        {
            string? detected = TranslationLanguages.Detect(text);
            if (detected != null && detected != target) source = detected;
        }
        string targetName = TranslationLanguages.EnglishName(target);
        string meaning = TranslationMeaningIntegrity.Instruction(text, source, enableHumanizer);
        string glossary = QwenTranslationEngine.Glossary(source, target, enableHumanizer);
        if (glossary.Length > 0) meaning += glossary + " ";
        if (model.PromptKind == TranslationPromptKind.Hunyuan)
        {
            ReadOnlySpan<ushort> srcSpan = MemoryMarshal.Cast<char, ushort>(source != null ? source.AsSpan() : ReadOnlySpan<char>.Empty);
            ReadOnlySpan<ushort> tgtSpan = MemoryMarshal.Cast<char, ushort>(target != null ? target.AsSpan() : ReadOnlySpan<char>.Empty);
            if (vn_transprompts_is_hunyuan_supported(srcSpan, srcSpan.Length, tgtSpan, tgtSpan.Length, out byte supported) != 0 || supported == 0)
                throw new TranslationException("translation.languageUnsupported");
            return $"<|startoftext|>Translate the following segment into {targetName}, without additional explanation. {meaning}\n\n{text}<|extra_0|>";
        }
        if (source == "auto")
            return $"<bos><start_of_turn>user\nYou are a professional multilingual translator. Identify the language of the following text, even if it is a single word, and translate it into {targetName} ({target}). Produce only the {targetName} translation, without language labels, explanations or commentary. {meaning}Please translate the following text into {targetName}:\n\n\n{text.Trim()}<end_of_turn>\n<start_of_turn>model\n";
        string sourceName = TranslationLanguages.EnglishName(source);
        // Render the text-only branch of the pinned TranslateGemma chat template.
        return $"<bos><start_of_turn>user\nYou are a professional {sourceName} ({source}) to {targetName} ({target}) translator. Your goal is to accurately convey the meaning and nuances of the original {sourceName} text while adhering to {targetName} grammar, vocabulary, and cultural sensitivities.\nProduce only the {targetName} translation, without any additional explanations or commentary. {meaning}Please translate the following {sourceName} text into {targetName}:\n\n\n{text.Trim()}<end_of_turn>\n<start_of_turn>model\n";
    }
}
