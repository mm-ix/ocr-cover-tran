using System.Drawing.Imaging;
using ShotTrans.Input;
using ShotTrans.Interop;
using ShotTrans.Rendering;
using ShotTrans.Windows;

namespace ShotTrans.Ui;

/// <summary>划词翻译结果卡片：译文可鼠标选择，右键菜单复制；Esc/点外部关闭。</summary>
internal sealed class SelectionTranslatePopup : LayeredWindow
{
    private readonly int MaxWidth;
    private readonly int MaxBodyHeight;
    private readonly int MaxExplainHeight;
    private readonly int ExplainHeaderHeight;
    private readonly int ExplainGap;
    private readonly float WheelStep;
    private readonly int Padding;
    private readonly int TitleHeight;
    private readonly int FooterHeight;
    private readonly float _scale;

    private readonly Point _anchor;
    private readonly InputHook _hook;
    private readonly System.Windows.Forms.Timer _autoCloseTimer;
    private readonly Font _titleFont;
    private readonly Font _bodyFont;
    private readonly Font _footerFont;
    private readonly TextSelection _selection = new();
    private readonly UiPalette _ui;

    private string _body = "";
    private string _bodyDrawn = "";
    private string? _bodySignature;
    private RectangleF _bodyRect;
    private RectangleF[] _bodyCharRects = Array.Empty<RectangleF>();
    private string _footer = "拖动选择 · 右键复制 · Esc / 点击外部关闭";
    private string _title = "划词翻译";
    private string _explanation = "";
    private bool _hasExplanation;
    private float _explainScroll;
    private float _explainContentHeight;
    private bool _explainScrollable;
    private RectangleF _explainViewport;
    private bool _isError;
    private bool _loading;
    private bool _selectingMouse;
    private bool _menuOpen;
    private readonly ContextMenuStrip _copyMenu;
    private readonly ToolStripMenuItem _copySelectedItem;
    private readonly ToolStripMenuItem _copyAllItem;
    private readonly ToolStripMenuItem _clearSelectionItem;
    private Rectangle _bounds;
    private string _copyText = "";
    private bool _closed;

    public bool IsClosed => _closed;
    public CancellationToken CancellationToken => _cts.Token;
    private readonly CancellationTokenSource _cts = new();

    public SelectionTranslatePopup(Point anchor, bool dark = true) : base(clickThrough: false, caption: "ShotTrans.SelectionPopup")
    {
        _anchor = anchor;
        _scale = NativeMethods.GetDpiScale(anchor);
        MaxWidth = UiDraw.ScalePx(460, _scale);
        MaxBodyHeight = UiDraw.ScalePx(360, _scale);
        MaxExplainHeight = UiDraw.ScalePx(200, _scale);
        ExplainHeaderHeight = UiDraw.ScalePx(22, _scale);
        ExplainGap = UiDraw.ScalePx(10, _scale);
        WheelStep = 64f * _scale;
        Padding = UiDraw.ScalePx(16, _scale);
        TitleHeight = UiDraw.ScalePx(26, _scale);
        FooterHeight = UiDraw.ScalePx(22, _scale);
        _ui = UiPalette.For(dark);
        _titleFont = UiDraw.CreateScaledFont(12, _scale, FontStyle.Bold);
        _bodyFont = UiDraw.CreateScaledFont(14, _scale);
        _footerFont = UiDraw.CreateScaledFont(11, _scale);

        _hook = new InputHook
        {
            HandleEscape = true,
            HandleCopy = true,
            HandleRightClick = true,
            HandleWheel = true
        };
        _hook.EscapePressed += HandleEscape;
        _hook.CopyPressed += CopySelectionOrAll;
        _hook.MouseDown += OnGlobalMouseDown;
        _hook.WheelScrolled += OnWheel;
        _hook.WheelFilter = p => _hasExplanation && _bounds.Contains(p);
        _hook.Install();

        _autoCloseTimer = new System.Windows.Forms.Timer { Interval = 20000 };
        _autoCloseTimer.Tick += (_, _) =>
        {
            if (_menuOpen) return;
            Close();
        };
        _autoCloseTimer.Start();

        _copySelectedItem = new ToolStripMenuItem("复制");
        _copySelectedItem.Click += (_, _) => CopySelectionOrAll();
        _copyAllItem = new ToolStripMenuItem("复制全部译文");
        _copyAllItem.Click += (_, _) => CopyAllText();
        _clearSelectionItem = new ToolStripMenuItem("取消选择");
        _clearSelectionItem.Click += (_, _) => { _selection.Clear(); Render(); };
        _copyMenu = new ContextMenuStrip { ShowImageMargin = false };
        _copyMenu.Items.Add(_copySelectedItem);
        _copyMenu.Items.Add(_copyAllItem);
        _copyMenu.Items.Add(new ToolStripSeparator());
        _copyMenu.Items.Add(_clearSelectionItem);
        _copyMenu.Closed += (_, _) => _menuOpen = false;
    }

    public void ShowLoading(string originalText, string? title = null)
    {
        _loading = true;
        _isError = false;
        _title = title ?? "划词翻译";
        _footer = "正在翻译…";
        _copyText = "";
        _body = originalText.Length > 500 ? originalText[..500] + "…" : originalText;
        _bodySignature = null;
        _explanation = "";
        _hasExplanation = false;
        _explainScroll = 0;
        _selection.Clear();
        Render();
    }

    public void ShowResult(string translated, string? title = null)
    {
        if (_closed) return;
        _loading = false;
        _isError = false;
        _body = translated;
        _copyText = translated;
        _title = title ?? "划词翻译";
        _footer = "拖动选择 · 右键复制 · Esc / 点击外部关闭";
        _bodySignature = null;
        _selection.Clear();
        _autoCloseTimer.Interval = 15000;
        _autoCloseTimer.Stop();
        _autoCloseTimer.Start();
        Render();
    }

    public void ShowAlreadyChinese(string text)
    {
        _loading = false;
        _isError = false;
        _body = text;
        _copyText = text;
        _title = "划词翻译 · 原文已是中文";
        _footer = "拖动选择 · 右键复制 · Esc / 点击外部关闭";
        _bodySignature = null;
        _selection.Clear();
        Render();
    }

    public void ShowError(string message)
    {
        if (_closed) return;
        _loading = false;
        _isError = true;
        _body = message;
        _copyText = message;
        _title = "划词翻译失败";
        _footer = "Esc / 点击外部关闭";
        _bodySignature = null;
        _hasExplanation = false;
        _selection.Clear();
        _autoCloseTimer.Interval = 8000;
        _autoCloseTimer.Stop();
        _autoCloseTimer.Start();
        Render();
    }

    /// <summary>在译文下方追加一段针对原文的简短词义/介绍，内容过长可滚动查看。</summary>
    public void ShowExplanation(string text)
    {
        if (_closed || _isError) return;
        if (string.IsNullOrWhiteSpace(text)) return;
        _explanation = text.Trim();
        _hasExplanation = true;
        _explainScroll = 0;
        _autoCloseTimer.Interval = 15000;
        _autoCloseTimer.Stop();
        _autoCloseTimer.Start();
        Render();
    }

    private void OnWheel(Point screenPoint, int delta)
    {
        if (_closed || !_hasExplanation) return;
        if (!_bounds.Contains(screenPoint)) return;
        float maxScroll = Math.Max(0f, _explainContentHeight - _explainViewport.Height);
        if (maxScroll <= 0f) return;
        _explainScroll = Math.Clamp(_explainScroll - delta / 120f * WheelStep, 0f, maxScroll);
        _autoCloseTimer.Stop();
        _autoCloseTimer.Start();
        Render();
    }

    private Rectangle BodyScreenRect
    {
        get
        {
            if (_bodyRect.Width <= 0 || _bodyRect.Height <= 0) return Rectangle.Empty;
            return new Rectangle(
                _bounds.X + (int)_bodyRect.X,
                _bounds.Y + (int)_bodyRect.Y,
                (int)_bodyRect.Width,
                (int)_bodyRect.Height);
        }
    }

    private void OnGlobalMouseDown(Point screenPoint, bool isRight)
    {
        if (_closed || _menuOpen) return;
        if (isRight)
        {
            if (BodyScreenRect.Contains(screenPoint))
                ShowCopyMenu(screenPoint);
            else
                Close();
            return;
        }
        if (!_bounds.Contains(screenPoint)) Close();
    }

    private void HandleEscape()
    {
        if (_menuOpen)
        {
            CloseContextMenu();
            return;
        }
        Close();
    }

    private void ShowCopyMenu(Point screenPoint)
    {
        if (_closed || _menuOpen) return;
        var hasSelection = _selection.HasSelection;
        _copySelectedItem.Enabled = hasSelection;
        _clearSelectionItem.Enabled = hasSelection;
        _copyAllItem.Text = _isError ? "复制全部文本" : "复制全部译文";
        _copyAllItem.Enabled = !_loading && !string.IsNullOrEmpty(_copyText);
        _menuOpen = true;
        _copyMenu.Show(new Point(screenPoint.X, screenPoint.Y));
    }

    private void CloseContextMenu()
    {
        if (!_menuOpen) return;
        _menuOpen = false;
        try { _copyMenu.Close(); } catch { }
    }

    private void CopySelectionOrAll()
    {
        if (_closed) return;
        if (_selection.HasSelection)
        {
            var selected = _selection.GetSelectedText();
            if (selected.Length > 0)
            {
                CopyText(selected);
                return;
            }
        }
        CopyAllText();
    }

    private void CopyAllText()
    {
        if (_closed || string.IsNullOrEmpty(_copyText)) return;
        CopyText(_copyText);
    }

    private void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _footer = Util.ClipboardService.TrySetText(text, 800)
            ? "已复制到剪贴板"
            : "复制失败：剪贴板被其他程序占用";
        Render();
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case NativeMethods.WM_NCHITTEST:
            {
                var screen = new Point(NativeMethods.GetXFromLParam(m.LParam), NativeMethods.GetYFromLParam(m.LParam));
                m.Result = BodyScreenRect.Contains(screen) ? (IntPtr)1 : (IntPtr)(-1);
                return;
            }
            case NativeMethods.WM_LBUTTONDOWN:
            {
                if (_closed) break;
                var local = new Point(NativeMethods.GetXFromLParam(m.LParam), NativeMethods.GetYFromLParam(m.LParam));
                if (_selection.Begin(local))
                {
                    _selectingMouse = true;
                    NativeMethods.SetCapture(Handle);
                    Render();
                }
                return;
            }
            case NativeMethods.WM_MOUSEMOVE when _selectingMouse:
            {
                var local = new Point(NativeMethods.GetXFromLParam(m.LParam), NativeMethods.GetYFromLParam(m.LParam));
                _selection.Update(local);
                Render();
                return;
            }
            case NativeMethods.WM_LBUTTONUP when _selectingMouse:
            {
                _selectingMouse = false;
                _selection.End();
                NativeMethods.ReleaseCapture();
                Render();
                return;
            }
            case NativeMethods.WM_SETCURSOR:
            {
                NativeMethods.GetCursorPos(out var cursor);
                var handle = BodyScreenRect.Contains(new Point(cursor.X, cursor.Y))
                    ? NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_IBEAM)
                    : NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_ARROW);
                NativeMethods.SetCursor(handle);
                m.Result = (IntPtr)1;
                return;
            }
            case NativeMethods.WM_ERASEBKGND:
                m.Result = (IntPtr)1;
                return;
        }
        base.WndProc(ref m);
    }

    private void Render()
    {
        if (_closed) return;
        var work = NativeMethods.GetMonitorWorkArea(_anchor);
        int edge = UiDraw.ScalePx(8, _scale);
        int maxWidth = Math.Min(MaxWidth, Math.Max(UiDraw.ScalePx(260, _scale), work.Width - UiDraw.ScalePx(32, _scale)));

        using var measureBitmap = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(measureBitmap);
        UiDraw.Prepare(g);

        float contentWidth = maxWidth - Padding * 2;
        var bodySize = g.MeasureString(_body, _bodyFont, new SizeF(contentWidth, 100000f));
        float bodyHeight = Math.Min(bodySize.Height, MaxBodyHeight);

        // 简介区域固定预留滚动条宽度，避免出现滚动条时文字重新换行
        float explainWidth = Math.Max(60f * _scale, contentWidth - 10f * _scale);
        float explainContentHeight = 0f;
        float explainViewportHeight = 0f;
        if (_hasExplanation)
        {
            explainContentHeight = g.MeasureString(_explanation, _bodyFont, new SizeF(explainWidth, 100000f)).Height;
            explainViewportHeight = Math.Min(explainContentHeight, MaxExplainHeight);
        }

        int width = maxWidth;
        int height = (int)Math.Ceiling(
            Padding + TitleHeight + bodyHeight
            + (_hasExplanation ? ExplainGap + ExplainHeaderHeight + explainViewportHeight : 0)
            + 8 * _scale + FooterHeight + Padding);

        int gap = UiDraw.ScalePx(18, _scale);
        int left = _anchor.X + gap;
        int top = _anchor.Y + UiDraw.ScalePx(20, _scale);
        if (left + width > work.Right - edge) left = _anchor.X - width - gap;
        left = Math.Clamp(left, work.Left + edge, Math.Max(work.Left + edge, work.Right - width - edge));
        if (top + height > work.Bottom - edge) top = _anchor.Y - height - gap;
        top = Math.Clamp(top, work.Top + edge, Math.Max(work.Top + edge, work.Bottom - height - edge));
        _bounds = new Rectangle(left, top, width, height);

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bmp))
        {
            UiDraw.Prepare(graphics);
            graphics.Clear(Color.Transparent);

            using (var path = UiDraw.RoundedRect(new RectangleF(0, 0, width, height), 10 * _scale))
            using (var brush = new SolidBrush(_ui.CardBack))
                graphics.FillPath(brush, path);
            using (var border = new Pen(_ui.CardBorder, 1f))
            using (var path = UiDraw.RoundedRect(new RectangleF(0.5f, 0.5f, width - 1, height - 1), 10 * _scale))
                graphics.DrawPath(border, path);

            var accent = _isError ? _ui.ErrorAccent : _ui.TitleAccent;
            using (var titleBrush = new SolidBrush(accent))
            using (var titleFormat = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
            {
                graphics.DrawString(_title, _titleFont, titleBrush,
                    new RectangleF(Padding, Padding, contentWidth, TitleHeight), titleFormat);
            }

            var bodyRect = new RectangleF(Padding, Padding + TitleHeight, contentWidth, bodyHeight);
            var bodyToDraw = UiDraw.TruncateToHeight(graphics, _body, _bodyFont, contentWidth, bodyHeight);
            var signature = bodyToDraw + "" + width + "x" + height;
            if (!string.Equals(signature, _bodySignature, StringComparison.Ordinal))
            {
                _bodySignature = signature;
                _bodyDrawn = bodyToDraw;
                _bodyRect = bodyRect;
                using var measureFormat = new StringFormat
                {
                    Alignment = StringAlignment.Near,
                    LineAlignment = StringAlignment.Near,
                    FormatFlags = StringFormatFlags.MeasureTrailingSpaces
                };
                _bodyCharRects = TextMeasure.CharacterRects(graphics, bodyToDraw, _bodyFont, bodyRect, measureFormat);
                _selection.SetBlocks(new[]
                {
                    new SelectableBlock
                    {
                        Text = bodyToDraw,
                        CharRects = _bodyCharRects,
                        Bounds = Rectangle.Round(bodyRect)
                    }
                });
            }

            var ranges = _selection.GetRanges();
            if (ranges.TryGetValue(0, out var range))
            {
                int start = Math.Clamp(range.Start, 0, Math.Max(0, _bodyCharRects.Length - 1));
                int end = Math.Clamp(range.End, 0, Math.Max(0, _bodyCharRects.Length - 1));
                using var highlight = new SolidBrush(_ui.SelectionFill);
                for (int c = start; c <= end && c < _bodyCharRects.Length; c++)
                {
                    var r = _bodyCharRects[c];
                    if (r.Width > 0.1f && r.Height > 0.1f)
                        graphics.FillRectangle(highlight, r);
                }
            }

            using (var bodyBrush = new SolidBrush(_isError ? _ui.ErrorText : _ui.BodyText))
            using (var bodyFormat = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near })
            {
                graphics.DrawString(_bodyDrawn, _bodyFont, bodyBrush, _bodyRect, bodyFormat);
            }

            _explainContentHeight = explainContentHeight;
            _explainScrollable = _hasExplanation && explainContentHeight > explainViewportHeight + 0.5f;
            if (_hasExplanation)
            {
                float bodyBottom = bodyRect.Bottom;
                float dividerY = bodyBottom + ExplainGap / 2f;
                using (var divPen = new Pen(_ui.Divider, 1f))
                    graphics.DrawLine(divPen, Padding, dividerY, Padding + contentWidth, dividerY);

                float headerTop = bodyBottom + ExplainGap;
                using (var headerBrush = new SolidBrush(_ui.TitleAccent))
                using (var headerFormat = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
                {
                    graphics.DrawString("词义 / 简介", _titleFont, headerBrush,
                        new RectangleF(Padding, headerTop, contentWidth, ExplainHeaderHeight), headerFormat);
                }

                var viewport = new RectangleF(Padding, headerTop + ExplainHeaderHeight, explainWidth, explainViewportHeight);
                _explainViewport = viewport;
                float maxScroll = Math.Max(0f, explainContentHeight - explainViewportHeight);
                _explainScroll = Math.Clamp(_explainScroll, 0f, maxScroll);

                var savedClip = graphics.Save();
                graphics.SetClip(viewport);
                graphics.TranslateTransform(0f, -_explainScroll);
                using (var exBrush = new SolidBrush(_ui.ExplainText))
                using (var exFormat = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near })
                {
                    graphics.DrawString(_explanation, _bodyFont, exBrush,
                        new RectangleF(viewport.X, viewport.Y, explainWidth, explainContentHeight + 4f), exFormat);
                }
                graphics.Restore(savedClip);

                if (_explainScrollable)
                {
                    float sbWidth = 3f * _scale;
                    float sbX = Padding + contentWidth - 5f * _scale;
                    float trackTop = viewport.Y;
                    float trackH = viewport.Height;
                    using (var trackBrush = new SolidBrush(_ui.ScrollTrack))
                        graphics.FillRectangle(trackBrush, sbX, trackTop, sbWidth, trackH);
                    float thumbH = Math.Max(18f * _scale, trackH * (explainViewportHeight / explainContentHeight));
                    float thumbTop = trackTop + (trackH - thumbH) * (_explainScroll / maxScroll);
                    using (var thumbBrush = new SolidBrush(_ui.ScrollThumb))
                        graphics.FillRectangle(thumbBrush, sbX, thumbTop, sbWidth, thumbH);
                }
            }

            var footerText = (_hasExplanation && _explainScrollable) ? "滚轮滚动简介 · " + _footer : _footer;
            using (var footerBrush = new SolidBrush(_ui.FooterText))
            using (var footerFormat = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
            {
                graphics.DrawString(footerText, _footerFont, footerBrush,
                    new RectangleF(Padding, height - Padding - FooterHeight, contentWidth, FooterHeight), footerFormat);
            }
        }

        ShowBitmap(bmp, _bounds);
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        try { _cts.Cancel(); } catch { }
        CloseContextMenu();
        try { _copyMenu.Dispose(); } catch { }
        if (_selectingMouse)
        {
            _selectingMouse = false;
            try { NativeMethods.ReleaseCapture(); } catch { }
        }
        _selection.Clear();
        _autoCloseTimer.Stop();
        _autoCloseTimer.Dispose();
        _hook.Dispose();
        HideWindow();
        _titleFont.Dispose();
        _bodyFont.Dispose();
        _footerFont.Dispose();
        base.Dispose();
    }

    public override void Dispose() => Close();
}
