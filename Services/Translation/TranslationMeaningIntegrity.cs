using System.Text.RegularExpressions;

namespace VNotch.Services.Translation;

internal static class TranslationMeaningIntegrity
{
    internal static string Instruction(string text, string source)
    {
        string general = "Preserve the speaker, addressee, tense, negation and sentence type. A statement must remain a statement; a question must remain a question. Do not turn an announcement addressed to the reader into a question. Translate standalone common words by their meaning; keep proper names intact. ";
        if (Regex.IsMatch(text.Trim(), @"^\p{L}+$")) general += "For a standalone word, give its common dictionary translation concisely, rather than a descriptive paraphrase. ";
        if (!IsClearStatement(text, source)) return general;
        return general +
            "This source is a declarative statement, not a question. Use declarative grammar in the translation, without adding a question mark or asking the reader anything. ";
    }

    internal static bool IsClearStatement(string text, string source)
    {
        if (text.IndexOfAny(['?', '？', '؟']) >= 0) return false;
        source = source == "auto" ? TranslationLanguages.Detect(text) ?? "auto" : source;
        // Restrict the guard to recognizable assertions. A missing '?' alone does
        // not make an informal question a statement.
        if (source == "vi")
        {
            if (Regex.IsMatch(text, @"\b(ai|gì|sao|đâu|nào|bao nhiêu|mấy|liệu|hả|nhỉ)\b", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(text, @"\bcó\b.+\b(không|chưa)\b", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(text, @"\b(không|chưa|à|ư|hở)\s*[.!]*$", RegexOptions.IgnoreCase)) return false;
            return Regex.IsMatch(text, @"^\s*(các bạn|bạn|chúng tôi|chúng ta|tôi|họ|anh ấy|cô ấy)\s+(đang|đã|sẽ|không)\b", RegexOptions.IgnoreCase);
        }
        return source == "en" && Regex.IsMatch(text, @"^\s*(you|we|they|I|he|she|it)\s+(are|is|am|was|were|will|have|has|do not|does not|did not)\b", RegexOptions.IgnoreCase);
    }

    internal static bool PreservesStatement(string text, string source, string translated) =>
        !IsClearStatement(text, source) || translated.IndexOfAny(['?', '？', '؟']) < 0;

    internal static string? EnglishStatementSubject(string text, string source, string target)
    {
        if (target != "en" || text.Contains('\n') || !IsClearStatement(text, source)) return null;
        source = source == "auto" ? TranslationLanguages.Detect(text) ?? "auto" : source;
        if (source != "vi") return null;
        var match = Regex.Match(text, @"^\s*(các bạn|bạn|chúng tôi|chúng ta|tôi|họ|anh ấy|cô ấy)\b", RegexOptions.IgnoreCase);
        return match.Value.Trim().ToLowerInvariant() switch
        {
            "các bạn" or "bạn" => "You",
            "chúng tôi" or "chúng ta" => "We",
            "tôi" => "I",
            "họ" => "They",
            "anh ấy" => "He",
            "cô ấy" => "She",
            _ => null
        };
    }
}
