using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Models;

namespace VNotch.Controls;

public enum TrayAppIconKind { Notes, Finder, Safari, Chrome, Photos, Colors, Figma, Terminal, Document, Pdf, Archive, Audio, Video, Folder }

/// <summary>Official app artwork and shared macOS-style vectors for tray file previews.</summary>
public static partial class TrayAppIcons
{
    [LibraryImport("VNotch.Native", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_tray_icons_match_source(
        ReadOnlySpan<char> appPtr, int appLen,
        ReadOnlySpan<char> exePtr, int exeLen,
        out int outIconKind, out byte outMatched);

    [LibraryImport("VNotch.Native", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_tray_icons_match_extension(
        ReadOnlySpan<char> extPtr, int extLen,
        out int outIconKind, out byte outMatched);

    [LibraryImport("VNotch.Native")]
    private static partial int vn_tray_icons_for_clipboard_kind(
        int clipboardKind, out int outIconKind);

    [LibraryImport("VNotch.Native")]
    private static partial int vn_tray_icons_compute_color_sector(
        int index, int count, double cx, double cy, double radius,
        out double outX1, out double outY1, out double outX2, out double outY2);

    private static readonly IReadOnlyDictionary<TrayAppIconKind, Lazy<ImageSource>> Icons = CreateIcons();

    public static ImageSource Get(TrayAppIconKind kind) => Icons[kind].Value;

    internal static ImageSource? FindSource(string app, string executable)
    {
        ReadOnlySpan<char> appSpan = app is not null ? app.AsSpan() : ReadOnlySpan<char>.Empty;
        ReadOnlySpan<char> exeSpan = executable is not null ? executable.AsSpan() : ReadOnlySpan<char>.Empty;
        return vn_tray_icons_match_source(appSpan, appSpan.Length, exeSpan, exeSpan.Length, out int kind, out byte matched) == 0 && matched != 0
            ? Get((TrayAppIconKind)kind)
            : null;
    }

    internal static ImageSource ForContent(ClipboardKind kind)
        => vn_tray_icons_for_clipboard_kind((int)kind, out int iconKind) == 0
            ? Get((TrayAppIconKind)iconKind)
            : Get(TrayAppIconKind.Notes);

    internal static ImageSource? FindFile(string extension)
    {
        ReadOnlySpan<char> extSpan = extension is not null ? extension.AsSpan() : ReadOnlySpan<char>.Empty;
        return vn_tray_icons_match_extension(extSpan, extSpan.Length, out int kind, out byte matched) == 0 && matched != 0
            ? Get((TrayAppIconKind)kind)
            : null;
    }

    // First card realization needs only its source/file icons. Decode each shared
    // asset on demand instead of loading the whole pack on that first UI frame.
    private static IReadOnlyDictionary<TrayAppIconKind, Lazy<ImageSource>> CreateIcons() => new Dictionary<TrayAppIconKind, Lazy<ImageSource>>
    {
        [TrayAppIconKind.Notes] = new(() => Load("notes.png")),
        [TrayAppIconKind.Finder] = new(() => Load("finder.png")),
        [TrayAppIconKind.Safari] = new(() => Load("safari.png")),
        [TrayAppIconKind.Chrome] = new(() => Load("chrome.png")),
        [TrayAppIconKind.Photos] = new(() => Load("photos.png")),
        [TrayAppIconKind.Colors] = new(() => Draw(dc =>
        {
            string[] colors = ["#FF4C5B", "#FF9C35", "#FFE44A", "#75D354", "#48D5C4", "#3FA8FF", "#7764E8", "#DF58B4"];
            for (int i = 0; i < colors.Length; i++)
            {
                if (vn_tray_icons_compute_color_sector(i, colors.Length, 32, 32, 28, out double x1, out double y1, out double x2, out double y2) != 0)
                    continue;
                var sector = new StreamGeometry();
                using (var shape = sector.Open())
                {
                    shape.BeginFigure(new Point(32, 32), true, true);
                    shape.LineTo(new Point(x1, y1), true, false);
                    shape.ArcTo(new Point(x2, y2), new Size(28, 28), 0, false, SweepDirection.Clockwise, true, false);
                }
                dc.DrawGeometry(Brush(colors[i]), null, sector);
            }
            dc.DrawEllipse(Gradient("#FFFFFF", "#DDE2EA"), new Pen(Brush("#44FFFFFF"), 1), new Point(32, 32), 11, 11);
        })),
        [TrayAppIconKind.Figma] = new(() => Draw(dc =>
        {
            // Verbatim geometry and colors from Assets/TrayIcons/figma.svg.
            Fill(dc, "#24CB71", "M11.9999 52.0001C11.9999 46.4772 16.477 42.0001 21.9999 42.0001H31.9999V52.0001C31.9999 57.5229 27.5227 62.0001 21.9999 62.0001V62.0001C16.477 62.0001 11.9999 57.5229 11.9999 52.0001V52.0001Z");
            Fill(dc, "#FF7237", "M32 2V22H42C47.5228 22 52 17.5228 52 12V12C52 6.47715 47.5228 2 42 2L32 2Z");
            dc.DrawEllipse(Brush("#00B6FF"), null, new Point(41.9166, 31.9999), 10, 10);
            Fill(dc, "#FF3737", "M11.9999 11.9999C11.9999 17.5228 16.477 21.9999 21.9999 21.9999L31.9999 21.9999L31.9999 1.99994L21.9999 1.99994C16.477 1.99994 11.9999 6.47709 11.9999 11.9999V11.9999Z");
            Fill(dc, "#874FFF", "M11.9999 32.0001C11.9999 37.5229 16.477 42.0001 21.9999 42.0001H31.9999L31.9999 22.0001L21.9999 22.0001C16.477 22.0001 11.9999 26.4772 11.9999 32.0001V32.0001Z");
        })),
        [TrayAppIconKind.Terminal] = new(() => Load("terminal.png")),
        [TrayAppIconKind.Document] = new(() => Draw(dc =>
        {
            Paper(dc);
            for (int y = 27; y <= 45; y += 6) Line(dc, "#A3ADB9", 2, $"M17,{y} L47,{y}");
            Line(dc, "#A3ADB9", 2, "M17,51 L36,51");
        })),
        [TrayAppIconKind.Pdf] = new(() => Draw(dc =>
        {
            Paper(dc);
            Line(dc, "#E34A42", 2.5, "M20,46 C36,16 36,21 31,35 C28,46 17,45 23,39 C30,32 48,34 45,40 C43,44 28,33 30,22");
        })),
        [TrayAppIconKind.Archive] = new(() => Draw(dc =>
        {
            Paper(dc);
            dc.DrawRectangle(Brush("#9C8770"), null, new Rect(27, 5, 8, 40));
            for (int y = 6; y < 40; y += 6) dc.DrawRectangle(Brush("#EEE7D9"), null, new Rect(y % 12 == 6 ? 27 : 31, y, 4, 3));
            dc.DrawRoundedRectangle(Brush("#C5B69A"), new Pen(Brush("#807361"), 1.5), new Rect(26, 40, 10, 11), 3, 3);
        })),
        [TrayAppIconKind.Audio] = new(() => Draw(dc =>
        {
            Tile(dc, "#FF7687", "#E12E57");
            Fill(dc, "#FFFFFF", "M29,17 L48,13 L48,41 C48,48 36,51 36,44 C36,40 42,38 45,39 L45,23 L32,26 L32,46 C32,53 20,56 20,49 C20,45 26,43 29,44 Z");
        })),
        [TrayAppIconKind.Video] = new(() => Draw(dc =>
        {
            Tile(dc, "#BB86F7", "#6851BD");
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(12, 19, 28, 26), 5, 5);
            Fill(dc, "#FFFFFF", "M42,26 L52,20 L52,44 L42,38 Z");
        })),
        [TrayAppIconKind.Folder] = new(() => Draw(dc =>
        {
            Fill(dc, "#5AB7EE", "M5,16 Q5,12 9,12 L26,12 L32,18 L55,18 Q59,18 59,22 L59,51 Q59,55 55,55 L9,55 Q5,55 5,51 Z");
            dc.DrawRoundedRectangle(Gradient("#92DBFF", "#3A9EDD"), new Pen(Brush("#2C90C9"), 1), new Rect(5, 22, 54, 33), 4, 4);
        }))
    };

    private const string TrayIconAssetPrefix = "pack://application:,,,/V-Notch;component/Assets/TrayIcons/";

    private static BitmapImage Load(string name)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(string.Concat(TrayIconAssetPrefix, name));
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static DrawingImage Draw(Action<DrawingContext> paint)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            // A shared viewport preserves optical padding in every image size.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 64, 64));
            paint(dc);
        }
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private static SolidColorBrush Brush(string color, double opacity = 1)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Gradient(string top, string bottom)
    {
        var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom), 90);
        brush.Freeze();
        return brush;
    }

    private static void Tile(DrawingContext dc, string top, string bottom)
        => dc.DrawRoundedRectangle(Gradient(top, bottom), new Pen(Brush("#22000000"), 0.7), new Rect(3, 3, 58, 58), 13, 13);

    private static void Paper(DrawingContext dc)
    {
        dc.DrawGeometry(Gradient("#FFFFFF", "#E6EBF0"), new Pen(Brush("#B6C0CB"), 1), Geometry.Parse("M14,4 L39,4 L52,17 L52,57 Q52,60 49,60 L14,60 Q11,60 11,57 L11,7 Q11,4 14,4 Z"));
        Fill(dc, "#D1D9E2", "M39,4 L39,17 L52,17 Z");
    }

    private static void Fill(DrawingContext dc, string color, string path) => dc.DrawGeometry(Brush(color), null, Geometry.Parse(path));

    private static void Line(DrawingContext dc, string color, double width, string path)
        => dc.DrawGeometry(null, new Pen(Brush(color), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Geometry.Parse(path));
}
