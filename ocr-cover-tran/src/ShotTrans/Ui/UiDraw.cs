using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ShotTrans.Ui;

internal static class UiDraw
{
    public static Font CreateFont(float pixelSize, FontStyle style = FontStyle.Regular)
    {
        try { return new Font("Microsoft YaHei UI", pixelSize, style, GraphicsUnit.Pixel); }
        catch { return new Font(FontFamily.GenericSansSerif, pixelSize, style, GraphicsUnit.Pixel); }
    }

    /// <summary>按显示器缩放创建字号；pixelSize 为 96 DPI 下的逻辑像素。</summary>
    public static Font CreateScaledFont(float pixelSize, float scale, FontStyle style = FontStyle.Regular)
        => CreateFont(Math.Max(1f, pixelSize * scale), style);

    public static int ScalePx(int value, float scale) => Math.Max(1, (int)Math.Round(value * scale));

    public static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static string TruncateToHeight(Graphics g, string text, Font font, float width, float maxHeight)
    {
        var measured = g.MeasureString(text, font, new SizeF(width, 100000f));
        if (measured.Height <= maxHeight) return text;

        int lo = 0, hi = text.Length;
        string best = "…";
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            string candidate = text[..mid].TrimEnd() + "…";
            var m = g.MeasureString(candidate, font, new SizeF(width, 100000f));
            if (m.Height <= maxHeight)
            {
                best = candidate;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return best;
    }

    public static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }
}
