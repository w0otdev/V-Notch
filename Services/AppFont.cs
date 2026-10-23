using System.Collections.Concurrent;
using System.IO;
using System.Windows.Markup;
using System.Windows.Media;

namespace VNotch.Services;

// Fonts are deployment content, so loading a face no longer pages the font bundle
// into the main assembly. Absolute file URIs also work from tests and setup hosts.
public static class AppFonts
{
    public static Uri BaseUri { get; } = new(Path.Combine(AppContext.BaseDirectory, "Fonts") + Path.DirectorySeparatorChar);
    public static string Location => BaseUri.AbsoluteUri;
    private static readonly ConcurrentDictionary<string, FontFamily> Families = new(StringComparer.Ordinal);

    public static FontFamily Get(string fallbacks = "") => Families.GetOrAdd(fallbacks,
        static value => new FontFamily(BaseUri, "./#SF Pro Display" + (value.Length > 0 ? ", " + value : "")));
}

[MarkupExtensionReturnType(typeof(FontFamily))]
public sealed class AppFont : MarkupExtension
{
    public string Fallbacks { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => AppFonts.Get(Fallbacks);
}
