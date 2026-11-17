using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VNotch.Services.Translation;

// Preview is untrusted presentation only. The service still validates the complete result.
internal sealed partial class TranslationPreview(Action<string>? publish)
{
    private long _lastPublished;

    internal void Report(StringBuilder output, bool json = false, IReadOnlyDictionary<string, string>? literals = null)
    {
        if (publish == null || (_lastPublished != 0 && Stopwatch.GetElapsedTime(_lastPublished).TotalMilliseconds < 50)) return;
        _lastPublished = Stopwatch.GetTimestamp();
        string text = json ? ReadJsonStringPrefix(output.ToString()) : output.ToString();
        if (literals != null)
            foreach (var (key, value) in literals) text = text.Replace(key, value, StringComparison.Ordinal);
        // A split placeholder or model stop token must never flash in the popup.
        int marker = text.IndexOf("__VNT_", StringComparison.Ordinal);
        if (marker >= 0) text = text[..marker];
        for (int length = Math.Min(5, text.Length); length > 0; length--)
            if (text.EndsWith("__VNT_"[..length], StringComparison.Ordinal)) { text = text[..^length]; break; }
        int control = text.IndexOf('<');
        if (control >= 0) text = text[..control];
        text = text.TrimEnd('_');
        if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text = text[..^1];
        publish(text);
    }

    [GeneratedRegex("^\\s*\\{\\s*\"translation\"\\s*:\\s*\"", RegexOptions.CultureInvariant)]
    private static partial Regex JsonPrefix();

    internal static string ReadJsonStringPrefix(string raw)
    {
        var match = JsonPrefix().Match(raw);
        if (!match.Success) return "";
        int end = match.Length;
        for (int i = end; i < raw.Length; i++)
        {
            if (raw[i] == '"') break;
            if (raw[i] == '\\')
            {
                if (++i >= raw.Length) break;
                if (raw[i] == 'u')
                {
                    if (i + 4 >= raw.Length) break;
                    if (!ushort.TryParse(raw.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code)) break;
                    i += 4;
                    if (char.IsHighSurrogate((char)code))
                    {
                        if (i + 6 >= raw.Length || raw[i + 1] != '\\' || raw[i + 2] != 'u' ||
                            !ushort.TryParse(raw.AsSpan(i + 3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort low) ||
                            !char.IsLowSurrogate((char)low)) break;
                        i += 6;
                    }
                }
            }
            end = i + 1;
        }
        try { return JsonSerializer.Deserialize<string>("\"" + raw[match.Length..end] + "\"") ?? ""; }
        catch (JsonException) { return ""; }
    }
}
