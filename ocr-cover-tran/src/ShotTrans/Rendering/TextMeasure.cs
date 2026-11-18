using System.Drawing;

namespace ShotTrans.Rendering;

/// <summary>逐字符测量文本的包围盒，供鼠标选中使用。</summary>
internal static class TextMeasure
{
    public static RectangleF[] CharacterRects(Graphics g, string text, Font font, RectangleF rect, StringFormat format)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<RectangleF>();
        try
        {
            var ranges = new CharacterRange[text.Length];
            for (int i = 0; i < text.Length; i++) ranges[i] = new CharacterRange(i, 1);
            format.SetMeasurableCharacterRanges(ranges);
            var regions = g.MeasureCharacterRanges(text, font, rect, format);
            var result = new RectangleF[text.Length];
            for (int i = 0; i < result.Length && i < regions.Length; i++)
            {
                result[i] = regions[i].GetBounds(g);
                regions[i].Dispose();
            }
            return result;
        }
        catch
        {
            return Array.Empty<RectangleF>();
        }
    }
}
