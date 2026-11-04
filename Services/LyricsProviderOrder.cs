namespace VNotch.Services;

internal static class LyricsProviderOrder
{
    internal const string Amll = "AMLL TTML DB";
    internal const string Default = "AMLL TTML DB,NetEase,Kugou,QQ Music,LRCLIB,lrc mux";
    internal const string Legacy = "LRCLIB,lrc mux";
    private static readonly string[] Providers = Default.Split(',');
    internal static string[] Normalize(string? order) =>
        (order is "LRCLIB,NetEase,lrc mux" or "NetEase,Kugou,QQ Music,LRCLIB,lrc mux" ? Default : order ?? "").Split(',')
        .Select(value => value.Trim())
        .Concat(Providers)
        .Where(value => Providers.Contains(value, StringComparer.Ordinal))
        .Distinct(StringComparer.Ordinal).ToArray();
}
