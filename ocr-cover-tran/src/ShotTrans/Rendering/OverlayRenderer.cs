using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using ShotTrans.Ocr;

namespace ShotTrans.Rendering;

internal sealed class LaidOutBlock : IDisposable
{
    public Rectangle Rect { get; init; }
    public Color Background { get; init; }
    public Color Foreground { get; init; }
    public required Font Font { get; init; }
    public required string RenderedText { get; init; }
    public required RectangleF[] CharRects { get; init; }
    public bool Center { get; init; }
    public int PadX { get; init; } = 3;
    public int PadY { get; init; } = 1;

    public void Dispose() => Font.Dispose();
}

internal sealed class OverlayLayout : IDisposable
{
    public required List<LaidOutBlock> Blocks { get; init; }
    public required List<Rectangle> ScreenHits { get; init; }

    public void Dispose()
    {
        foreach (var b in Blocks) b.Dispose();
    }
}

/// <summary>译文覆盖层的排版与绘制（GDI+ 逐像素）。</summary>
internal sealed class OverlayRenderer : IDisposable
{
    // 96 DPI 下的可读字号下限；实际按显示器 DPI 放大，避免 150% 屏幕上译文仍过小。
    private const int ReadableMinPx = 12;
    private const int CardGap = 2;

    private readonly Bitmap _screenshot;
    private readonly AppConfig _config;
    private readonly Color[] _backgrounds;
    private readonly Rectangle[] _localRects;
    private readonly int _width;
    private readonly int _height;
    private readonly float _dpiScale;

    public OverlayRenderer(Bitmap screenshot, IReadOnlyList<OcrLineBox> blocks, AppConfig config, float dpiScale = 1f)
    {
        _screenshot = screenshot;
        _config = config;
        _width = screenshot.Width;
        _height = screenshot.Height;
        _dpiScale = dpiScale > 0 ? dpiScale : 1f;
        _backgrounds = new Color[blocks.Count];
        _localRects = new Rectangle[blocks.Count];

        using var sampler = new BitmapSampler(screenshot);
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i].Bounds;
            b.Inflate(1, 1);
            b.Intersect(new Rectangle(0, 0, _width, _height));
            _localRects[i] = b;
            _backgrounds[i] = sampler.SampleDominantColor(b);
        }
    }

    public Font? BarFont { get; private set; }

    /// <summary>
    /// 排版。allowedBottom 为截图坐标下的可用下边界（可大于截图高度，用于让卡片向下生长）。
    /// </summary>
    public OverlayLayout BuildLayout(
        IReadOnlyList<OcrLineBox> blocks,
        IReadOnlyList<string> texts,
        bool center,
        int allowedBottom)
    {
        int bottomLimit = Math.Max(1, allowedBottom);
        using var g = Graphics.FromImage(_screenshot);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        float scale = 1f;
        var specs = BuildSpecs(g, blocks, texts, center, scale);
        Place(specs);

        // 整体仍超出可用高度时，逐轮缩小字号；仍不行则在收尾时钳制并截断。
        for (int attempt = 0; attempt < 3 && LowestBottom(specs) > bottomLimit; attempt++)
        {
            int top = specs.Count > 0 ? specs.Min(s => s.Card.Top) : 0;
            int required = Math.Max(1, LowestBottom(specs) - top);
            int available = Math.Max(1, bottomLimit - top);
            float ratio = Math.Clamp((float)available / required, 0.6f, 0.97f);
            float nextScale = Math.Max(0.5f, scale * ratio);
            if (scale - nextScale < 0.01f) break;

            var next = BuildSpecs(g, blocks, texts, center, nextScale);
            Place(next);
            DisposeFonts(specs);
            specs = next;
            scale = nextScale;
        }

        ClampToBottom(specs, bottomLimit);
        return FinalizeLayout(g, specs);
    }

    private List<Spec> BuildSpecs(
        Graphics g,
        IReadOnlyList<OcrLineBox> blocks,
        IReadOnlyList<string> texts,
        bool center,
        float scale)
    {
        var specs = new List<Spec>(blocks.Count);

        int min = Math.Clamp(_config.FontMinSize, 6, 40);
        int max = Math.Clamp(_config.FontMaxSize, min, 48);
        int readableFloor = Math.Clamp(
            Math.Max((int)Math.Round(ReadableMinPx * _dpiScale), min), min, max);
        int scaledFloor = Math.Clamp((int)Math.Round(readableFloor * scale), min, max);
        int scaledMax = Math.Clamp((int)Math.Round(max * scale), min, max);

        for (int i = 0; i < blocks.Count; i++)
        {
            var rect = _localRects[i];
            if (rect.Width < 3 || rect.Height < 3) continue;
            string text = i < texts.Count ? texts[i] ?? "" : blocks[i].Text;
            if (string.IsNullOrWhiteSpace(text)) continue;

            int padX = Math.Clamp(rect.Width / 12, 2, 6);
            const int padY = 2;
            float layoutW = Math.Max(1, rect.Width - padX * 2);
            float layoutH = Math.Max(1, rect.Height - padY * 2);
            using var format = CreateFormat(center);

            // 先找能塞进原始矩形的最大字号（保持"小文本填满框"）。
            Font? chosen = null;
            int chosenSize = 0;
            for (int size = scaledMax; size >= min; size--)
            {
                var font = CreateFont(size);
                var measured = g.MeasureString(text, font, new SizeF(layoutW, 100000f), format);
                if (measured.Height <= layoutH + 0.5f && measured.Width <= layoutW + 1.5f)
                {
                    chosen = font;
                    chosenSize = size;
                    break;
                }
                font.Dispose();
            }

            Rectangle card;
            if (chosen != null && chosenSize >= scaledFloor)
            {
                card = rect;
            }
            else
            {
                // 放不下或会小于可读下限：保持下限字号，卡片向下长高。
                chosen?.Dispose();
                chosenSize = scaledFloor;
                chosen = CreateFont(chosenSize);
                var measured = g.MeasureString(text, chosen, new SizeF(layoutW, 100000f), format);
                int needed = (int)Math.Ceiling(measured.Height) + padY * 2 + 2;
                card = new Rectangle(rect.X, rect.Y, rect.Width, Math.Max(rect.Height, needed));
            }

            specs.Add(new Spec
            {
                Text = text,
                Font = chosen,
                Card = card,
                Background = _backgrounds[i],
                Foreground = TextColorFor(_backgrounds[i]),
                Center = center,
                PadX = padX,
                PadY = padY
            });
        }

        return specs;
    }

    private static void Place(List<Spec> specs)
    {
        var ordered = specs
            .OrderBy(s => s.Card.Top)
            .ThenBy(s => s.Card.Left)
            .ToList();
        var placed = new List<Spec>(ordered.Count);

        foreach (var spec in ordered)
        {
            int top = spec.Card.Top;
            bool moved = true;
            int guard = 0;
            while (moved && guard++ < 256)
            {
                moved = false;
                foreach (var other in placed)
                {
                    // 只处理水平方向有交叠的卡片；左右并排的多列块互不影响。
                    if (spec.Card.Right <= other.Card.Left || spec.Card.Left >= other.Card.Right) continue;
                    int bottom = top + spec.Card.Height;
                    if (top < other.Card.Bottom + CardGap && bottom > other.Card.Top - CardGap)
                    {
                        int pushed = other.Card.Bottom + CardGap;
                        if (pushed > top)
                        {
                            top = pushed;
                            moved = true;
                        }
                    }
                }
            }

            spec.Card = new Rectangle(spec.Card.X, top, spec.Card.Width, spec.Card.Height);
            placed.Add(spec);
        }

    }

    private static void ClampToBottom(List<Spec> specs, int bottomLimit)
    {
        foreach (var spec in specs)
        {
            if (spec.Card.Bottom <= bottomLimit) continue;
            int height = Math.Max(1, bottomLimit - spec.Card.Top);
            spec.Card = new Rectangle(spec.Card.X, spec.Card.Top, spec.Card.Width, height);
        }
    }

    private OverlayLayout FinalizeLayout(Graphics g, List<Spec> specs)
    {
        var laid = new List<LaidOutBlock>(specs.Count);
        var hits = new List<Rectangle>(specs.Count);

        foreach (var spec in specs)
        {
            float w = Math.Max(1, spec.Card.Width - spec.PadX * 2);
            float h = Math.Max(1, spec.Card.Height - spec.PadY * 2);
            using var format = CreateFormat(spec.Center);

            var measured = g.MeasureString(spec.Text, spec.Font, new SizeF(w, 100000f), format);
            string rendered = measured.Height <= h + 0.5f
                ? spec.Text
                : TruncateToFit(g, spec.Text, spec.Font, w, h, format);

            var textRect = new RectangleF(spec.Card.X + spec.PadX, spec.Card.Y + spec.PadY, w, h);
            var charRects = TextMeasure.CharacterRects(g, rendered, spec.Font, textRect, format);

            laid.Add(new LaidOutBlock
            {
                Rect = spec.Card,
                Background = spec.Background,
                Foreground = spec.Foreground,
                Font = spec.Font,
                RenderedText = rendered,
                CharRects = charRects,
                Center = spec.Center,
                PadX = spec.PadX,
                PadY = spec.PadY
            });
            hits.Add(spec.Card);
        }

        return new OverlayLayout { Blocks = laid, ScreenHits = hits };
    }

    private static int LowestBottom(List<Spec> specs)
    {
        int lowest = 0;
        foreach (var spec in specs)
            if (spec.Card.Bottom > lowest) lowest = spec.Card.Bottom;
        return lowest;
    }

    private static void DisposeFonts(List<Spec> specs)
    {
        foreach (var spec in specs) spec.Font.Dispose();
    }

    private static StringFormat CreateFormat(bool center) => new()
    {
        Alignment = center ? StringAlignment.Center : StringAlignment.Near,
        LineAlignment = StringAlignment.Center,
        Trimming = StringTrimming.None,
        FormatFlags = StringFormatFlags.MeasureTrailingSpaces
    };

    private Font CreateFont(int pixelSize)
    {
        string family = string.IsNullOrWhiteSpace(_config.FontFamily) ? "Microsoft YaHei UI" : _config.FontFamily;
        try { return new Font(family, pixelSize, FontStyle.Regular, GraphicsUnit.Pixel); }
        catch { return new Font("Segoe UI", pixelSize, FontStyle.Regular, GraphicsUnit.Pixel); }
    }

    private static string TruncateToFit(Graphics g, string text, Font font, float width, float height, StringFormat format)
    {
        bool Fits(string s)
        {
            var m = g.MeasureString(s, font, new SizeF(width, 100000f), format);
            return m.Height <= height + 0.5f && m.Width <= width + 1.5f;
        }

        if (Fits(text)) return text;
        int lo = 0, hi = text.Length;
        string best = "…";
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            string candidate = text[..mid].TrimEnd() + "…";
            if (Fits(candidate))
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

    public void DrawBlocks(Graphics g, OverlayLayout layout, IReadOnlyDictionary<int, (int Start, int End)>? selection = null)
    {
        for (int blockIndex = 0; blockIndex < layout.Blocks.Count; blockIndex++)
        {
            var b = layout.Blocks[blockIndex];
            int alpha = (int)Math.Round(Math.Clamp(_config.BackgroundOpacity, 0.3, 1.0) * 255);
            var bg = Color.FromArgb(alpha, b.Background.R, b.Background.G, b.Background.B);
            var radius = Math.Min(5f, b.Rect.Height / 3f);

            using (var path = RoundedRect(new RectangleF(b.Rect.X, b.Rect.Y, b.Rect.Width, b.Rect.Height), radius))
            using (var brush = new SolidBrush(bg))
                g.FillPath(brush, path);

            if (b.Rect.Width > 8 && b.Rect.Height > 8)
            {
                using var border = new Pen(Color.FromArgb(38, b.Foreground), 1f);
                using var path = RoundedRect(new RectangleF(b.Rect.X + 0.5f, b.Rect.Y + 0.5f,
                    b.Rect.Width - 1, b.Rect.Height - 1), radius);
                g.DrawPath(border, path);
            }

            var state = g.Save();
            g.SetClip(b.Rect);

            if (selection != null && selection.TryGetValue(blockIndex, out var range) && b.CharRects.Length > 0)
            {
                int start = Math.Clamp(range.Start, 0, b.CharRects.Length - 1);
                int end = Math.Clamp(range.End, 0, b.CharRects.Length - 1);
                using var highlight = new SolidBrush(Color.FromArgb(95, 80, 150, 235));
                for (int c = start; c <= end; c++)
                {
                    var r = b.CharRects[c];
                    if (r.Width > 0.1f && r.Height > 0.1f)
                        g.FillRectangle(highlight, r);
                }
            }

            using (var textBrush = new SolidBrush(b.Foreground))
            using (var format = CreateFormat(b.Center))
            {
                var textRect = new RectangleF(
                    b.Rect.X + b.PadX,
                    b.Rect.Y + b.PadY,
                    Math.Max(1, b.Rect.Width - b.PadX * 2),
                    Math.Max(1, b.Rect.Height - b.PadY * 2));
                g.DrawString(b.RenderedText, b.Font, textBrush, textRect, format);
            }
            g.Restore(state);
        }
    }

    public void DrawStatusBar(Graphics g, int width, string text, bool error, double alpha = 1.0, float y = 6, bool dark = true)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        if (alpha <= 0.01) return;

        float scale = _dpiScale;
        BarFont ??= CreateFont(Math.Max(9, (int)Math.Round(12 * scale)));
        float padX = 13 * scale;
        float pillHeight = 28 * scale;
        float maxWidth = Math.Max(40 * scale, width - 16 * scale);
        var measured = g.MeasureString(text, BarFont);
        float textWidth = Math.Min(measured.Width, maxWidth - padX * 2);
        float pillWidth = textWidth + padX * 2;
        float x = (width - pillWidth) / 2f;

        var bg = error
            ? Color.FromArgb((int)(228 * alpha), 176, 48, 54)
            : dark
                ? Color.FromArgb((int)(210 * alpha), 22, 26, 33)
                : Color.FromArgb((int)(225 * alpha), 246, 248, 251);
        using (var path = RoundedRect(new RectangleF(x, y, pillWidth, pillHeight), pillHeight / 2f))
        using (var brush = new SolidBrush(bg))
            g.FillPath(brush, path);

        var textColor = error
            ? Color.FromArgb((int)(248 * alpha), 245, 247, 250)
            : dark
                ? Color.FromArgb((int)(248 * alpha), 245, 247, 250)
                : Color.FromArgb((int)(248 * alpha), 28, 32, 40);
        using var textBrush = new SolidBrush(textColor);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };
        g.DrawString(text, BarFont, textBrush, new RectangleF(x + padX, y, pillWidth - padX * 2, pillHeight), format);
    }

    private static Color TextColorFor(Color background)
    {
        double lum = Luminance(background);
        return lum > 0.55
            ? Color.FromArgb(255, 12, 14, 17)
            : Color.FromArgb(255, 249, 250, 252);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    internal static GraphicsPath RoundedRect(RectangleF rect, float radius)
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

    public void Dispose()
    {
        BarFont?.Dispose();
        BarFont = null;
    }

    private sealed class Spec
    {
        public required string Text { get; init; }
        public required Font Font { get; set; }
        public Rectangle Card { get; set; }
        public Color Background { get; init; }
        public Color Foreground { get; init; }
        public bool Center { get; init; }
        public int PadX { get; init; }
        public int PadY { get; init; }
    }

    private sealed class BitmapSampler : IDisposable
    {
        private readonly Bitmap _bitmap;
        private readonly BitmapData _data;
        private readonly byte[] _pixels;
        private readonly int _stride;

        public BitmapSampler(Bitmap bitmap)
        {
            _bitmap = bitmap;
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            _data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            _stride = Math.Abs(_data.Stride);
            _pixels = new byte[_stride * bitmap.Height];
            System.Runtime.InteropServices.Marshal.Copy(_data.Scan0, _pixels, 0, _pixels.Length);
        }

        private Color At(int x, int y)
        {
            int i = y * _stride + x * 4;
            return Color.FromArgb(255, _pixels[i + 2], _pixels[i + 1], _pixels[i]);
        }

        public Color SampleDominantColor(Rectangle rect)
        {
            var outer = rect;
            outer.Inflate(4, 4);
            outer.Intersect(new Rectangle(0, 0, _bitmap.Width, _bitmap.Height));
            if (outer.Width <= 0 || outer.Height <= 0)
                return Color.FromArgb(32, 32, 32);

            var counts = new Dictionary<int, (int Count, long R, long G, long B)>();
            void Add(int x, int y)
            {
                var c = At(x, y);
                int key = ((c.R >> 4) << 8) | ((c.G >> 4) << 4) | (c.B >> 4);
                if (counts.TryGetValue(key, out var e))
                    counts[key] = (e.Count + 1, e.R + c.R, e.G + c.G, e.B + c.B);
                else
                    counts[key] = (1, c.R, c.G, c.B);
            }

            for (int x = outer.Left; x < outer.Right; x++)
            {
                Add(x, outer.Top);
                Add(x, outer.Bottom - 1);
            }
            for (int y = outer.Top + 1; y < outer.Bottom - 1; y++)
            {
                Add(outer.Left, y);
                Add(outer.Right - 1, y);
            }

            if (counts.Count == 0) return Color.FromArgb(32, 32, 32);
            var best = counts.Values.OrderByDescending(v => v.Count).First();
            return Color.FromArgb(
                (int)(best.R / best.Count),
                (int)(best.G / best.Count),
                (int)(best.B / best.Count));
        }

        public void Dispose()
        {
            try { _bitmap.UnlockBits(_data); } catch { }
        }
    }
}
