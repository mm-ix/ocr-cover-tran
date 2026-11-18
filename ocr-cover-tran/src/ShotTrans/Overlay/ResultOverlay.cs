using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using ShotTrans.Input;
using ShotTrans.Interop;
using ShotTrans.Ocr;
using ShotTrans.Rendering;
using ShotTrans.Windows;

namespace ShotTrans.Overlay;

/// <summary>结果覆盖层：点击穿透、原位译文、Space 对比、R 重试、Ctrl+C 复制。</summary>
internal sealed class ResultOverlay : LayeredWindow
{
    private const int GrowthMargin = 160;

    private readonly Bitmap _screenshot;
    private readonly Rectangle _region;
    private readonly Rectangle _canvasBounds;
    private readonly int _contentOffsetY;
    private readonly int _allowedBottom;
    private readonly float _barY;
    private readonly float _dpiScale;
    private readonly IReadOnlyList<OcrLineBox> _blocks;
    private readonly AppConfig _config;
    private readonly OverlayRenderer _renderer;
    private readonly List<string> _originalTexts;
    private readonly InputHook _hook;
    private readonly System.Windows.Forms.Timer _fadeTimer;
    private readonly System.Windows.Forms.Timer _topmostTimer;
    private readonly CancellationTokenSource _cts = new();

    private Bitmap? _canvasBitmap;
    private Graphics? _canvasGraphics;
    private long _lastRenderTick;
    private bool _renderQueued;

    private OverlayLayout? _originalLayout;
    private OverlayLayout? _translatedLayout;
    private IReadOnlyList<string>? _translations;
    private Rectangle[] _screenHits = Array.Empty<Rectangle>();

    private bool _showOriginalMode;
    private bool _errorBar;
    private bool _barVisible = true;
    private double _barAlpha = 1;
    private string _barText = "";
    private int _fadeElapsedMs;
    private bool _closed;
    private bool _rendering;
    private bool _selectingMouse;
    private bool _menuOpen;
    private readonly TextSelection _selection = new();
    private readonly ContextMenuStrip _copyMenu;
    private readonly ToolStripMenuItem _copySelectedItem;
    private readonly ToolStripMenuItem _copyAllItem;
    private readonly ToolStripMenuItem _clearSelectionItem;

    public event Action? RetryRequested;
    public event Action? Closed;
    public event Action<string>? NotifyRequested;

    public CancellationToken CancellationToken => _cts.Token;
    public bool IsClosed => _closed;

    public IReadOnlyList<string> GetOriginalTexts() => _originalTexts;

    public byte[]? CreateThumbnail() => ShotTrans.Capture.ScreenCapture.MakeThumbnailPng(_screenshot);

    public ResultOverlay(Bitmap screenshot, IReadOnlyList<OcrLineBox> blocks, Rectangle screenRegion, AppConfig config)
        : base(clickThrough: false)
    {
        _screenshot = screenshot;
        _region = screenRegion;

        var center = new Point(
            screenRegion.X + screenRegion.Width / 2,
            screenRegion.Y + screenRegion.Height / 2);
        _dpiScale = NativeMethods.GetDpiScale(center);
        var work = NativeMethods.GetMonitorWorkArea(center);

        // 画布只做纵向扩容：上方放状态条，下方留给译文卡片生长，避免把文字压到过小。
        int slot = Math.Max(30, (int)Math.Round(36 * _dpiScale));
        int topMargin = 0, bottomMargin = 0;
        bool barAbove = false, barBelow = false;
        if (screenRegion.Top - slot >= work.Top)
        {
            topMargin = slot;
            barAbove = true;
        }
        int availableBelow = Math.Max(0, work.Bottom - screenRegion.Bottom);
        if (!barAbove && availableBelow >= slot)
        {
            barBelow = true;
            bottomMargin += slot;
            availableBelow -= slot;
        }
        bottomMargin += Math.Min(GrowthMargin, availableBelow);

        _canvasBounds = Rectangle.FromLTRB(
            screenRegion.Left,
            screenRegion.Top - topMargin,
            screenRegion.Right,
            screenRegion.Bottom + bottomMargin);
        _contentOffsetY = topMargin;
        _barY = barAbove ? 3 : barBelow ? _canvasBounds.Height - slot + 3 : 4;
        _allowedBottom = Math.Max(
            screenRegion.Height,
            _canvasBounds.Height - _contentOffsetY - (barBelow ? slot : 0) - 4);

        _blocks = blocks;
        _config = config;
        _originalTexts = blocks.Select(b => b.Text).ToList();
        _renderer = new OverlayRenderer(screenshot, blocks, config, _dpiScale);

        _hook = new InputHook
        {
            HandleEscape = true,
            HandleSpace = true,
            HandleCopy = true,
            HandleRetry = true,
            HandleRightClick = true
        };
        _hook.EscapePressed += HandleEscape;
        _hook.SpaceDown += () => { ReassertTopmost(); SetOriginalMode(true); };
        _hook.SpaceUp += () => { ReassertTopmost(); SetOriginalMode(false); };
        _hook.CopyPressed += () => { ReassertTopmost(); CopySelectionOrAll(); };
        _hook.RetryPressed += () =>
        {
            if (_errorBar && !_closed) RetryRequested?.Invoke();
        };
        _hook.MouseDown += OnGlobalMouseDown;

        _fadeTimer = new System.Windows.Forms.Timer { Interval = 50 };
        _fadeTimer.Tick += (_, _) => OnFadeTick();

        _topmostTimer = new System.Windows.Forms.Timer { Interval = 800 };
        _topmostTimer.Tick += (_, _) => ReassertTopmost();

        _copySelectedItem = new ToolStripMenuItem("复制");
        _copySelectedItem.Click += (_, _) => CopySelectionOrAll();
        _copyAllItem = new ToolStripMenuItem("复制全部译文");
        _copyAllItem.Click += (_, _) => CopyAllTranslations();
        _clearSelectionItem = new ToolStripMenuItem("取消选择");
        _clearSelectionItem.Click += (_, _) => { _selection.Clear(); Render(); };
        _copyMenu = new ContextMenuStrip { ShowImageMargin = false };
        _copyMenu.Items.Add(_copySelectedItem);
        _copyMenu.Items.Add(_copyAllItem);
        _copyMenu.Items.Add(new ToolStripSeparator());
        _copyMenu.Items.Add(_clearSelectionItem);
        _copyMenu.Closed += (_, _) => _menuOpen = false;
    }

    private void ReassertTopmost()
    {
        if (_closed || Handle == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    public void ShowBusyOriginal()
    {
        if (_closed) return;
        _originalLayout?.Dispose();
        _originalLayout = BuildLayout(_originalTexts, _config.AlignCenter);
        _selection.Clear();
        RefreshSelectionBlocks();
        _barText = "正在翻译…";
        _errorBar = false;
        _barVisible = true;
        _barAlpha = 1;
        _hook.Install();
        _topmostTimer.Start();
        Render();
    }

    public void ShowTranslated(IReadOnlyList<string> texts, double elapsedSeconds)
    {
        if (_closed) return;
        _translations = texts;
        _translatedLayout?.Dispose();
        _translatedLayout = BuildLayout(texts, _config.AlignCenter);
        int count = texts.Count(t => !string.IsNullOrWhiteSpace(t));
        _barText = $"已翻译 {count} 块 · {elapsedSeconds:0.0}s   拖动选择 / 右键复制 / Space 原文 / Esc 关闭";
        _errorBar = false;
        _barVisible = true;
        _barAlpha = 1;
        _fadeElapsedMs = 0;
        _showOriginalMode = false;
        _selection.Clear();
        RefreshSelectionBlocks();
        StartFadeTimer();
        Render();
    }

    public void ShowRetrying()
    {
        if (_closed) return;
        _errorBar = false;
        _barText = "正在重试…";
        _barVisible = true;
        _barAlpha = 1;
        _showOriginalMode = false;
        _selection.Clear();
        RefreshSelectionBlocks();
        StopFadeTimer();
        Render();
    }

    public void ShowError(string message)
    {
        if (_closed) return;
        _errorBar = true;
        _barText = "翻译失败：" + Shorten(message, 90) + "    R 重试 / Esc 关闭";
        _barVisible = true;
        _barAlpha = 1;
        _showOriginalMode = false;
        _selection.Clear();
        RefreshSelectionBlocks();
        StopFadeTimer();
        Render();
    }

    private void StartFadeTimer()
    {
        int fadeSeconds = Math.Clamp(_config.StatusBarFadeSeconds, 0, 30);
        if (fadeSeconds <= 0)
        {
            _barVisible = false;
            return;
        }
        _fadeElapsedMs = 0;
        _fadeTimer.Start();
    }

    private void StopFadeTimer() => _fadeTimer.Stop();

    private void OnFadeTick()
    {
        if (_closed || _errorBar || _showOriginalMode) return;
        int fadeSeconds = Math.Clamp(_config.StatusBarFadeSeconds, 0, 30);
        _fadeElapsedMs += _fadeTimer.Interval;
        if (_fadeElapsedMs < fadeSeconds * 1000) return;

        _barAlpha -= 0.12;
        if (_barAlpha <= 0.01)
        {
            _barAlpha = 0;
            _barVisible = false;
            _fadeTimer.Stop();
        }
        Render();
    }

    private OverlayLayout BuildLayout(IReadOnlyList<string> texts, bool center)
    {
        var layout = _renderer.BuildLayout(_blocks, texts, center, _allowedBottom);
        _screenHits = layout.ScreenHits
            .Select(r => new Rectangle(_region.X + r.X, _region.Y + r.Y, r.Width, r.Height))
            .ToArray();
        if (Environment.GetEnvironmentVariable("SHOTRANS_DEBUG_INPUT") == "1")
        {
            foreach (var r in _screenHits)
                Log.Info($"译文块屏幕区域：{r}");
        }
        return layout;
    }

    private OverlayLayout? CurrentLayout =>
        _showOriginalMode || _translatedLayout == null ? _originalLayout : _translatedLayout;

    private void RefreshSelectionBlocks()
    {
        var layout = CurrentLayout;
        if (layout == null)
        {
            _selection.SetBlocks(Array.Empty<SelectableBlock>());
            return;
        }
        var list = new List<SelectableBlock>(layout.Blocks.Count);
        foreach (var block in layout.Blocks)
        {
            list.Add(new SelectableBlock
            {
                Text = block.RenderedText,
                CharRects = block.CharRects,
                Bounds = block.Rect
            });
        }
        _selection.SetBlocks(list);
    }

    private void SetOriginalMode(bool original)
    {
        if (_closed || _translations == null) return;
        if (_showOriginalMode == original) return;
        _showOriginalMode = original;
        _selection.Clear();
        RefreshSelectionBlocks();
        if (Environment.GetEnvironmentVariable("SHOTRANS_DEBUG_INPUT") == "1")
            Log.Info("Space 原文模式：" + original);
        Render();
    }

    private bool IsPointInText(Point screenPoint)
    {
        foreach (var hit in _screenHits)
            if (hit.Contains(screenPoint)) return true;
        return false;
    }

    private void OnGlobalMouseDown(Point pt, bool isRight)
    {
        if (_closed || _menuOpen) return;
        if (isRight)
        {
            if (IsPointInText(pt))
                ShowCopyMenu(pt);
            else
                Close();
            return;
        }
        // 文字区域内的左键由窗口自己处理（拖选）；空白区域点击关闭。
        if (!IsPointInText(pt)) Close();
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
        _copyAllItem.Text = _translations != null ? "复制全部译文" : "复制全部原文";
        _copyAllItem.Enabled = true;
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
                CopyText(selected, "已复制选中文字");
                return;
            }
        }
        CopyAllTranslations();
    }

    private void CopyAllTranslations()
    {
        if (_closed) return;
        var texts = _translations ?? _originalTexts;
        string joined = string.Join("\n", texts.Where(t => !string.IsNullOrWhiteSpace(t)));
        if (joined.Length == 0) return;
        CopyText(joined, "已复制全部" + (_translations != null ? "译文" : "原文"));
    }

    private void CopyText(string text, string toast)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("SHOTRANS_DEBUG_INPUT") == "1")
                Log.Info($"复制文字（{text.Length} 字符）");
            if (!Util.ClipboardService.TrySetText(text, 800))
            {
                NotifyRequested?.Invoke("复制失败：剪贴板被其他程序占用");
                return;
            }
            NotifyRequested?.Invoke(toast);
        }
        catch (Exception ex)
        {
            Log.Error("复制失败", ex);
            NotifyRequested?.Invoke("复制失败：" + ex.Message);
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case NativeMethods.WM_NCHITTEST:
            {
                var screen = new Point(NativeMethods.GetXFromLParam(m.LParam), NativeMethods.GetYFromLParam(m.LParam));
                m.Result = IsPointInText(screen) ? (IntPtr)1 : (IntPtr)(-1);
                return;
            }
            case NativeMethods.WM_LBUTTONDOWN:
            {
                if (_closed) break;
                var local = new Point(
                    NativeMethods.GetXFromLParam(m.LParam),
                    NativeMethods.GetYFromLParam(m.LParam) - _contentOffsetY);
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
                var local = new Point(
                    NativeMethods.GetXFromLParam(m.LParam),
                    NativeMethods.GetYFromLParam(m.LParam) - _contentOffsetY);
                _selection.Update(local);
                QueueRender();
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
                var handle = IsPointInText(new Point(cursor.X, cursor.Y))
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
            if (!_closed) Render();
        };
        timer.Start();
    }

    private void Render()
    {
        if (_closed || _rendering) return;
        _rendering = true;
        _lastRenderTick = Environment.TickCount64;
        try
        {
            var layout = _showOriginalMode || _translatedLayout == null ? _originalLayout : _translatedLayout;
            if (layout == null) return;

            if (_canvasBitmap == null ||
                _canvasBitmap.Width != _canvasBounds.Width ||
                _canvasBitmap.Height != _canvasBounds.Height)
            {
                _canvasGraphics?.Dispose();
                _canvasBitmap?.Dispose();
                _canvasBitmap = new Bitmap(_canvasBounds.Width, _canvasBounds.Height, PixelFormat.Format32bppPArgb);
                _canvasGraphics = Graphics.FromImage(_canvasBitmap);
            }

            var g = _canvasGraphics!;
            g.ResetTransform();
            g.ResetClip();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            g.TranslateTransform(0, _contentOffsetY);
            var ranges = _selection.GetRanges();
            _renderer.DrawBlocks(g, layout, ranges.Count > 0 ? ranges : null);
            g.ResetTransform();
            if (_barVisible && _barAlpha > 0)
                _renderer.DrawStatusBar(g, _canvasBitmap.Width, _barText, _errorBar, _barAlpha, _barY, ShotTrans.Ui.Theme.IsDark(_config.UiTheme));

            ShowBitmap(_canvasBitmap, _canvasBounds);
        }
        catch (Exception ex)
        {
            Log.Error("覆盖层渲染失败", ex);
        }
        finally
        {
            _rendering = false;
        }
    }

    private static string Shorten(string s, int max)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        Log.Info("覆盖层关闭");
        try { _cts.Cancel(); } catch { }
        CloseContextMenu();
        try { _copyMenu.Dispose(); } catch { }
        if (_selectingMouse)
        {
            _selectingMouse = false;
            try { NativeMethods.ReleaseCapture(); } catch { }
        }
        _selection.Clear();
        StopFadeTimer();
        _fadeTimer.Dispose();
        _topmostTimer.Stop();
        _topmostTimer.Dispose();
        _hook.Dispose();
        HideWindow();
        Closed?.Invoke();
        _originalLayout?.Dispose();
        _translatedLayout?.Dispose();
        _originalLayout = null;
        _translatedLayout = null;
        _canvasGraphics?.Dispose();
        _canvasGraphics = null;
        _canvasBitmap?.Dispose();
        _canvasBitmap = null;
        _renderer.Dispose();
        _screenshot.Dispose();
        base.Dispose();
    }

    public override void Dispose()
    {
        Close();
    }
}
