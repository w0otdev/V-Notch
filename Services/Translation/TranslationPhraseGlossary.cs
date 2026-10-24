using System;
using System.Runtime.InteropServices;

namespace VNotch.Services.Translation;

internal static partial class TranslationPhraseGlossary
{
    internal sealed record Entry(string Source, string Meaning);

    internal static unsafe Entry? Find(string text, string source = "auto")
    {
        if (string.IsNullOrEmpty(text)) return null;
        string src = source ?? "auto";

        Span<char> srcBuf = stackalloc char[16];
        Span<char> meaningBuf = stackalloc char[512];
        fixed (char* pText = text)
        fixed (char* pSrc = src)
        fixed (char* pOutSrc = srcBuf)
        fixed (char* pOutMeaning = meaningBuf)
        {
            int rc = vn_translation_glossary_find(
                pText, text.Length,
                pSrc, src.Length,
                pOutSrc, srcBuf.Length, out int outSrcLen,
                pOutMeaning, meaningBuf.Length, out int outMeaningLen,
                out byte found);
            if (rc == 0 && found != 0 && outSrcLen > 0 && outMeaningLen > 0)
            {
                return new Entry(new string(srcBuf[..outSrcLen]), new string(meaningBuf[..outMeaningLen]));
            }
        }
        return null;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_translation_glossary_find(
        char* text_ptr, int text_len,
        char* src_ptr, int src_len,
        char* out_src_buf, int out_src_cap, out int out_src_len,
        char* out_meaning_buf, int out_meaning_cap, out int out_meaning_len,
        out byte out_found);
}
