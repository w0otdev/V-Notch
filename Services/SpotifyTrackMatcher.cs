using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VNotch.Services;

internal static partial class SpotifyTrackMatcher
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotify_normalize_for_match(
        ReadOnlySpan<char> valPtr,
        int valLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    internal static string? ParsePathfinderTrackId(string json, string expectedTrack, string expectedArtist) =>
        NativeCatalogParser.Select(json, 1, expectedTrack, expectedArtist).Text;

    internal static string? ParsePathfinderTrackId(ReadOnlySpan<byte> json, string expectedTrack, string expectedArtist) =>
        NativeCatalogParser.Select(json, 1, expectedTrack, expectedArtist).Text;

    internal static string? ParsePathfinderTrackArtists(string json, string expectedTrack, string expectedArtist, string? expectedAlbum = null) =>
        NativeCatalogParser.Select(json, 7, expectedTrack, expectedArtist, album: expectedAlbum).Text;

    internal static string? ParsePathfinderTrackArtists(ReadOnlySpan<byte> json, string expectedTrack, string expectedArtist, string? expectedAlbum = null) =>
        NativeCatalogParser.Select(json, 7, expectedTrack, expectedArtist, album: expectedAlbum).Text;

    internal static string? ParseTrackId(string json, string expectedTrack, string expectedArtist, TimeSpan expectedDuration) =>
        NativeCatalogParser.Select(json, 0, expectedTrack, expectedArtist, (long)expectedDuration.TotalMilliseconds).Text;

    internal static string? ParseTrackId(ReadOnlySpan<byte> json, string expectedTrack, string expectedArtist, TimeSpan expectedDuration) =>
        NativeCatalogParser.Select(json, 0, expectedTrack, expectedArtist, (long)expectedDuration.TotalMilliseconds).Text;

    internal static string NormalizeForMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        ReadOnlySpan<char> span = value.AsSpan();
        int cap = span.Length * 2 + 16;
        Span<char> buf = cap <= 512 ? stackalloc char[cap] : new char[cap];
        int rc = vn_spotify_normalize_for_match(span, span.Length, buf, buf.Length, out int len);
        if (rc == -2 && len > buf.Length) { char[] larger = new char[len]; rc = vn_spotify_normalize_for_match(span, span.Length, larger, larger.Length, out len); return rc == 0 && len > 0 ? new string(larger, 0, len) : ""; }
        if (rc != 0) len = 0;
        return len > 0 ? new string(buf[..len]) : "";
    }

}
