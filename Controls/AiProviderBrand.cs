using System.Windows.Media;
using System.Xml;

namespace VNotch.Controls;

// Source geometry and attribution are recorded in Assets/Brands/sources.json.
internal static class AiProviderBrand
{
    private static readonly ImageSource Claude = Load("claude");
    private static readonly ImageSource Codex = Load("codex");
    internal static ImageSource For(string provider) => provider == "Claude" ? Claude : Codex;

    private static ImageSource Load(string name)
    {
        using var stream = typeof(AiProviderBrand).Assembly.GetManifestResourceStream("VNotch.Brands." + name + ".svg")
            ?? throw new InvalidOperationException("Missing bundled AI provider logo.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32768 });
        var drawing = new DrawingGroup();
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "path" || reader.GetAttribute("d") is not { } path) continue;
            var geometry = Geometry.Parse((reader.GetAttribute("fill-rule") == "evenodd" || name == "codex" ? "F0 " : "F1 ") + path);
            drawing.Children.Add(new GeometryDrawing(Brushes.White, null, geometry));
        }
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
