using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

internal static class ArtworkFrameDetector
{
    internal static Int32Rect? DetectFrame(BitmapSource source) =>
        DetectWhiteFrame(source) ?? DetectDarkTexturedFrame(source);

    // Album art can be inset into textured video backgrounds. Require four long,
    // straight transitions into a near-square picture rather than trimming darkness.
    internal static Int32Rect? DetectDarkTexturedFrame(BitmapSource source)
    {
        int width = source.PixelWidth, height = source.PixelHeight;
        if (Math.Min(width, height) < 64) return null;
        double scale = Math.Min(1, 384.0 / Math.Max(width, height));
        BitmapSource sample = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        sample = new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0);
        int w = sample.PixelWidth, h = sample.PixelHeight;
        byte[] pixels = new byte[w * h * 4];
        sample.CopyPixels(pixels, w * 4, 0);

        double Brightness(int x, int y)
        {
            int i = (y * w + x) * 4;
            return (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3.0;
        }

        double EdgeSupport(int position, bool row, int direction, int start, int end)
        {
            int supported = 0;
            for (int p = start; p < end; p++)
            {
                double inside = Brightness(row ? p : position + direction * 2, row ? position + direction * 2 : p);
                double outside = Brightness(row ? p : position - direction * 2, row ? position - direction * 2 : p);
                if (outside <= 85 && inside - outside >= 30) supported++;
            }
            return supported / (double)(end - start);
        }

        int[] Candidates(bool row, int direction)
        {
            int length = row ? h : w, across = row ? w : h;
            int start = direction > 0 ? Math.Max(3, length / 25) : length * 3 / 5;
            int end = direction > 0 ? length * 2 / 5 : Math.Min(length - 3, length * 24 / 25);
            var scores = new List<(int Position, double Score)>();
            for (int p = start; p < end; p++)
                scores.Add((p, EdgeSupport(p, row, direction, across * 2 / 5, across * 3 / 5)));
            var selected = new List<int>();
            foreach (var candidate in scores.OrderByDescending(s => s.Score))
            {
                if (candidate.Score < .8) break;
                if (selected.All(p => Math.Abs(p - candidate.Position) > 3)) selected.Add(candidate.Position);
                if (selected.Count == 4) break;
            }
            return selected.ToArray();
        }

        var lefts = Candidates(false, 1);
        var rights = Candidates(false, -1);
        var tops = Candidates(true, 1);
        var bottoms = Candidates(true, -1);
        Int32Rect? best = null;
        double bestScore = 0;
        foreach (int left in lefts)
            foreach (int right in rights)
                foreach (int top in tops)
                    foreach (int bottom in bottoms)
                    {
                        int cw = right - left, ch = bottom - top;
                        double aspect = cw / (double)ch;
                        if (aspect < .85 || aspect > 1.15 || cw < w * .3 || ch < h * .45) continue;
                        double[] supports =
                        {
                EdgeSupport(left, false, 1, top + 3, bottom - 3),
                EdgeSupport(right, false, -1, top + 3, bottom - 3),
                EdgeSupport(top, true, 1, left + 3, right - 3),
                EdgeSupport(bottom, true, -1, left + 3, right - 3)
            };
                        if (supports.Min() < .85) continue;
                        double score = supports.Average() + .05 * cw * ch / (w * h);
                        if (score <= bestScore) continue;
                        // Move beyond the sampled transition so interpolation cannot retain the mat.
                        int x = (int)Math.Ceiling((left + 3.0) * width / w);
                        int y = (int)Math.Ceiling((top + 3.0) * height / h);
                        int x2 = (int)Math.Floor((right - 3.0) * width / w);
                        int y2 = (int)Math.Floor((bottom - 3.0) * height / h);
                        best = new Int32Rect(x, y, x2 - x, y2 - y);
                        bestScore = score;
                    }
        return best;
    }

    // Detect a light mat on all four sides, not white objects within the artwork.
    internal static Int32Rect? DetectWhiteFrame(BitmapSource source)
    {
        int width = source.PixelWidth, height = source.PixelHeight;
        if (Math.Min(width, height) < 64) return null;
        double scale = Math.Min(1, 384.0 / Math.Max(width, height));
        BitmapSource sample = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        sample = new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0);
        int w = sample.PixelWidth, h = sample.PixelHeight;
        byte[] pixels = new byte[w * h * 4];
        sample.CopyPixels(pixels, w * 4, 0);

        bool IsWhite(int x, int y)
        {
            int i = (y * w + x) * 4;
            int min = Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]));
            int max = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
            return pixels[i + 3] >= 240 && min >= 225 && max - min <= 24;
        }

        // Ignore a narrow outer decoration or black video edge; tolerate sparse ornaments.
        int padX = Math.Max(1, (int)Math.Ceiling(w * .02));
        int padY = Math.Max(1, (int)Math.Ceiling(h * .02));
        bool IsMatLine(int position, bool row)
        {
            int start = row ? padX : padY;
            int end = row ? w - padX : h - padY;
            int white = 0;
            for (int p = start; p < end; p++)
                if (IsWhite(row ? p : position, row ? position : p)) white++;
            return white >= (end - start) * .85;
        }

        int left = padX, right = w - padX - 1, top = padY, bottom = h - padY - 1;
        while (left < w / 3 && IsMatLine(left, false)) left++;
        while (right > w * 2 / 3 && IsMatLine(right, false)) right--;
        while (top < h / 3 && IsMatLine(top, true)) top++;
        while (bottom > h * 2 / 3 && IsMatLine(bottom, true)) bottom--;
        if (left <= padX || right >= w - padX - 1 || top <= padY || bottom >= h - padY - 1 ||
            left >= w / 3 || right <= w * 2 / 3 || top >= h / 3 || bottom <= h * 2 / 3)
            return null;

        // Each inner edge must belong predominantly to the picture. This rejects
        // irregular white backgrounds and avoids following a white central subject.
        bool HasPictureEdge(int position, bool row)
        {
            int start = row ? left : top, end = row ? right : bottom;
            int content = 0;
            for (int p = start; p <= end; p++)
                if (!IsWhite(row ? p : position, row ? position : p)) content++;
            return content >= (end - start + 1) * .7;
        }
        if (!HasPictureEdge(left, false) || !HasPictureEdge(right, false) ||
            !HasPictureEdge(top, true) || !HasPictureEdge(bottom, true)) return null;

        // One analysis pixel of overscan removes resampling/JPEG fringe at the boundary.
        int x = (int)Math.Ceiling((left + 1.0) * width / w);
        int y = (int)Math.Ceiling((top + 1.0) * height / h);
        int x2 = (int)Math.Floor(right * (double)width / w);
        int y2 = (int)Math.Floor(bottom * (double)height / h);
        return new Int32Rect(x, y, x2 - x, y2 - y);
    }
}
