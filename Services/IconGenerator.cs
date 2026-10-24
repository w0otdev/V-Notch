using System.Drawing;
using System.Drawing.Drawing2D;

namespace VNotch.Services;

public static class IconGenerator
{
    public static Icon CreateNotchIcon(int size = 16)
    {
        using var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var brush = new SolidBrush(Color.FromArgb(30, 30, 30));
        using var path = new GraphicsPath();

        int radius = size / 4;
        var rect = new Rectangle(0, 0, size, size);

        path.AddLine(rect.Left, rect.Top, rect.Right, rect.Top);
        path.AddLine(rect.Right, rect.Top, rect.Right, rect.Bottom - radius);
        path.AddArc(rect.Right - radius * 2, rect.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddLine(rect.Right - radius, rect.Bottom, rect.Left + radius, rect.Bottom);
        path.AddArc(rect.Left, rect.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        path.AddLine(rect.Left, rect.Bottom - radius, rect.Left, rect.Top);
        path.CloseFigure();

        g.FillPath(brush, path);

        int dotSize = size / 4;
        int dotX = (size - dotSize) / 2;
        int dotY = (size - dotSize) / 2 - 1;
        using var dotBrush = new SolidBrush(Color.FromArgb(60, 60, 60));
        g.FillEllipse(dotBrush, dotX, dotY, dotSize, dotSize);

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using var tempIcon = Icon.FromHandle(hIcon);
            return (Icon)tempIcon.Clone();
        }
        finally
        {
            Win32Interop.DestroyIcon(hIcon);
        }
    }
}
