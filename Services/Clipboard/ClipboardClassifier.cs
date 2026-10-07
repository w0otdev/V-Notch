using System.Globalization;
using System.Text.RegularExpressions;
using VNotch.Models;

namespace VNotch.Services.Clipboard;

/// <summary>Deterministic offline classification. Rules can be added without changing capture or storage.</summary>
public static partial class ClipboardClassifier
{
    public static readonly string[] GroupNames = ["Work", "Ideas", "Email", "Shopping", "Receipts", "Important"];
    private static readonly (string Group, string[] Strong, string[] Context)[] Rules =
    [
        ("Work", ["project", "meeting", "deadline", "pull request", "dự án", "công việc", "cuộc họp"], ["agenda", "task", "commit"]),
        ("Ideas", ["idea", "brainstorm", "inspiration", "sketch", "ý tưởng", "cảm hứng"], ["concept", "palette"]),
        ("Email", ["subject:", "mailto:", "kính gửi"], ["from:", "to:", "dear ", "regards", "trân trọng"]),
        ("Shopping", ["shopping", "add to cart", "amazon.", "shopee.", "lazada.", "mua hàng", "giỏ hàng"], ["checkout", "product"]),
        ("Receipts", ["receipt", "invoice", "total paid", "hóa đơn", "hoá đơn"], ["subtotal", "order total", "thanh toán"]),
        ("Important", ["important", "urgent", "critical", "asap", "quan trọng", "khẩn cấp"], [])
    ];

    public static ClipboardKind Detect(ClipboardCapture capture)
    {
        if (capture.FilePaths.Length != 0)
            return capture.FilePaths.All(p => IsImagePath(p)) ? ClipboardKind.Image : ClipboardKind.File;
        if (capture.ImagePng != null) return ClipboardKind.Image;
        string text = capture.Text.Trim();
        if (TryParseColor(text, out _)) return ClipboardKind.Color;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "mailto")
            return ClipboardKind.Link;
        if (TryExtractHexColor(text, out _)) return ClipboardKind.Color;
        if (CodePattern().IsMatch(text)) return ClipboardKind.Code;
        return ClipboardKind.Text;
    }

    private static bool IsImagePath(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp" or ".ico";

    public static string[] Classify(string text, ClipboardKind kind)
    {
        // A strong signal, or two independent context signals, reaches the confidence threshold.
        var groups = Rules.Where(rule => rule.Strong.Count(word => ContainsPhrase(text, word)) * 2 +
                                        rule.Context.Count(word => ContainsPhrase(text, word)) >= 2)
            .Select(rule => rule.Group).ToList();
        if (EmailPattern().IsMatch(text) && !groups.Contains("Email")) groups.Add("Email");
        return groups.ToArray();
    }

    private static bool ContainsPhrase(string text, string phrase)
    {
        int offset = 0;
        while (offset < text.Length)
        {
            int index = text.IndexOf(phrase, offset, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            int end = index + phrase.Length;
            if ((index == 0 || !char.IsLetterOrDigit(text[index - 1])) &&
                (!char.IsLetterOrDigit(phrase[^1]) || end == text.Length || !char.IsLetterOrDigit(text[end]))) return true;
            offset = end;
        }
        return false;
    }

    public static bool TryParseColor(string value, out uint argb)
    {
        argb = 0;
        string text = value.Trim();
        if (!ColorPattern().IsMatch(text)) return false;
        if (text[0] == '#')
        {
            string hex = text[1..];
            if (hex.Length is 3 or 4) hex = string.Concat(hex.Select(c => new string(c, 2)));
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint color)) return false;
            argb = hex.Length == 6 ? 0xFF000000 | color : (color >> 8) | ((color & 0xFF) << 24);
            return true;
        }
        string[] parts = text[(text.IndexOf('(') + 1)..^1].Split(',', StringSplitOptions.TrimEntries);
        if (!byte.TryParse(parts[0], out byte red) || !byte.TryParse(parts[1], out byte green) ||
            !byte.TryParse(parts[2], out byte blue)) return false;
        double alpha = 1;
        if (parts.Length == 4 && (!double.TryParse(parts[3], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out alpha) || alpha is < 0 or > 1)) return false;
        argb = ((uint)Math.Round(alpha * 255) << 24) | ((uint)red << 16) | ((uint)green << 8) | blue;
        return true;
    }

    public static bool TryExtractHexColor(string text, out string hex)
    {
        hex = "";
        var match = HexTokenPattern().Match(text);
        if (!match.Success || match.NextMatch().Success) return false;
        hex = match.Value.ToUpperInvariant();
        return true;
    }

    [GeneratedRegex(@"(?<![\w#])#(?:[0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{4}|[0-9a-fA-F]{3})(?![\w#])", RegexOptions.CultureInvariant)]
    private static partial Regex HexTokenPattern();

    [GeneratedRegex(@"^(#[0-9a-fA-F]{3,4}|#[0-9a-fA-F]{6}|#[0-9a-fA-F]{8}|rgba?\(\s*\d+\s*,\s*\d+\s*,\s*\d+(?:\s*,\s*[\d.]+)?\s*\))$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();
    [GeneratedRegex(@"(?m)(^\s*(using\s+[\w.]+;|import\s|from\s+\w+\s+import|(?:public|private|internal)\s+(?:static\s+)?(?:class|void|Task)|(?:const|let|var)\s+\w+\s*=|def\s+\w+\(|function\s+\w+\(|SELECT\s+.+\s+FROM|<\/?(?:div|html|script|style|body)\b)|=>\s*\{|[{};]\s*$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
    [GeneratedRegex(@"\b[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();
}
