using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace VNotch.Services;

internal sealed partial class LyricsService
{
    private static readonly HttpClient AmllHttp = CreateClient("https://raw.githubusercontent.com", 2000);
    private long _amllRetryUntil;
    private static readonly XNamespace Ttml = "http://www.w3.org/ns/ttml";
    private static readonly XNamespace TtmlMetadata = "http://www.w3.org/ns/ttml#metadata";

    internal static string? NormalizeSpotifyTrackId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.StartsWith("spotify:track:", StringComparison.Ordinal))
            value = value[14..];
        else if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                 uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase))
        {
            string[] path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (path.Length < 2 || path[^2] != "track") return null;
            value = path[^1];
        }
        return value.Length == 22 && value.All(char.IsAsciiLetterOrDigit) ? value : null;
    }

    private async Task<LyricsResult?> TryAmllAsync(string? trackId, CancellationToken token)
    {
        trackId = NormalizeSpotifyTrackId(trackId);
        if (trackId == null || DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _amllRetryUntil)) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_amllClient.Timeout);
        try
        {
            using var response = await _amllClient.GetAsync(
                $"/amll-dev/amll-ttml-db/main/spotify-lyrics/{trackId}.ttml",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                MarkCooldown(ref _amllRetryUntil, TimeSpan.FromMinutes(2));
                return null;
            }
            // A missing community contribution is normal and must not cool down other tracks.
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxLyricsResponseBytes) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new System.IO.MemoryStream();
            byte[] chunk = new byte[8192];
            while (true)
            {
                int read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaxLyricsResponseBytes) return null;
                buffer.Write(chunk, 0, read);
            }
            timeout.Token.ThrowIfCancellationRequested();
            buffer.Position = 0;
            using var reader = XmlReader.Create(buffer, TtmlReaderSettings());
            return ParseAmllTtml(XDocument.Load(reader, LoadOptions.PreserveWhitespace));
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
        {
            MarkCooldown(ref _amllRetryUntil, TimeSpan.FromMinutes(2));
            throw;
        }
    }

    private static XmlReaderSettings TtmlReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxLyricsResponseBytes
    };

    internal static LyricsResult? ParseAmllTtml(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > MaxLyricsResponseBytes) return null;
        using var input = new System.IO.StringReader(xml);
        using var reader = XmlReader.Create(input, TtmlReaderSettings());
        return ParseAmllTtml(XDocument.Load(reader, LoadOptions.PreserveWhitespace));
    }

    private static LyricsResult? ParseAmllTtml(XDocument document)
    {
        if (document.Root?.Name != Ttml + "tt") return null;
        var body = document.Root.Element(Ttml + "body");
        if (body == null) return null;
        var lines = new List<LyricLine>();
        foreach (var p in body.Descendants(Ttml + "p"))
        {
            if (!TryTtmlTime((string?)p.Attribute("begin"), out var begin) ||
                !TryTtmlTime((string?)p.Attribute("end"), out var end) || end <= begin) continue;
            var text = new StringBuilder();
            var words = new List<LyricWord>();
            bool validWords = true;
            AppendTtmlContent(p, text, words, begin, end, ref validWords, 0);
            string label = text.ToString();
            if (string.IsNullOrWhiteSpace(label) || label.Length > 4096) continue;
            lines.Add(new(begin, label, validWords ? LyricTiming.ValidateWords(begin, label, words) : null));
        }
        // OrderBy is stable for simultaneous duet lines.
        return lines.Count == 0 ? null : new(lines.OrderBy(line => line.Time).ToList(), LyricsProviderOrder.Amll);
    }

    private static void AppendTtmlContent(XElement element, StringBuilder text, List<LyricWord> words,
        TimeSpan lineStart, TimeSpan lineEnd, ref bool validWords, int depth)
    {
        if (depth > 32 || text.Length > 4096) { validWords = false; return; }
        foreach (var node in element.Nodes())
        {
            if (node is XText content)
            {
                // Pretty-print indentation is not a lyric space. Explicit spaces between
                // spans (including untimed space spans) must keep their UTF-16 offsets.
                if (string.IsNullOrWhiteSpace(content.Value) && content.Value.IndexOfAny(['\r', '\n']) >= 0 &&
                    !element.AncestorsAndSelf().Any(e => (string?)e.Attribute(XNamespace.Xml + "space") == "preserve")) continue;
                text.Append(content.Value);
            }
            else if (node is XElement span && span.Name == Ttml + "span")
            {
                string? role = (string?)span.Attribute(TtmlMetadata + "role");
                // The compact lyrics view displays the lead vocal, without mixing
                // auxiliary translations, romanization or overlapping backing vocals.
                if (role is "x-translation" or "x-roman" or "x-bg") continue;
                int offset = text.Length;
                int previousWords = words.Count;
                AppendTtmlContent(span, text, words, lineStart, lineEnd, ref validWords, depth + 1);
                int length = text.Length - offset;
                if (words.Count != previousWords || length == 0 ||
                    string.IsNullOrWhiteSpace(text.ToString(offset, length))) continue;
                if (span.Attribute("begin") == null && span.Attribute("end") == null) continue;
                if (!TryTtmlTime((string?)span.Attribute("begin"), out var start) ||
                    !TryTtmlTime((string?)span.Attribute("end"), out var end) ||
                    start < lineStart || end < start || end > lineEnd)
                    validWords = false;
                else
                    words.Add(new(start, end, offset, length));
            }
        }
    }

    private static bool TryTtmlTime(string? value, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrEmpty(value)) return false;
        if (value.EndsWith('s')) value = value[..^1];
        string[] parts = value.Split(':');
        if (parts.Length > 3) return false;
        double seconds = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            if (!double.TryParse(parts[i], last ? NumberStyles.AllowDecimalPoint : NumberStyles.None,
                    CultureInfo.InvariantCulture, out double part) || !double.IsFinite(part) || part < 0 ||
                (parts.Length > 1 && (i > 0 || parts.Length == 2) && part >= 60)) return false;
            seconds = seconds * 60 + part;
        }
        if (seconds > 86400) return false;
        time = TimeSpan.FromSeconds(seconds);
        return true;
    }
}
