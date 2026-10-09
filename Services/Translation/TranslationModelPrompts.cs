namespace VNotch.Services.Translation;

internal static class TranslationModelPrompts
{
    private static readonly HashSet<string> HunyuanLanguages = "zh en fr pt es ja tr ru ar ko th it de vi ms id tl hi pl cs nl km my fa gu ur te mr he bn ta uk bo kk mn ug yue".Split(' ').ToHashSet();
    internal static string Build(TranslationModelProfile model, string text, string source, string target)
    {
        // Do not let text containing model control tokens break out of the user turn.
        text = text.Replace("<|", "< |", StringComparison.Ordinal).Replace("<start_of_turn>", "< start_of_turn >", StringComparison.Ordinal)
            .Replace("<end_of_turn>", "< end_of_turn >", StringComparison.Ordinal).Replace("<bos>", "< bos >", StringComparison.Ordinal);
        string targetName = TranslationLanguages.EnglishName(target);
        string meaning = TranslationMeaningIntegrity.Instruction(text, source);
        if (model.PromptKind == TranslationPromptKind.Hunyuan)
        {
            if (!HunyuanLanguages.Contains(target) || (source != "auto" && !HunyuanLanguages.Contains(source)))
                throw new TranslationException("translation.languageUnsupported");
            return $"<|startoftext|>Translate the following segment into {targetName}, without additional explanation. {meaning}\n\n{text}<|extra_0|>";
        }
        if (source == "auto") source = TranslationLanguages.Detect(text) ?? "auto";
        if (source == "auto")
            return $"<bos><start_of_turn>user\nYou are a professional multilingual translator. Identify the language of the following text, even if it is a single word, and translate it into {targetName} ({target}). Produce only the {targetName} translation, without language labels, explanations or commentary. {meaning}Please translate the following text into {targetName}:\n\n\n{text.Trim()}<end_of_turn>\n<start_of_turn>model\n";
        string sourceName = TranslationLanguages.EnglishName(source);
        // Render the text-only branch of the pinned TranslateGemma chat template.
        return $"<bos><start_of_turn>user\nYou are a professional {sourceName} ({source}) to {targetName} ({target}) translator. Your goal is to accurately convey the meaning and nuances of the original {sourceName} text while adhering to {targetName} grammar, vocabulary, and cultural sensitivities.\nProduce only the {targetName} translation, without any additional explanations or commentary. {meaning}Please translate the following {sourceName} text into {targetName}:\n\n\n{text.Trim()}<end_of_turn>\n<start_of_turn>model\n";
    }
}
