namespace VNotch.Services;

public static partial class SensitiveDataScrubber
{
    private static bool TryReadStandaloneToken(string input, int index, int cursor, out SecretStart start)
    {
        start = default;
        var text = input.AsSpan(index);
        int value;
        switch (input[index])
        {
            case '-' when index >= 2 && input.AsSpan(index - 2, 2).SequenceEqual("sk")
                && IsWordStart(input, index - 2) && HasTokenAlphabet(text[1..], 8, encrypted: false):
                value = index - 2;
                break;
            case 'A' when text.StartsWith("AIza", StringComparison.Ordinal)
                && HasTokenAlphabet(text[4..], 16, encrypted: false):
                value = index + 4; // Preserve the established AIza diagnostic prefix.
                break;
            case ':' when index >= 3 && input.AsSpan(index - 3, 3).SequenceEqual("enc")
                && HasTokenAlphabet(text[1..], 16, encrypted: true):
                value = index + 1;
                break;
            case '.':
                return TryReadJwt(input, index, cursor, out start);
            default:
                return false;
        }
        int end = value;
        while (end < input.Length && !IsTokenBoundary(input[end])) end++;
        start = new(value, SecretKind.Token, end);
        return true;
    }

    private static bool HasTokenAlphabet(ReadOnlySpan<char> text, int minimum, bool encrypted)
    {
        if (text.Length < minimum) return false;
        for (int i = 0; i < minimum; i++)
            if (!(char.IsAsciiLetterOrDigit(text[i]) || (encrypted ? text[i] is '+' or '/' or '=' : text[i] is '_' or '-'))) return false;
        return true;
    }

    private static bool TryReadJwt(string input, int separator, int cursor, out SecretStart start)
    {
        start = default;
        int header = separator;
        while (header > cursor && IsBase64Url(input[header - 1])) header--;
        if (header == separator || !StartsWithJsonObject(input.AsSpan(header, separator - header))) return false;
        int end = separator + 1, separators = 1;
        while (end < input.Length && (IsBase64Url(input[end]) || input[end] == '.'))
        {
            if (input[end] == '.') separators++;
            end++;
        }
        // JWS (including alg=none) and JWE both have at least two separators.
        // A scrubber also removes malformed extensions; it never verifies signatures.
        if (separators < 2) return false;
        start = new(header, SecretKind.Token, end);
        return true;
    }

    private static bool StartsWithJsonObject(ReadOnlySpan<char> encoded)
    {
        uint buffer = 0;
        int bits = 0;
        foreach (char c in encoded)
        {
            int value = c is >= 'A' and <= 'Z' ? c - 'A'
                : c is >= 'a' and <= 'z' ? c - 'a' + 26
                : c is >= '0' and <= '9' ? c - '0' + 52 : c == '-' ? 62 : 63;
            buffer = (buffer << 6) | (uint)value;
            bits += 6;
            if (bits < 8) continue;
            bits -= 8;
            byte decoded = (byte)(buffer >> bits);
            if (decoded is not (9 or 10 or 13 or 32)) return decoded == '{';
        }
        return false;
    }

    private static bool IsBase64Url(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';
}
