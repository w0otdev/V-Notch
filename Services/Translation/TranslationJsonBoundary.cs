namespace VNotch.Services.Translation;

// Tracks the outer JSON object across chunks without reparsing every string brace.
internal sealed class TranslationJsonBoundary
{
    private bool _quoted, _escaped, _started;
    private int _depth;

    internal bool Append(string chunk)
    {
        foreach (char c in chunk)
        {
            if (_quoted)
            {
                if (_escaped) _escaped = false;
                else if (c == '\\') _escaped = true;
                else if (c == '"') _quoted = false;
                continue;
            }
            if (c == '"') _quoted = true;
            else if (c == '{') { _started = true; _depth++; }
            else if (c == '}' && _started && --_depth == 0) return true;
        }
        return false;
    }
}
