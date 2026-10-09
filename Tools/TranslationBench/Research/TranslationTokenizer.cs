using System.IO;
using System.Text.Json.Nodes;
using System.Text;
using Tokenizers.DotNet;

namespace VNotch.Services.Translation;

internal static class TranslationTokenizer
{
    internal static Tokenizer Create(string verifiedPath, bool m2m = true)
    {
        // The export omits intermediate SentencePiece pieces that some BPE rules need.
        // Restore them with temporary IDs, preserving every original merge and model ID.
        string prepared = m2m ? PrepareVocabulary(File.ReadAllText(verifiedPath)) : PrepareMadlad(File.ReadAllText(verifiedPath));
        string temporary = Path.GetTempFileName();
        try { File.WriteAllText(temporary, prepared); return new Tokenizer(temporary); }
        finally { File.Delete(temporary); }
    }

    internal const uint TemporaryIdStart = 128112;
    internal static string PrepareMadlad(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new TranslationException("translation.modelInvalid");
        // The original SP model uses identity normalization and byte fallback. The
        // conversion's fast-tokenizer JSON loses both whitespace and unknown Unicode.
        root["normalizer"] = null;
        root["model"]!["byte_fallback"] = true;
        root["decoder"] = JsonNode.Parse("{\"type\":\"Sequence\",\"decoders\":[{\"type\":\"Replace\",\"pattern\":{\"String\":\"▁\"},\"content\":\" \"},{\"type\":\"ByteFallback\"},{\"type\":\"Fuse\"},{\"type\":\"Strip\",\"content\":\" \",\"start\":1,\"stop\":0}]}");
        return root.ToJsonString();
    }
    internal static uint[] Encode(Tokenizer tokenizer, string text)
        => tokenizer.Encode(text.Normalize(NormalizationForm.FormC)).Select(id => id >= TemporaryIdStart ? 3U : id).ToArray();

    internal static string PrepareVocabulary(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new TranslationException("translation.modelInvalid");
        var model = root["model"];
        if (model?["vocab"] is not JsonObject vocabulary || model["merges"] is not JsonArray merges)
            throw new TranslationException("translation.modelInvalid");
        uint next = TemporaryIdStart;
        void Add(string piece) { if (!vocabulary.ContainsKey(piece)) vocabulary[piece] = next++; }
        foreach (var merge in merges)
        {
            string? value = merge?.GetValue<string>();
            string[]? parts = value?.Split(' ');
            if (parts is not { Length: 2 }) throw new TranslationException("translation.modelInvalid");
            Add(parts[0]); Add(parts[1]); Add(parts[0] + parts[1]);
        }
        // Remaining original SP pieces absent from HF vocabulary, not used by any merge.
        Add("\u0085"); Add("📺"); Add("🤤");
        // SentencePiece's normalizer already maps its whitespace. Rust WhitespaceSplit
        // additionally erases U+0085, which is an original SP piece; use Metaspace alone.
        root["pre_tokenizer"] = JsonNode.Parse("{\"type\":\"Metaspace\",\"replacement\":\"▁\",\"add_prefix_space\":true}");
        return root.ToJsonString();
    }
}
