using System.Text;
using System.Text.RegularExpressions;

namespace VNotch.Services.Translation;

// Source-side disambiguation is shared by every model and target language.
// Entries describe meaning; the model still produces the target translation.
internal static class TranslationPhraseGlossary
{
    internal sealed record Entry(string Source, string Meaning);

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["yêu xa"] = new("vi", "This expression means being in a romantic relationship while living far apart (a long-distance relationship). Preserve the geographic separation in the target language. It does not mean 'I love you' or 'I miss you'.")
    };

    internal static Entry? Find(string text, string source = "auto")
    {
        string key = text.Normalize(NormalizationForm.FormC).Trim();
        int start = 0, end = key.Length;
        while (start < end && (char.IsPunctuation(key[start]) || char.IsWhiteSpace(key[start]))) start++;
        while (end > start && (char.IsPunctuation(key[end - 1]) || char.IsWhiteSpace(key[end - 1]))) end--;
        key = Regex.Replace(key[start..end], @"\s+", " ");
        return Entries.TryGetValue(key, out var entry) && (source == "auto" || source == entry.Source) ? entry : null;
    }
}
