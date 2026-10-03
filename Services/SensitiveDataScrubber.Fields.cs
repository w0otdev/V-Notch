using System.Collections.Frozen;

namespace VNotch.Services;

public static partial class SensitiveDataScrubber
{
    // Explicit names make cookie coverage reviewable without extending a regex.
    private static readonly string[] SessionFields = [
        "sp_dc", "session", "sessionid", "session_id", "session_token", "connect.sid", "sid",
        "token", "access_token", "refresh_token", "auth_token", "secret", "client_secret",
        "JSESSIONID", "PHPSESSID", "ASP.NET_SessionId", ".AspNetCore.Cookies"
    ];
    private static readonly FrozenDictionary<char, string[]> SessionFieldsByInitial = SessionFields
        .GroupBy(name => char.ToUpperInvariant(name[0]))
        .ToFrozenDictionary(group => group.Key, group => group.ToArray());

    private static bool TryReadField(string input, int delimiter, out SecretStart start)
    {
        start = default;
        if (input[delimiter] is not (':' or '=')) return false;
        int end = delimiter;
        while (end > 0 && char.IsWhiteSpace(input[end - 1])) end--;
        char quote = end > 0 && input[end - 1] is '"' or '\'' ? input[--end] : '\0';
        int keyStart = end;
        if (quote != '\0')
        {
            while (keyStart > 0)
            {
                keyStart--;
                if (input[keyStart] == quote && !IsEscapedQuote(input, keyStart)) break;
            }
            if (keyStart == end || input[keyStart] != quote) return false;
            keyStart++;
        }
        else
            while (keyStart > 0 && IsFieldCharacter(input[keyStart - 1])) keyStart--;
        scoped ReadOnlySpan<char> key = input.AsSpan(keyStart, end - keyStart);
        Span<char> decoded = stackalloc char[32];
        bool completeName = true;
        if (quote == '"' && key.Contains('\\'))
        {
            if (!TryDecodeFieldName(key, decoded, out int length)) return false;
            completeName = length <= decoded.Length;
            key = decoded[..Math.Min(length, decoded.Length)];
        }
        bool password = IsPasswordField(key);
        bool query = keyStart > 0 && input[keyStart - 1] is '?' or '&';
        if (!password && !IsApiKeyField(key) && !(completeName && IsSessionField(key))
            && !(query && key.Equals("key", StringComparison.OrdinalIgnoreCase))) return false;
        int value = SkipSpace(input, delimiter + 1);
        bool json = quote == '"' && input[delimiter] == ':' && keyStart > 0 && input[keyStart - 1] == '"';
        start = new(value, json ? SecretKind.JsonValue : password ? SecretKind.Password : SecretKind.Token);
        return true;
    }

    private static bool IsPasswordField(ReadOnlySpan<char> key)
    {
        if (key.IsEmpty || char.ToUpperInvariant(key[^1]) != 'D') return false;
        foreach (string name in PasswordFields)
        {
            if (!key.EndsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
            int prefix = key.Length - name.Length;
            if (prefix == 0 || !(char.IsLetterOrDigit(key[prefix - 1]) || key[prefix - 1] == '_')) return true;
        }
        return false;
    }

    private static readonly string[] PasswordFields = ["password", "passwd", "pwd"];
    private static bool IsApiKeyField(ReadOnlySpan<char> key) =>
        !key.IsEmpty && char.ToUpperInvariant(key[^1]) == 'Y' &&
        (key.EndsWith("apikey", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("api_key", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("api-key", StringComparison.OrdinalIgnoreCase));

    private static bool IsSessionField(ReadOnlySpan<char> key)
    {
        if (key.IsEmpty || !SessionFieldsByInitial.TryGetValue(char.ToUpperInvariant(key[0]), out var names)) return false;
        foreach (string name in names)
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool TryReadAuthentication(string input, int index, out SecretStart start)
    {
        start = default;
        if (input[index] is not ('B' or 'b') || !IsWordStart(input, index)) return false;
        var text = input.AsSpan(index);
        int length = text.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase) ? 6
            : text.StartsWith("Basic", StringComparison.OrdinalIgnoreCase) ? 5 : 0;
        if (length == 0 || text.Length <= length || !IsHorizontalSpace(text[length])) return false;
        int value = SkipSpace(input, index + length);
        // Ordinary prose such as "basic view" is not a credential. Header values
        // are always hidden; standalone Basic must encode the user:password separator.
        if (length == 5 && !IsAuthorizationValue(input, index) && !BasicHasCredentialSeparator(input.AsSpan(value))) return false;
        start = new(value, SecretKind.Token);
        return true;
    }

    private static bool IsAuthorizationValue(string input, int index)
    {
        int end = index;
        while (end > 0 && IsHorizontalSpace(input[end - 1])) end--;
        if (end > 0 && input[end - 1] is '"' or '\'') end--;
        while (end > 0 && IsHorizontalSpace(input[end - 1])) end--;
        if (end == 0 || input[--end] is not (':' or '=')) return false;
        while (end > 0 && IsHorizontalSpace(input[end - 1])) end--;
        if (end > 0 && input[end - 1] is '"' or '\'') end--;
        int begin = end;
        while (begin > 0 && IsFieldCharacter(input[begin - 1])) begin--;
        var key = input.AsSpan(begin, end - begin);
        return key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase);
    }

    private static bool BasicHasCredentialSeparator(ReadOnlySpan<char> text)
    {
        if (!text.IsEmpty && text[0] is '"' or '\'') text = text[1..];
        uint buffer = 0;
        int bits = 0;
        foreach (char c in text)
        {
            int value = c is >= 'A' and <= 'Z' ? c - 'A'
                : c is >= 'a' and <= 'z' ? c - 'a' + 26
                : c is >= '0' and <= '9' ? c - '0' + 52 : c == '+' ? 62 : c == '/' ? 63 : -1;
            if (value < 0) return false;
            buffer = (buffer << 6) | (uint)value;
            bits += 6;
            if (bits < 8) continue;
            bits -= 8;
            if ((byte)(buffer >> bits) == ':') return true;
        }
        return false;
    }

    private static bool IsFieldCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.';

    private static bool IsEscapedQuote(string input, int index)
    {
        int backslashes = 0;
        while (index > 0 && input[--index] == '\\') backslashes++;
        return (backslashes & 1) != 0;
    }

    private static bool TryDecodeFieldName(ReadOnlySpan<char> key, Span<char> tail, out int length)
    {
        // Only a fixed-size suffix is needed for API/password names. Session
        // names require the entire decoded key; no attacker-sized buffer is allocated.
        Span<char> ring = stackalloc char[32];
        length = 0;
        for (int i = 0; i < key.Length; i++)
        {
            char c = key[i];
            if (c == '\\')
            {
                if (++i >= key.Length) return false;
                c = key[i];
                if (c == 'u')
                {
                    if (i + 4 >= key.Length) return false;
                    int value = 0;
                    for (int n = 0; n < 4; n++)
                    {
                        char hex = key[++i];
                        int digit = hex is >= '0' and <= '9' ? hex - '0'
                            : hex is >= 'a' and <= 'f' ? hex - 'a' + 10
                            : hex is >= 'A' and <= 'F' ? hex - 'A' + 10 : -1;
                        if (digit < 0) return false;
                        value = (value << 4) | digit;
                    }
                    c = (char)value;
                }
                else
                {
                    c = c switch { '"' => '"', '\\' => '\\', '/' => '/', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', _ => '\0' };
                    if (c == '\0') return false;
                }
            }
            ring[length++ & 31] = c;
        }
        int count = Math.Min(length, ring.Length);
        for (int i = 0; i < count; i++) tail[i] = ring[(length - count + i) & 31];
        return true;
    }
}
