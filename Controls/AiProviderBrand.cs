using System.Windows.Media;
using System.Xml;

namespace VNotch.Controls;

// Source geometry and attribution are recorded in Assets/Brands/sources.json.
internal static class AiProviderBrand
{
    private sealed record Brand(ImageSource Image, Geometry Geometry);
    private static readonly Lazy<Brand> Claude = new(() => Load("claude"));
    private static readonly Lazy<Brand> Codex = new(() => Load("codex"));
    private static readonly Lazy<Brand> OpenAI = new(() => Load("openai"));
    private static readonly Lazy<Brand> Gemini = new(() => Load("gemini"));
    private static readonly Lazy<Brand> DeepSeek = new(() => Load("deepseek"));
    private static readonly Brand Neutral = CreateNeutral();

    internal static ImageSource For(string provider) => Get(provider).Image;
    internal static Geometry GeometryFor(string provider) => Get(provider).Geometry;

    private static Brand Get(string provider) => provider.Trim().ToUpperInvariant() switch
    {
        "CLAUDE" => Claude.Value,
        "CODEX" => Codex.Value,
        "OPENAI" => OpenAI.Value,
        "GEMINI" => Gemini.Value,
        "DEEPSEEK" => DeepSeek.Value,
        _ => Neutral
    };

    private static Brand Load(string name)
    {
        using var stream = typeof(AiProviderBrand).Assembly.GetManifestResourceStream("VNotch.Brands." + name + ".svg")
            ?? throw new InvalidOperationException("Missing bundled AI provider logo.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32768 });
        var drawing = new DrawingGroup();
        var shapes = new GeometryGroup();
        string? inheritedFillRule = null;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "svg")
                inheritedFillRule = reader.GetAttribute("fill-rule");
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "path" || reader.GetAttribute("d") is not { } path) continue;
            var geometry = Geometry.Parse(((reader.GetAttribute("fill-rule") ?? inheritedFillRule) == "evenodd" ? "F0 " : "F1 ") + path);
            drawing.Children.Add(new GeometryDrawing(Brushes.White, null, geometry));
            shapes.Children.Add(geometry);
        }
        drawing.Freeze();
        shapes.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return new Brand(image, shapes.Children.Count == 1 ? shapes.Children[0] : shapes);
    }

    private static Brand CreateNeutral()
    {
        var geometry = Geometry.Parse("M4 3 H20 Q22 3 22 5 V16 Q22 18 20 18 H10 L5 22 V18 H4 Q2 18 2 16 V5 Q2 3 4 3 Z");
        geometry.Freeze();
        var image = new DrawingImage(new GeometryDrawing(Brushes.White, null, geometry));
        image.Freeze();
        return new Brand(image, geometry);
    }
}
