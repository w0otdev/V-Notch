using System.Buffers;
using System.Text;

namespace VNotch.Services;

public static partial class SensitiveDataScrubber
{
    private const string Redacted = "[REDACTED]";
    private enum SecretKind { Token, Password, JsonValue }
    private readonly record struct SecretStart(int ValueIndex, SecretKind Kind, int ValueEnd = -1);
    // Every supported field/scheme/token has at least one of these triggers.
    // IndexOfAny uses a reusable SIMD search; an ordinary entry keeps its original string.
    private static readonly SearchValues<char> Triggers = SearchValues.Create(":=Bb-.A");

    public static string Scrub(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        StringBuilder? output = null;
        int cursor = 0, index = 0;
        while (index < input.Length)
        {
            int offset = input.AsSpan(index).IndexOfAny(Triggers);
            if (offset < 0) break;
            index += offset;
            if (TryReadField(input, index, out var start)
                || TryReadAuthentication(input, index, out start)
                || TryReadStandaloneToken(input, index, cursor, out start))
            {
                output ??= new StringBuilder(Math.Min(input.Length, 1024));
                cursor = AppendRedacted(input, output, cursor, start);
                index = cursor;
            }
            else index++;
        }
        if (output == null) return input;
        output.Append(input.AsSpan(cursor));
        return output.ToString();
    }

    private static int AppendRedacted(string input, StringBuilder output, int cursor, SecretStart start)
    {
        int value = start.ValueIndex;
        if (start.Kind == SecretKind.JsonValue)
            while (value < input.Length && char.IsWhiteSpace(input[value])) value++;
        output.Append(input.AsSpan(cursor, value - cursor));
        if (start.ValueEnd >= value)
        {
            output.Append(Redacted);
            return start.ValueEnd;
        }
        if (value < input.Length && input[value] is '"' or '\'')
        {
            char quote = input[value];
            int end = QuotedEnd(input, value, quote);
            output.Append(quote).Append(Redacted).Append(quote);
            return end;
        }
        int stop = value;
        if (start.Kind == SecretKind.JsonValue)
        {
            int depth = 0;
            while (stop < input.Length)
            {
                char c = input[stop];
                if (c is '"' or '\'') { stop = QuotedEnd(input, stop, c); continue; }
                if (depth == 0 && c is ',' or '}' or ']' or '\r' or '\n') break;
                if (c is '{' or '[') depth++;
                else if (c is '}' or ']') depth--;
                stop++;
            }
            output.Append('"').Append(Redacted).Append('"');
        }
        else
        {
            // A marker does not make an attacker-controlled suffix safe.
            if (input.AsSpan(value).StartsWith(Redacted, StringComparison.Ordinal)) stop += Redacted.Length;
            // Bare passphrases have no unambiguous comma/semicolon delimiter.
            while (stop < input.Length && (start.Kind == SecretKind.Password
                ? input[stop] is not ('\r' or '\n') : !IsTokenBoundary(input[stop]))) stop++;
            output.Append(Redacted);
        }
        return stop;
    }

    private static int QuotedEnd(string input, int start, char quote)
    {
        int end = start + 1;
        while (end < input.Length)
        {
            if (input[end] == '\\' && end + 1 < input.Length) end += 2;
            else if (input[end++] == quote) return end;
        }
        return end;
    }

    private static bool IsTokenBoundary(char c) => char.IsWhiteSpace(c) || c is '&' or ',' or ';' or '"' or '\'' or '}' or ']' or ')' or '>';
    private static bool IsWordStart(string input, int index) => index == 0 || !(char.IsLetterOrDigit(input[index - 1]) || input[index - 1] == '_');
    private static bool IsHorizontalSpace(char c) => char.IsWhiteSpace(c) && c is not ('\r' or '\n');
    private static int SkipSpace(string input, int index)
    {
        while (index < input.Length && IsHorizontalSpace(input[index])) index++;
        return index;
    }
}
