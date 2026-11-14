using System;
using System.Runtime.InteropServices;

namespace VNotch.Services;

internal static partial class LyricsProviderOrder
{
    internal const string Amll = "AMLL TTML DB";
    internal const string Default = "AMLL TTML DB,NetEase,Kugou,QQ Music,LRCLIB,lrc mux";
    internal const string Legacy = "LRCLIB,lrc mux";
    private static readonly string[] Providers =
    [
        Amll,
        "NetEase",
        "Kugou",
        "QQ Music",
        "LRCLIB",
        "lrc mux"
    ];

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_lyrics_provider_order_parse(
        ReadOnlySpan<char> raw,
        int rawLen,
        byte isNull,
        Span<byte> outIndices,
        int cap,
        out int outCount);

    internal static string[] Normalize(string? order)
    {
        ReadOnlySpan<char> span = order is null ? ReadOnlySpan<char>.Empty : order.AsSpan();
        Span<byte> indices = stackalloc byte[6];
        if (vn_lyrics_provider_order_parse(span, span.Length, order is null ? (byte)1 : (byte)0, indices, indices.Length, out int count) != 0 || count <= 0)
            return (string[])Providers.Clone();

        var result = new string[count];
        for (int i = 0; i < count; i++)
            result[i] = Providers[indices[i]];
        return result;
    }
}
