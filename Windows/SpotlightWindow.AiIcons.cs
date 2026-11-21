using System.Windows.Media;

namespace VNotch;

public partial class SpotlightWindow
{
    public static Geometry AiBinGeometry { get; } = CreateAiSvgGeometry("M4.91666667,14.8888889 C4.91666667,15.3571429 5.60416667,16 6.0625,16 L12.9375,16 C13.3958333,16 14.0833333,15.3571429 14.0833333,14.8888889 L14.0833333,6 L4.91666667,6 L4.91666667,14.8888889 L4.91666667,14.8888889 L4.91666667,14.8888889 Z M15,3.46500003 L12.5555556,3.46500003 L11.3333333,2 L7.66666667,2 L6.44444444,3.46500003 L4,3.46500003 L4,4.93000007 L15,4.93000007 L15,3.46500003 L15,3.46500003 L15,3.46500003 Z");

    public static Geometry AiMagnifierGeometry { get; } = CreateAiSvgGeometry("M0.292,15.001l0.707,0.707c0.389,0.389,1.025,0.389,1.414,0l3.675-3.675C7.08,12.646,8.249,13,9.5,13 c3.59,0,6.5-2.91,6.5-6.5S13.09,0,9.5,0S3,2.91,3,6.5c0,1.251,0.354,2.42,0.967,3.412l-3.675,3.675 C-0.097,13.976-0.097,14.612,0.292,15.001z M9.5,11c-1.202,0-2.332-0.468-3.182-1.318C5.468,8.832,5,7.702,5,6.5 s0.468-2.332,1.318-3.182S8.298,2,9.5,2s2.332,0.468,3.182,1.318C13.532,4.168,14,5.298,14,6.5s-0.468,2.332-1.318,3.182 C11.832,10.532,10.702,11,9.5,11z");

    public static Geometry AiCopyGeometry { get; } = CreateAiSvgGeometry("M8 5h7.795c1.115 0 1.519.116 1.926.334.407.218.727.538.945.945.218.407.334.811.334 1.926V16a1 1 0 1 0 2 0V8.128c0-1.783-.186-2.43-.534-3.082a3.635 3.635 0 0 0-1.512-1.512C18.302 3.186 17.655 3 15.872 3H8a1 1 0 0 0 0 2zm7.721 2.334C15.314 7.116 14.91 7 13.795 7h-7.59c-1.115 0-1.519.116-1.926.334a2.272 2.272 0 0 0-.945.945C3.116 8.686 3 9.09 3 10.205v7.59c0 1.114.116 1.519.334 1.926.218.407.538.727.945.945.407.218.811.334 1.926.334h7.59c1.114 0 1.519-.116 1.926-.334.407-.218.727-.538.945-.945.218-.407.334-.811.334-1.926v-7.59c0-1.115-.116-1.519-.334-1.926a2.272 2.272 0 0 0-.945-.945z");

    public static Geometry AiArrowLeftGeometry { get; } = CreateArrowLeftGeometry();
    public static Geometry QuickActionOpenGeometry { get; } = FreezeSolidIcon(
        "F0 M5 4 H11 V7 H6.5 V17.5 H17 V13 H20 V19 Q20 21 18 21 H5 Q3 21 3 19 V6 Q3 4 5 4 Z M13 3 H21 V11 H18 V8.1 L11.6 14.5 L9.5 12.4 L15.9 6 H13 Z");
    public static Geometry QuickActionAdminGeometry { get; } = FreezeSolidIcon(
        "F0 M12 2 L20 5.2 V11.2 C20 16.5 16.6 20.9 12 22.5 C7.4 20.9 4 16.5 4 11.2 V5.2 Z M10.6 16.2 L16.8 10.0 L14.7 7.9 L10.6 12.0 L8.7 10.1 L6.6 12.2 Z");
    public static Geometry QuickActionFolderGeometry { get; } = FreezeSolidIcon(
        "F0 M4 4 H10 L12 6.5 H20 Q22 6.5 22 8.5 V18.5 Q22 20.5 20 20.5 H4 Q2 20.5 2 18.5 V6 Q2 4 4 4 Z M5 9.5 V12 H19 V9.5 Z");
    public static Geometry QuickActionCopyGeometry { get; } = FreezeSolidIcon(
        "F0 M5 2 H15 Q17 2 17 4 V6 H14 V5 H6 V15 H4 Q3 15 3 13 V4 Q3 2 5 2 Z M9 7 H19 Q21 7 21 9 V20 Q21 22 19 22 H9 Q7 22 7 20 V9 Q7 7 9 7 Z M10 11.5 V13.8 H18 V11.5 Z M10 16 V18.3 H15.5 V16 Z");
    public static Geometry QuickActionPinGeometry { get; } = FreezeSolidIcon(
        "F0 M7.5 2.5 H16.5 Q18 2.5 18 4 Q18 5.5 16.5 5.5 H15.5 V10.2 L18.5 13.2 Q19 13.7 19 14.5 V15.5 H13.5 V20.5 L12 22.5 L10.5 20.5 V15.5 H5 V14.5 Q5 13.7 5.5 13.2 L8.5 10.2 V5.5 H7.5 Q6 5.5 6 4 Q6 2.5 7.5 2.5 Z M10.5 5.5 V9.5 H13.5 V5.5 Z");
    public static Geometry QuickActionAliasGeometry { get; } = FreezeSolidIcon(
        "F0 M3 4.5 Q3 3 4.5 3 H11.8 Q12.8 3 13.5 3.7 L20.8 11.0 Q22 12.2 20.8 13.4 L13.4 20.8 Q12.2 22 11.0 20.8 L3.7 13.5 Q3 12.8 3 11.8 Z M7.8 6.2 A1.8 1.8 0 1 0 7.8 9.8 A1.8 1.8 0 1 0 7.8 6.2 Z");
    public static Geometry QuickActionExcludeFolderGeometry { get; } = FreezeSolidIcon(
        "F0 M4 4 H10 L12 6.5 H20 Q22 6.5 22 8.5 V18.5 Q22 20.5 20 20.5 H4 Q2 20.5 2 18.5 V6 Q2 4 4 4 Z M7.5 12.2 V15.0 H16.5 V12.2 Z");

    private static Geometry FreezeSolidIcon(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static Geometry CreateArrowLeftGeometry()
    {
        var source = Geometry.Parse("M5.79891 24L41.7989 24 M17.7988 36L5.79883 24L17.7988 12");
        var pen = new Pen(Brushes.Black, 6)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        var geometry = source.GetWidenedPathGeometry(pen);
        geometry.Freeze();
        return geometry;
    }

    private static Geometry CreateAiSvgGeometry(string path)
    {
        var geometry = Geometry.Parse("F0 " + path);
        // Thicken the silhouette in source coordinates; Path.Stretch keeps the UI size fixed.
        var outline = geometry.GetWidenedPathGeometry(new Pen(Brushes.Black, geometry.Bounds.Width * 0.035)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        });
        var bold = Geometry.Combine(geometry, outline, GeometryCombineMode.Union, null);
        bold.Freeze();
        return bold;
    }
}
