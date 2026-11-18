using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ShotTrans.Input;
using ShotTrans.Interop;
using ShotTrans.Windows;

namespace ShotTrans.Capture;

/// <summary>全屏暗化 + 拖拽框选，返回物理像素坐标。</summary>
internal sealed class SelectionOverlay : LayeredWindow
{
    private readonly Rectangle _virtual;
    private readonly float _scale;
    private readonly TaskCompletionSource<Rectangle?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Point _start;
    private Point _current;
    private bool _dragging;
    private bool _finished;
    private InputHook? _hook;
    private long _lastRenderTick;
    private bool _renderQueued;

    private Font? _hintFont;
    private Font? _sizeFont;

    public SelectionOverlay() : base(clickThrough: false)
    {
        _virtual = NativeMethods.GetVirtualScreen();
        if (_virtual.Width <= 0 || _virtual.Height <= 0)
            _virtual = SystemInformation.VirtualScreen;
        _scale = NativeMethods.GetDpiScale(new Point(_virtual.X + _virtual.Width / 2, _virtual.Y + 24));
    }

    public Task<Rectangle?> PickAsync()
    {
        EnsureHandle();
        _hook = new InputHook { HandleEscape = true, HandleRightClick = false };
        _hook.EscapePressed += Cancel;
        _hook.InstallKeyboard();
        Render();
        return _result.Task;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case NativeMethods.WM_LBUTTONDOWN when !_finished:
                NativeMethods.GetCursorPos(out var down);
                _start = new Point(down.X, down.Y);
                _current = _start;
                _dragging = true;
                NativeMethods.SetCapture(Handle);
                Render();
                return;

            case NativeMethods.WM_MOUSEMOVE when _dragging && !_finished:
                NativeMethods.GetCursorPos(out var move);
                _current = new Point(move.X, move.Y);
                QueueRender();
                return;

            case NativeMethods.WM_LBUTTONUP when _dragging && !_finished:
                NativeMethods.GetCursorPos(out var up);
                _current = new Point(up.X, up.Y);
                _dragging = false;
                NativeMethods.ReleaseCapture();
                FinishWithSelection();
                return;

            case NativeMethods.WM_RBUTTONDOWN:
            case NativeMethods.WM_RBUTTONUP:
                if (!_finished)
                {
                    Cancel();
                    return;
                }
                break;

            case NativeMethods.WM_SETCURSOR:
            {
                var cursor = NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_CROSS);
                NativeMethods.SetCursor(cursor);
                m.Result = (IntPtr)1;
                return;
            }

            case NativeMethods.WM_ERASEBKGND:
                m.Result = (IntPtr)1;
                return;
        }

        base.WndProc(ref m);
    }

    private void QueueRender()
    {
        long now = Environment.TickCount64;
        if (now - _lastRenderTick >= 12)
        {
            Render();
            return;
        }
        if (_renderQueued) return;
        _renderQueued = true;
        var timer = new System.Windows.Forms.Timer { Interval = 12 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            _renderQueued = false;
            if (!_finished) Render();
        };
        timer.Start();
    }

    private void FinishWithSelection()
    {
        var rect = NormalizeRect(_start, _current);
        if (rect.Width < 40 || rect.Height < 20)
        {
            Finish(null);
            return;
        }
        Finish(ScreenCapture.Normalize(rect));
    }

    private void Cancel() => Finish(null);

    private void Finish(Rectangle? rect)
    {
        if (_finished) return;
        _finished = true;
        try { NativeMethods.ReleaseCapture(); } catch { }
        _hook?.Dispose();
        _hook = null;
        HideWindow();
        _result.TrySetResult(rect);
        Dispose();
    }

    private static Rectangle NormalizeRect(Point a, Point b)
    {
        return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    private void Render()
    {
        _lastRenderTick = Environment.TickCount64;
        if (IsDisposed) return;

        using var bmp = new Bitmap(_virtual.Width, _virtual.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using (var dim = new SolidBrush(Color.FromArgb(102, 0, 0, 0)))
            g.FillRectangle(dim, 0, 0, bmp.Width, bmp.Height);

        _hintFont ??= CreateFont(14 * _scale, FontStyle.Regular);
        _sizeFont ??= CreateFont(13 * _scale, FontStyle.Bold);

        var sel = _dragging ? NormalizeRect(_start, _current) : Rectangle.Empty;
        if (sel.Width > 0 && sel.Height > 0)
        {
            var local = new Rectangle(sel.X - _virtual.X, sel.Y - _virtual.Y, sel.Width, sel.Height);
            local.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
            if (local.Width > 0 && local.Height > 0)
            {
                var oldMode = g.CompositingMode;
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var clear = new SolidBrush(Color.Transparent))
                    g.FillRectangle(clear, local);
                g.CompositingMode = oldMode;

                using var border = new Pen(Color.FromArgb(255, 90, 169, 255), 1.5f);
                g.DrawRectangle(border, local.X, local.Y, local.Width - 1, local.Height - 1);
            }
        }

        // 顶部提示
        DrawPill(g, "拖拽选择翻译区域 · Esc / 右键取消", bmp.Width / 2f, 24 * _scale, _hintFont,
            Color.FromArgb(215, 22, 26, 32), Color.FromArgb(255, 235, 240, 248));

        if (_dragging && sel.Width > 0 && sel.Height > 0)
        {
            string label = $"{sel.Width} × {sel.Height}";
            var localCursor = new PointF(_current.X - _virtual.X, _current.Y - _virtual.Y);
            float x = Math.Clamp(localCursor.X + 14 * _scale, 8 * _scale, Math.Max(8 * _scale, bmp.Width - 120 * _scale));
            float y = Math.Clamp(localCursor.Y + 16 * _scale, 8 * _scale, Math.Max(8 * _scale, bmp.Height - 44 * _scale));
            DrawPillAt(g, label, x, y, _sizeFont,
                Color.FromArgb(230, 20, 24, 30), Color.FromArgb(255, 120, 200, 255));
        }

        ShowBitmap(bmp, _virtual);
    }

    private static Font CreateFont(float pixelSize, FontStyle style)
    {
        try { return new Font("Microsoft YaHei UI", pixelSize, style, GraphicsUnit.Pixel); }
        catch { return new Font(FontFamily.GenericSansSerif, pixelSize, style, GraphicsUnit.Pixel); }
    }

    private void DrawPill(Graphics g, string text, float centerX, float top, Font font, Color bg, Color fg)
    {
        var size = g.MeasureString(text, font);
        float w = size.Width + 24 * _scale;
        float h = size.Height + 12 * _scale;
        float x = Math.Clamp(centerX - w / 2f, 8 * _scale, Math.Max(8 * _scale, g.VisibleClipBounds.Width - w - 8 * _scale));
        DrawPillRect(g, x, top, w, h, bg);
        using var brush = new SolidBrush(fg);
        g.DrawString(text, font, brush, new RectangleF(x + 12 * _scale, top + 6 * _scale, w - 24 * _scale, h - 12 * _scale),
            new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
    }

    private void DrawPillAt(Graphics g, string text, float x, float y, Font font, Color bg, Color fg)
    {
        var size = g.MeasureString(text, font);
        float w = size.Width + 22 * _scale;
        float h = size.Height + 10 * _scale;
        DrawPillRect(g, x, y, w, h, bg);
        using var brush = new SolidBrush(fg);
        g.DrawString(text, font, brush, new RectangleF(x + 11 * _scale, y + 5 * _scale, w - 22 * _scale, h - 10 * _scale),
            new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
    }

    private static void DrawPillRect(Graphics g, float x, float y, float w, float h, Color bg)
    {
        using var path = RoundedRect(new RectangleF(x, y, w, h), h / 2f);
        using var brush = new SolidBrush(bg);
        g.FillPath(brush, path);
    }

    internal static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (d <= 0)
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

    public override void Dispose()
    {
        if (!_finished)
        {
            _finished = true;
            _hook?.Dispose();
            _hook = null;
            _result.TrySetResult(null);
        }
        _hintFont?.Dispose();
        _sizeFont?.Dispose();
        base.Dispose();
    }
}
