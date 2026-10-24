using System;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VNotch.Services;

internal static partial class SpotifyJson
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll)]
    private static partial int vn_spotify_json_is_depth_within_limit(
        int depth,
        out byte outWithinLimit);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotify_json_matches_property_icase(
        ReadOnlySpan<char> propName,
        int propLen,
        ReadOnlySpan<char> targetName,
        int targetLen,
        out byte outMatches);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotify_json_is_non_whitespace(
        ReadOnlySpan<char> value,
        int valueLen,
        out byte outNonWs);

    private static bool IsDepthValid(int depth) =>
        vn_spotify_json_is_depth_within_limit(depth, out byte within) == 0 ? within != 0 : depth <= 16;

    private static bool MatchesNameIgnoreCase(string propName, string targetName) =>
        vn_spotify_json_matches_property_icase(propName.AsSpan(), propName.Length, targetName.AsSpan(), targetName.Length, out byte m) == 0
            ? m != 0
            : propName.Equals(targetName, StringComparison.OrdinalIgnoreCase);

    private static bool HasNonWhitespace(string? s) =>
        s is not null && vn_spotify_json_is_non_whitespace(s.AsSpan(), s.Length, out byte nonWs) == 0 && nonWs != 0;

    internal static string? GetDirectString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (TryGetProperty(element, name, out var property) && property.ValueKind == JsonValueKind.String)
                return property.GetString();
        }
        return null;
    }

    internal static string? FindStringProperty(JsonElement element, string name, int depth)
    {
        if (!IsDepthValid(depth))
            return null;

        return element.ValueKind switch
        {
            JsonValueKind.Object => FindStringInObject(element, name, depth),
            JsonValueKind.Array => FindStringInArray(element, name, depth),
            _ => null
        };
    }

    private static string? FindStringInObject(JsonElement element, string name, int depth)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(name) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        // Keep compatibility with differently cased providers only after the allocation-free pass.
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String &&
                MatchesNameIgnoreCase(property.Name, name))
                return property.Value.GetString();
        }

        foreach (var property in element.EnumerateObject())
        {
            string? nested = FindStringProperty(property.Value, name, depth + 1);
            if (HasNonWhitespace(nested))
                return nested;
        }

        return null;
    }

    private static string? FindStringInArray(JsonElement element, string name, int depth)
    {
        foreach (var item in element.EnumerateArray())
        {
            string? nested = FindStringProperty(item, name, depth + 1);
            if (HasNonWhitespace(nested))
                return nested;
        }

        return null;
    }

    internal static double? FindNumberProperty(JsonElement element, string name, int depth)
    {
        if (!IsDepthValid(depth))
            return null;

        return element.ValueKind switch
        {
            JsonValueKind.Object => FindNumberInObject(element, name, depth),
            JsonValueKind.Array => FindNumberInArray(element, name, depth),
            _ => null
        };
    }

    private static double? FindNumberInObject(JsonElement element, string name, int depth)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(name) &&
                property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetDouble(out double value))
            {
                return value;
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number &&
                MatchesNameIgnoreCase(property.Name, name) &&
                property.Value.TryGetDouble(out double value))
                return value;
        }

        foreach (var property in element.EnumerateObject())
        {
            double? nested = FindNumberProperty(property.Value, name, depth + 1);
            if (nested.HasValue)
                return nested;
        }

        return null;
    }

    private static double? FindNumberInArray(JsonElement element, string name, int depth)
    {
        foreach (var item in element.EnumerateArray())
        {
            double? nested = FindNumberProperty(item, name, depth + 1);
            if (nested.HasValue)
                return nested;
        }

        return null;
    }

    internal static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name))
                {
                    value = property.Value;
                    return true;
                }
            }

            var enumerator = element.EnumerateObject();
            while (enumerator.MoveNext())
            {
                var property = enumerator.Current;
                if (MatchesNameIgnoreCase(property.Name, name))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

}
