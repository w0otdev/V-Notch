using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Markup;
using System.Windows.Media;

namespace VNotch.Services;

// Fonts are deployment content, so loading a face no longer pages the font bundle
// into the main assembly. Absolute file URIs also work from tests and setup hosts.
public static partial class AppFonts
{
    private const string NativeDll = "VNotch.Native.dll";
    private const int VN_ERR_BUFFER_TOO_SMALL = -2;
    private const string DefaultFontSpec =
        "./#SF Pro Display, Segoe UI Variable Text, Segoe UI, Segoe UI Emoji, Segoe UI Symbol, Yu Gothic UI, Malgun Gothic, Microsoft JhengHei UI, Microsoft YaHei UI, Leelawadee UI, Nirmala UI, Arial";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_text_build_font_spec(
        ReadOnlySpan<char> fallbacksPtr,
        int fallbacksLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    public static Uri BaseUri { get; } = new(Path.Combine(AppContext.BaseDirectory, "Fonts") + Path.DirectorySeparatorChar);
    public static string Location => BaseUri.AbsoluteUri;
    private static readonly ConcurrentDictionary<string, FontFamily> Families = new(StringComparer.Ordinal);

    public static FontFamily Get(string fallbacks = "") => Families.GetOrAdd(fallbacks ?? string.Empty,
        static value => new FontFamily(BaseUri, BuildFontSpec(value)));

    internal static string BuildFontSpec(string? fallbacks = null)
    {
        ReadOnlySpan<char> input = (fallbacks ?? string.Empty).AsSpan();
        try
        {
            Span<char> stackBuf = stackalloc char[512];
            int rc = vn_text_build_font_spec(input, input.Length, stackBuf, stackBuf.Length, out int len);
            if (rc == 0 && len > 0)
            {
                return new string(stackBuf[..len]);
            }
            if (rc == VN_ERR_BUFFER_TOO_SMALL && len > stackBuf.Length)
            {
                char[] heapBuf = new char[len];
                if (vn_text_build_font_spec(input, input.Length, heapBuf, heapBuf.Length, out len) == 0 && len > 0)
                {
                    return new string(heapBuf.AsSpan(0, len));
                }
            }
        }
        catch (DllNotFoundException) { /* Fallback to canonical spec before native build */ }
        catch (EntryPointNotFoundException) { /* Fallback to canonical spec before native build */ }

        return DefaultFontSpec;
    }
}

[MarkupExtensionReturnType(typeof(FontFamily))]
public sealed class AppFont : MarkupExtension
{
    public string Fallbacks { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => AppFonts.Get(Fallbacks);
}
