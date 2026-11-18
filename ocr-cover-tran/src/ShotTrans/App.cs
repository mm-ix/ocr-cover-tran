using System.Diagnostics;
using System.Drawing;
using ShotTrans.Capture;
using ShotTrans.History;
using ShotTrans.Input;
using ShotTrans.Ocr;
using ShotTrans.Overlay;
using ShotTrans.Settings;
using ShotTrans.Translation;
using ShotTrans.Ui;
using ShotTrans.Util;

namespace ShotTrans;

internal sealed class ShotTransApp : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly TranslationService _translation;
    private readonly HistoryStore _history;
    private readonly HotkeyManager _hotkeys;
    private readonly NotifyIcon _tray;
    private readonly Icon _trayIcon;
    private readonly System.Windows.Forms.Timer _startupTimer;
    private readonly string? _startupWindow;

    private ResultOverlay? _overlay;
    private SelectionTranslatePopup? _popup;
    private ToastWindow? _toast;
    private SettingsForm? _settings;
    private HistoryForm? _historyForm;

    private bool _busy;
    private bool _modalOpen;
    private bool _exiting;

    public ShotTransApp(string[]? args = null)
    {
        _startupWindow = args?.Any(a => a.Equals("--history", StringComparison.OrdinalIgnoreCase)) == true
            ? "history"
            : args?.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase)) == true
                ? "settings"
                : null;

        _config = ConfigStore.Load();
        Log.Configure(Path.Combine(ConfigStore.DataDirectory, "logs"), _config.EnableDebugLog);
        Log.Info($"ShotTrans 启动 v{Application.ProductVersion}，数据目录：{ConfigStore.DataDirectory}");

        _translation = new TranslationService(_config);
        _history = new HistoryStore(Path.Combine(ConfigStore.DataDirectory, "history.db"));

        _trayIcon = AppIcon.CreateTrayIcon();
        _tray = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "ShotTrans 截图翻译",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _tray.DoubleClick += (_, _) => _ = StartScreenshotAsync();

        _hotkeys = new HotkeyManager();
        _hotkeys.ScreenshotPressed += () => _ = StartScreenshotAsync();
        _hotkeys.SelectionPressed += () => _ = StartSelectionTranslateAsync();

        if (!_hotkeys.TryRegisterInitial(_config, out string error))
        {
            Log.Error("初始快捷键注册失败：" + error);
            ShowToast(error, ToastKind.Error, 5000);
            _startupTimer = new System.Windows.Forms.Timer { Interval = 700 };
            _startupTimer.Tick += (_, _) =>
            {
                _startupTimer.Stop();
                _startupTimer.Dispose();
                OpenSettings();
            };
            _startupTimer.Start();
        }
        else
        {
            _startupTimer = new System.Windows.Forms.Timer { Interval = 700 };
            _startupTimer.Tick += (_, _) => OnStartupTick();
            _startupTimer.Start();
        }
    }

    private void OnStartupTick()
    {
        _startupTimer.Stop();
        _startupTimer.Dispose();

        if (_startupWindow == "history")
        {
            OpenHistory();
            return;
        }
        if (_startupWindow == "settings")
        {
            OpenSettings();
            return;
        }

        if (!_config.IsTranslationConfigured)
        {
            ShowToast("请先在设置中配置翻译接口", ToastKind.Info, 3000);
            OpenSettings();
        }
        else
        {
            ShowToast($"已就绪 · {_hotkeys.ScreenshotHotkey.ToDisplayString()} 截图翻译", ToastKind.Success, 2200);
        }
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        var shot = new ToolStripMenuItem("截图翻译") { Font = new Font(menu.Font, FontStyle.Bold) };
        var selection = new ToolStripMenuItem("划词翻译");
        var history = new ToolStripMenuItem("历史记录");
        var settings = new ToolStripMenuItem("设置");
        var autoStart = new ToolStripMenuItem("开机自启") { CheckOnClick = true };
        var exit = new ToolStripMenuItem("退出");

        shot.Click += (_, _) => _ = StartScreenshotAsync();
        selection.Click += (_, _) => _ = StartSelectionTranslateAsync();
        history.Click += (_, _) => OpenHistory();
        settings.Click += (_, _) => OpenSettings();
        autoStart.Click += (_, _) =>
        {
            bool want = autoStart.Checked;
            try
            {
                StartupManager.Apply(want);
                _config.AutoStart = want;
                ConfigStore.Save(_config);
            }
            catch (Exception ex)
            {
                autoStart.Checked = !want;
                ShowToast("开机自启设置失败：" + ex.Message, ToastKind.Error, 3500);
            }
        };
        exit.Click += (_, _) => Shutdown();

        menu.Items.Add(shot);
        menu.Items.Add(selection);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(history);
        menu.Items.Add(settings);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autoStart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        menu.Opening += (_, _) =>
        {
            shot.Text = "截图翻译" + (_hotkeys.ScreenshotHotkey.IsEmpty ? "" : "    " + _hotkeys.ScreenshotHotkey.ToDisplayString());
            selection.Text = "划词翻译" + (_hotkeys.SelectionHotkey.IsEmpty ? "" : "    " + _hotkeys.SelectionHotkey.ToDisplayString());
            try { autoStart.Checked = StartupManager.IsEnabled(); } catch { autoStart.Checked = _config.AutoStart; }
        };

        return menu;
    }

    private void OpenSettings()
    {
        if (_exiting) return;
        if (_settings is { IsDisposed: false })
        {
            _settings.Activate();
            return;
        }

        var form = new SettingsForm(_config, ApplyConfig);
        _settings = form;
        _modalOpen = true;
        try
        {
            form.ShowDialog();
        }
        finally
        {
            _modalOpen = false;
            _settings = null;
            try { form.Dispose(); } catch { }
        }
    }

    private string? ApplyConfig(AppConfig updated)
    {
        if (!Hotkey.TryParse(updated.ScreenshotHotkey, out var shot, out string error1)) return error1;
        if (!Hotkey.TryParse(updated.SelectionHotkey, out var sel, out string error2)) return error2;

        Hotkey.TryParse(_config.ScreenshotHotkey, out var oldShot, out _);
        Hotkey.TryParse(_config.SelectionHotkey, out var oldSel, out _);

        if (!_hotkeys.TryReregister(shot, sel, out string error))
            return error;

        try
        {
            StartupManager.Apply(updated.AutoStart);
        }
        catch (Exception ex)
        {
            _hotkeys.TryReregister(oldShot, oldSel, out _);
            return "开机自启设置失败：" + ex.Message;
        }

        try
        {
            _config.CopyFrom(updated);
            ConfigStore.Save(_config);
        }
        catch (Exception ex)
        {
            _hotkeys.TryReregister(oldShot, oldSel, out _);
            Log.Error("保存配置失败", ex);
            return "保存配置失败：" + ex.Message;
        }

        _translation.ClearCache();
        ShotTrans.Ocr.OcrService.ClearEngineCache();
        Log.Configure(Path.Combine(ConfigStore.DataDirectory, "logs"), _config.EnableDebugLog);
        Log.Info("设置已更新，热键：" + _config.ScreenshotHotkey + " / " + _config.SelectionHotkey);
        return null;
    }

    private void OpenHistory()
    {
        if (_exiting) return;
        if (_historyForm is { IsDisposed: false })
        {
            _historyForm.Activate();
            return;
        }
        var form = new HistoryForm(_history, _config);
        _historyForm = form;
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_historyForm, form)) _historyForm = null;
        };
        form.Show();
    }

    private async Task StartScreenshotAsync()
    {
        if (_exiting || _busy || _modalOpen) return;
        if (!_config.IsTranslationConfigured)
        {
            ShowToast("请先在设置中配置翻译接口", ToastKind.Error, 2600);
            OpenSettings();
            return;
        }

        _busy = true;
        Rectangle? selection = null;
        try
        {
            CloseTransient();
            using var selectionWindow = new SelectionOverlay();
            selection = await selectionWindow.PickAsync();
        }
        catch (Exception ex)
        {
            Log.Error("框选失败", ex);
            ShowToast("框选失败：" + ex.Message, ToastKind.Error, 3000);
            return;
        }
        finally
        {
            _busy = false;
        }

        if (selection is not { } region || region.Width < 40 || region.Height < 20) return;

        Bitmap screenshot;
        try
        {
            screenshot = ScreenCapture.Capture(region);
        }
        catch (Exception ex)
        {
            Log.Error("截图失败", ex);
            ShowToast("截图失败：" + ex.Message, ToastKind.Error, 3000, region);
            return;
        }

        if (ScreenCapture.IsBlank(screenshot))
        {
            screenshot.Dispose();
            ShowToast("无法截取该画面（可能是独占全屏），请改用无边框窗口模式", ToastKind.Error, 4000, region);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        OcrResultData ocr;
        try
        {
            ocr = await OcrService.RecognizeAsync(screenshot, _config.OcrLanguage);
        }
        catch (Exception ex)
        {
            screenshot.Dispose();
            Log.Error("OCR 失败", ex);
            ShowToast("OCR 失败：" + ex.Message, ToastKind.Error, 4000, region);
            return;
        }

        if (ocr.Lines.Count == 0)
        {
            screenshot.Dispose();
            ShowToast("没识别到文字", ToastKind.Info, 1200, region);
            return;
        }

        Log.Info($"OCR 完成：{ocr.Lines.Count} 行，{stopwatch.ElapsedMilliseconds}ms，区域 {region.Width}x{region.Height} @ ({region.X},{region.Y})");

        var overlay = new ResultOverlay(screenshot, ocr.Lines, region, _config);
        _overlay = overlay;
        overlay.Closed += () =>
        {
            if (ReferenceEquals(_overlay, overlay)) _overlay = null;
        };
        overlay.RetryRequested += () => _ = RetryTranslationAsync(overlay);
        overlay.NotifyRequested += message => ShowToast(message, ToastKind.Success, 1500, region);

        try
        {
            overlay.ShowBusyOriginal();
            Log.Info($"覆盖层已显示（原文占位）：{stopwatch.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Log.Error("显示覆盖层失败", ex);
            overlay.Close();
            ShowToast("显示覆盖层失败：" + ex.Message, ToastKind.Error, 3500, region);
            return;
        }

        _ = TranslateIntoOverlayAsync(overlay, stopwatch);
    }

    private async Task TranslateIntoOverlayAsync(ResultOverlay overlay, Stopwatch stopwatch)
    {
        var texts = overlay.GetOriginalTexts();
        try
        {
            var result = await _translation.TranslateBlocksAsync(texts, overlay.CancellationToken);
            if (overlay.IsClosed) return;

            stopwatch.Stop();
            overlay.ShowTranslated(result.Texts, stopwatch.Elapsed.TotalSeconds);
            Log.Info($"翻译完成：{result.TranslatedCount} 条，总计 {stopwatch.ElapsedMilliseconds}ms");
            SaveScreenshotHistory(overlay, result.Texts);
        }
        catch (OperationCanceledException)
        {
            // 用户关闭了覆盖层
        }
        catch (Exception ex)
        {
            Log.Error("翻译失败", ex);
            if (!overlay.IsClosed)
            {
                stopwatch.Stop();
                overlay.ShowError(ex.Message);
            }
        }
    }

    private async Task RetryTranslationAsync(ResultOverlay overlay)
    {
        if (overlay.IsClosed) return;
        overlay.ShowRetrying();
        var texts = overlay.GetOriginalTexts();
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _translation.TranslateBlocksAsync(texts, overlay.CancellationToken);
            if (overlay.IsClosed) return;
            overlay.ShowTranslated(result.Texts, sw.Elapsed.TotalSeconds);
            Log.Info($"重试翻译成功：{result.TranslatedCount} 条，{sw.ElapsedMilliseconds}ms");
            SaveScreenshotHistory(overlay, result.Texts);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("重试翻译失败", ex);
            if (!overlay.IsClosed) overlay.ShowError(ex.Message);
        }
    }

    private void SaveScreenshotHistory(ResultOverlay overlay, IReadOnlyList<string> translated)
    {
        if (!_config.HistoryEnabled) return;
        try
        {
            var original = string.Join("\n", overlay.GetOriginalTexts().Where(t => !string.IsNullOrWhiteSpace(t)));
            var result = string.Join("\n", translated.Where(t => !string.IsNullOrWhiteSpace(t)));
            if (original.Length == 0 && result.Length == 0) return;
            var thumbnail = overlay.CreateThumbnail();
            Task.Run(() => _history.Add("screenshot", thumbnail, original, result));
        }
        catch (Exception ex)
        {
            Log.Error("保存截图历史失败", ex);
        }
    }

    private async Task StartSelectionTranslateAsync()
    {
        if (_exiting || _busy || _modalOpen) return;
        if (!_config.IsTranslationConfigured)
        {
            ShowToast("请先在设置中配置翻译接口", ToastKind.Error, 2600);
            OpenSettings();
            return;
        }

        _busy = true;
        CloseSelectionPopup();
        string? text;
        var anchor = Cursor.Position;
        try
        {
            text = await ClipboardHelper.CaptureSelectedTextAsync();
        }
        catch (Exception ex)
        {
            Log.Error("读取选区失败", ex);
            ShowToast("读取选区失败：" + ex.Message, ToastKind.Error, 3000);
            return;
        }
        finally
        {
            _busy = false;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            ShowToast("未选中文字", ToastKind.Info, 1400);
            return;
        }

        text = text.Trim();
        if (text.Length > 6000) text = text[..6000];

        bool isChinese = IsMostlyChinese(text);
        if (isChinese && !_config.SelectionBidirectionalEnabled)
        {
            var chinesePopup = new SelectionTranslatePopup(anchor, Theme.IsDark(_config.UiTheme));
            _popup = chinesePopup;
            chinesePopup.ShowAlreadyChinese(text);
            SaveSelectionHistory(text, text);
            if (_config.SelectionExplainEnabled)
            {
                var explanation = await FetchExplanationAsync(text, chinesePopup.CancellationToken);
                if (!chinesePopup.IsClosed && explanation != null)
                    chinesePopup.ShowExplanation(explanation);
            }
            return;
        }

        // 中英互译开启时，纯中文选区译成英文；否则保持译成中文。
        bool toEnglish = isChinese;
        string? title = toEnglish ? "划词翻译 · 中文 → 英文" : null;

        var popup = new SelectionTranslatePopup(anchor, Theme.IsDark(_config.UiTheme));
        _popup = popup;
        popup.ShowLoading(text, title);

        Task<string?>? explainTask = _config.SelectionExplainEnabled
            ? FetchExplanationAsync(text, popup.CancellationToken)
            : null;

        try
        {
            var translated = await _translation.TranslateTextAsync(text, popup.CancellationToken, toEnglish);
            if (popup.IsClosed) return;
            popup.ShowResult(translated, title);
            SaveSelectionHistory(text, translated);

            if (explainTask != null)
            {
                var explanation = await explainTask;
                if (!popup.IsClosed && explanation != null)
                    popup.ShowExplanation(explanation);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("划词翻译失败", ex);
            if (!popup.IsClosed) popup.ShowError(ex.Message);
        }
    }

    /// <summary>生成选中的原文的简短词义/介绍；失败时返回 null。</summary>
    private async Task<string?> FetchExplanationAsync(string text, CancellationToken ct)
    {
        try
        {
            var explanation = await _translation.ExplainAsync(text, ct);
            return string.IsNullOrWhiteSpace(explanation) ? null : explanation.Trim();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("生成词义失败：" + ex.Message);
            return null;
        }
    }

    private void SaveSelectionHistory(string original, string translated)
    {
        if (!_config.HistoryEnabled) return;
        try
        {
            Task.Run(() => _history.Add("selection", null, original, translated));
        }
        catch (Exception ex)
        {
            Log.Error("保存划词历史失败", ex);
        }
    }

    private static bool IsMostlyChinese(string text)
    {
        int cjk = 0, latin = 0;
        foreach (char c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) cjk++;
            else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) latin++;
        }
        return cjk > 0 && latin == 0;
    }

    private void CloseTransient()
    {
        CloseOverlay();
        CloseSelectionPopup();
        CloseToast();
    }

    private void CloseOverlay()
    {
        var overlay = _overlay;
        _overlay = null;
        try { overlay?.Close(); } catch { }
    }

    private void CloseSelectionPopup()
    {
        var popup = _popup;
        _popup = null;
        try { popup?.Close(); } catch { }
    }

    private void CloseToast()
    {
        var toast = _toast;
        _toast = null;
        try { toast?.Dispose(); } catch { }
    }

    private void ShowToast(string message, ToastKind kind = ToastKind.Info, int duration = 1800, Rectangle? anchor = null)
    {
        try
        {
            CloseToast();
            var rect = anchor ?? new Rectangle(Cursor.Position, new Size(1, 1));
            _toast = new ToastWindow(message, kind, rect, duration, Theme.IsDark(_config.UiTheme));
        }
        catch (Exception ex)
        {
            Log.Error("显示提示失败：" + message, ex);
        }
    }

    private void Shutdown()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("ShotTrans 退出");
        CloseTransient();
        try { _historyForm?.Close(); } catch { }
        try { _settings?.Close(); } catch { }
        _hotkeys.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _trayIcon.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _startupTimer.Dispose(); } catch { }
            try { _hotkeys.Dispose(); } catch { }
            try { _tray.Dispose(); } catch { }
            try { _trayIcon.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
