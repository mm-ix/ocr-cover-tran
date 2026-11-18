using System.Drawing.Imaging;
using ShotTrans.Interop;
using ShotTrans.Windows;

namespace ShotTrans.Ui;

internal enum ToastKind
{
    Info,
    Success,
    Error
}

/// <summary>不会抢焦点、可穿透的轻提示条。</summary>
internal sealed class ToastWindow : LayeredWindow
{
    private readonly System.Windows.Forms.Timer _timer;

    public ToastWindow(string message, ToastKind kind, Rectangle anchor, int durationMs = 1800, bool dark = true)
        : base(clickThrough: true)
    {
        message ??= "";
        var ui = UiPalette.For(dark);
        float scale = NativeMethods.GetDpiScale(new Point(anchor.Left + anchor.Width / 2, anchor.Top + anchor.Height / 2));
        using var measureBitmap = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(measureBitmap);
        UiDraw.Prepare(g);
        using var font = UiDraw.CreateScaledFont(13, scale);

        float maxTextWidth = 560 * scale;
        var measured = g.MeasureString(message, font, new SizeF(maxTextWidth, 100000f));
        int width = (int)Math.Ceiling(Math.Clamp(measured.Width + 56 * scale, 180 * scale, maxTextWidth + 56 * scale));
        int height = (int)Math.Ceiling(Math.Clamp(measured.Height + 22 * scale, 42 * scale, 160 * scale));

        var work = NativeMethods.GetMonitorWorkArea(new Point(anchor.Left + anchor.Width / 2, anchor.Top + anchor.Height / 2));
        int edge = (int)Math.Round(8 * scale);
        int left = anchor.Left + (anchor.Width - width) / 2;
        left = Math.Clamp(left, work.Left + edge, Math.Max(work.Left + edge, work.Right - width - edge));
        int top = anchor.Top + (int)Math.Round(16 * scale);
        if (top + height > work.Bottom - edge || top < work.Top + edge)
            top = work.Top + (int)Math.Round(72 * scale);

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bmp))
        {
            UiDraw.Prepare(graphics);
            graphics.Clear(Color.Transparent);
            using (var path = UiDraw.RoundedRect(new RectangleF(0, 0, width, height), 9 * scale))
            using (var brush = new SolidBrush(ui.ToastBack))
                graphics.FillPath(brush, path);
            using (var border = new Pen(ui.ToastBorder, 1f))
            using (var path = UiDraw.RoundedRect(new RectangleF(0.5f, 0.5f, width - 1, height - 1), 9 * scale))
                graphics.DrawPath(border, path);

            var accent = kind switch
            {
                ToastKind.Success => Color.FromArgb(255, 78, 201, 160),
                ToastKind.Error => Color.FromArgb(255, 255, 100, 100),
                _ => Color.FromArgb(255, 90, 169, 255)
            };
            using (var accentBrush = new SolidBrush(accent))
                graphics.FillEllipse(accentBrush, 18 * scale, height / 2f - 4 * scale, 8 * scale, 8 * scale);

            using (var textBrush = new SolidBrush(ui.ToastText))
            using (var format = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
            {
                graphics.DrawString(message, font, textBrush,
                    new RectangleF(36 * scale, 8 * scale, width - 48 * scale, height - 16 * scale), format);
            }
        }

        ShowBitmap(bmp, new Rectangle(left, top, width, height));

        _timer = new System.Windows.Forms.Timer { Interval = Math.Clamp(durationMs, 700, 10000) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _timer.Dispose();
            Dispose();
        };
        _timer.Start();
    }

    public override void Dispose()
    {
        try { _timer?.Stop(); _timer?.Dispose(); } catch { }
        base.Dispose();
    }
}
