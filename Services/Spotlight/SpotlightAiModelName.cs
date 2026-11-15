using System.Globalization;

namespace VNotch.Services.Spotlight;

/// <summary>Presentation only: the configured model ID sent to the API is never changed.</summary>
internal static class SpotlightAiModelName
{
    private static readonly string[] Families = ["gpt", "claude", "gemini", "deepseek", "sol", "o1", "o3", "o4"];

    internal static string Format(string model, string provider)
    {
        model = model.Trim();
        if (model.Length == 0) return provider;
        // User-defined names (for example "Sol 6.1" or "Astra") stay intact.
        if (model.Contains(' ') || model.Contains('/')) return model;
        string? family = Families
            .FirstOrDefault(prefix => model.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase));
        if (family == null) return model;
        if (model.Length > 11 && model[^11] == '-' && DateTime.TryParseExact(model[^10..], "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            model = model[..^11];

        string brand = family switch { "gpt" => "GPT", "deepseek" => "DeepSeek", "o1" or "o3" or "o4" => family,
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(family) };
        string[] parts = model[(family.Length + 1)..].Split('-', StringSplitOptions.RemoveEmptyEntries);
        var words = new List<string>();
        foreach (string part in parts)
        {
            // API snapshot dates remain available in the tooltip, not the short title.
            if (part.Length == 8 && part.All(char.IsAsciiDigit)) continue;
            if (family == "claude" && part.All(char.IsAsciiDigit) && words.Count > 0 &&
                words[^1].All(c => char.IsAsciiDigit(c) || c == '.'))
                words[^1] += "." + part;
            else
                words.Add(char.IsAsciiDigit(part[0]) ? part : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(part));
        }
        if (words.Count == 0) return model;
        return brand + (family == "gpt" ? "-" : " ") + string.Join(" ", words);
    }
}
