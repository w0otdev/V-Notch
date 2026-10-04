using System.Text.Json;

namespace VNotch.Services;

internal static class SpotifyJson
{
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
        if (depth > 16)
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
                property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property.Value.GetString();
        }

        foreach (var property in element.EnumerateObject())
        {
            string? nested = FindStringProperty(property.Value, name, depth + 1);
            if (!string.IsNullOrWhiteSpace(nested))
                return nested;
        }

        return null;
    }

    private static string? FindStringInArray(JsonElement element, string name, int depth)
    {
        foreach (var item in element.EnumerateArray())
        {
            string? nested = FindStringProperty(item, name, depth + 1);
            if (!string.IsNullOrWhiteSpace(nested))
                return nested;
        }

        return null;
    }

    internal static double? FindNumberProperty(JsonElement element, string name, int depth)
    {
        if (depth > 16)
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
                property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
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
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
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
