using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using ShotTrans.Input;
using ShotTrans.Ocr;
using ShotTrans.Translation;
using ShotTrans.Ui;

namespace ShotTrans.Settings;

internal sealed class SettingsForm : Form
{
    private readonly Func<AppConfig, string?> _apply;
    private readonly AppConfig _original;

    private readonly HotkeyTextBox _shotHotkey = new();
    private readonly HotkeyTextBox _selHotkey = new();
    private readonly ComboBox _ocrLang = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _fontMin = new() { Minimum = 6, Maximum = 48, Width = 100 };
    private readonly NumericUpDown _fontMax = new() { Minimum = 6, Maximum = 64, Width = 100 };
    private readonly NumericUpDown _opacity = new() { Minimum = 30, Maximum = 100, Width = 100 };
    private readonly NumericUpDown _fadeSeconds = new() { Minimum = 0, Maximum = 30, Width = 100 };
    private readonly ComboBox _align = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _theme = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _history = new() { Text = "启用本地历史记录（最近 200 条，含缩略图，存 SQLite）", AutoSize = true };
    private readonly CheckBox _autoStart = new() { Text = "开机自启（启动文件夹快捷方式，不写注册表）", AutoSize = true };
    private readonly CheckBox _debugLog = new() { Text = "启用调试日志（写入 logs 目录，默认关闭）", AutoSize = true };
    private readonly CheckBox _protectCode = new() { Text = "自动保护代码标识符 / 路径 / URL / 版本号", AutoSize = true };
    private readonly CheckBox _selExplain = new() { Text = "划词翻译时在译文下方显示原文的简短词义/介绍", AutoSize = true };
    private readonly CheckBox _selBidirectional = new() { Text = "划词中英互译（选中中文时译成英文；关闭时中文只显示原文）", AutoSize = true };

    private readonly TextBox _baseUrl = new();
    private readonly TextBox _apiKey = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _showKey = new() { Text = "显示", AutoSize = true };
    private readonly TextBox _model = new();
    private readonly NumericUpDown _temperature = new() { Minimum = 0, Maximum = 2, DecimalPlaces = 2, Increment = 0.1m, Width = 100 };
    private readonly NumericUpDown _timeout = new() { Minimum = 3, Maximum = 120, Width = 100 };
    private readonly Button _test = new() { Text = "测试连接", Width = 100, Height = 28 };
    private readonly Label _testResult = new() { AutoSize = false, Height = 44, TextAlign = ContentAlignment.MiddleLeft };

    private readonly TextBox _glossary = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        AcceptsReturn = true,
        WordWrap = false,
        Height = 300
    };

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly Button _save = new() { Text = "保存", Width = 92, Height = 32 };
    private readonly Button _cancel = new() { Text = "取消", Width = 92, Height = 32 };

    public SettingsForm(AppConfig config, Func<AppConfig, string?> apply)
    {
        _original = config.Clone();
        _apply = apply;

        Text = "ShotTrans 设置";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 800);
        MinimumSize = new Size(720, 640);
        Font = new Font("Microsoft YaHei UI", 9F);
        Icon = AppIcon.CreateTrayIcon();
        ShowInTaskbar = true;
        MaximizeBox = false;
        MinimizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;

        BuildTabs();
        BuildButtons();
        LoadValues(config);

        _save.Click += (_, _) => SaveAndClose();
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        AcceptButton = _save;
        CancelButton = _cancel;

        if (!config.IsTranslationConfigured)
            _tabs.SelectedIndex = 1;
    }

    private void BuildTabs()
    {
        _tabs.TabPages.Add(BuildGeneralTab());
        _tabs.TabPages.Add(BuildTranslationTab());
        _tabs.TabPages.Add(BuildGlossaryTab());
        _tabs.TabPages.Add(BuildAboutTab());

        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        content.Controls.Add(_tabs);
        Controls.Add(content);
    }

    private void BuildButtons()
    {
        var panel = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(12, 8, 12, 8) };
        _save.Left = panel.Width - 208;
        _save.Top = 10;
        _cancel.Left = panel.Width - 108;
        _cancel.Top = 10;
        _save.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        _cancel.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        panel.Resize += (_, _) =>
        {
            _save.Left = panel.Width - 208;
            _cancel.Left = panel.Width - 108;
        };
        panel.Controls.Add(_save);
        panel.Controls.Add(_cancel);
        Controls.Add(panel);
    }

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage("常规") { Padding = new Padding(8) };
        var table = CreateTable();

        AddRow(table, "截图翻译热键", _shotHotkey);
        AddRow(table, "划词翻译热键", _selHotkey);
        AddRow(table, "OCR 语言", _ocrLang);
        AddRow(table, "", new Label
        {
            Text = "英文识别建议在 Windows 设置 → 时间和语言 → 语言中安装英语 OCR 语言包；未安装时会回退到系统中可用的模型。",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = Color.DimGray
        }, 58);

        var ocrActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
        var installOcr = new Button { Text = "安装英文 OCR…", Width = 130, Height = 28, Margin = new Padding(0, 4, 8, 0) };
        var refreshOcr = new Button { Text = "刷新语言", Width = 96, Height = 28, Margin = new Padding(0, 4, 0, 0) };
        installOcr.Click += (_, _) => InstallEnglishOcr();
        refreshOcr.Click += (_, _) =>
        {
            RefreshOcrLanguages();
            MessageBox.Show(this, "已刷新。可用 OCR 语言：\n" +
                string.Join("\n", OcrService.AvailableLanguages().Select(l => l.LanguageTag + " — " + l.DisplayName)),
                "ShotTrans", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        ocrActions.Controls.Add(installOcr);
        ocrActions.Controls.Add(refreshOcr);
        AddRow(table, "OCR 语言包", ocrActions, 40);
        AddRow(table, "最小字号 (px)", _fontMin);
        AddRow(table, "最大字号 (px)", _fontMax);
        AddRow(table, "背景不透明度 (%)", _opacity);
        AddRow(table, "状态条淡出 (秒)", _fadeSeconds);
        AddRow(table, "译文对齐", _align);
        AddRow(table, "界面主题（弹窗）", _theme);
        AddRow(table, "", new Label
        {
            Text = "字号为物理像素；背景不透明度越低越能透出原图。Esc 可取消按键捕获。",
            AutoSize = true,
            ForeColor = Color.DimGray
        });
        AddRow(table, "", _selExplain);
        AddRow(table, "", _selBidirectional);
        AddRow(table, "", _protectCode);
        AddRow(table, "", _history);
        AddRow(table, "", _autoStart);
        AddRow(table, "", _debugLog);
        AddRow(table, "", new Label
        {
            Text = "调试日志默认关闭：关闭时不会创建或写入 logs 目录；排查问题时再开启。",
            AutoSize = true,
            ForeColor = Color.DimGray
        }, 30);

        page.Controls.Add(table);
        return page;
    }

    private TabPage BuildTranslationTab()
    {
        var page = new TabPage("翻译接口") { Padding = new Padding(8) };
        var table = CreateTable();

        AddRow(table, "Base URL", _baseUrl);
        AddRow(table, "API Key", BuildApiKeyRow());
        AddRow(table, "模型", _model);
        AddRow(table, "温度", _temperature);
        AddRow(table, "超时 (秒)", _timeout);
        AddRow(table, "连接测试", BuildTestRow());
        AddRow(table, "", new Label
        {
            Text = "兼容 OpenAI 格式的 /chat/completions 接口。可填根地址（如 https://host/v1）或完整地址。\n" +
                   "隐私：截图与 OCR 结果只留在内存中，只有识别出的文字会发送到该接口。",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = Color.DimGray
        }, 72);

        page.Controls.Add(table);
        return page;
    }

    private Control BuildApiKeyRow()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        _apiKey.Dock = DockStyle.Fill;
        _showKey.Dock = DockStyle.Fill;
        _showKey.CheckedChanged += (_, _) => _apiKey.UseSystemPasswordChar = !_showKey.Checked;
        panel.Controls.Add(_apiKey, 0, 0);
        panel.Controls.Add(_showKey, 1, 0);
        return panel;
    }

    private Control BuildTestRow()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
        _test.Margin = new Padding(0, 4, 8, 0);
        _testResult.AutoSize = true;
        _testResult.MaximumSize = new Size(480, 0);
        _test.Click += async (_, _) => await RunTestAsync();
        panel.Controls.Add(_test);
        panel.Controls.Add(_testResult);
        return panel;
    }

    private TabPage BuildGlossaryTab()
    {
        var page = new TabPage("术语表") { Padding = new Padding(8) };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8),
            AutoScroll = true
        };
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

        var help = new Label
        {
            Dock = DockStyle.Fill,
            Text = "每行一条：原始词=译文。例如：\nGetProcAddress=获取函数地址",
            ForeColor = Color.DimGray
        };
        _glossary.Dock = DockStyle.Fill;
        var note = new Label
        {
            Dock = DockStyle.Fill,
            Text = "代码标识符、路径、URL、版本号会由程序自动保护，不需要手工添加。",
            ForeColor = Color.DimGray
        };
        table.Controls.Add(help, 0, 0);
        table.Controls.Add(_glossary, 0, 1);
        table.Controls.Add(note, 0, 2);
        page.Controls.Add(table);
        return page;
    }

    private TabPage BuildAboutTab()
    {
        var page = new TabPage("关于") { Padding = new Padding(16) };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        void AddLabel(string text, Font? font = null, int width = 680, Color? color = null)
        {
            panel.Controls.Add(new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(width, 0),
                Font = font ?? Font,
                ForeColor = color ?? Color.Black,
                Margin = new Padding(0, 0, 0, 8)
            });
        }

        AddLabel("ShotTrans 截图翻译  v" + Application.ProductVersion, new Font("Microsoft YaHei UI", 13F, FontStyle.Bold));
        AddLabel("热键触发框选 → 本地 Windows OCR → OpenAI 兼容接口翻译 → 原位覆盖译文。");
        AddLabel("· 译文只覆盖识别出的文字块，不改变原图文件。\n· 覆盖期间可点击下方窗口；按住 Space 看原文；Esc / 右键 / 点击外部关闭。\n· 划词翻译：选中文本后按热键，自动复制并显示浮动译文，Ctrl+C 复制。\n· 历史记录默认关闭，数据只保存在本机。");
        AddLabel("数据目录：" + ConfigStore.DataDirectory, color: Color.DimGray);
        AddLabel("已知限制：独占全屏（Exclusive Fullscreen）游戏无法截取，请改用无边框窗口模式。", color: Color.FromArgb(160, 90, 60));

        var openDir = new Button { Text = "打开数据目录", Width = 120, Height = 30 };
        openDir.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ConfigStore.DataDirectory}\"") { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "ShotTrans", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        panel.Controls.Add(openDir);

        page.Controls.Add(panel);
        return page;
    }

    private static TableLayoutPanel CreateTable()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoScroll = true,
            Padding = new Padding(8)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    private static void AddRow(TableLayoutPanel table, string labelText, Control control, int height = 36)
    {
        int row = table.RowCount;
        table.RowCount = row + 1;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        var label = new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 4, 0, 4);
        table.Controls.Add(label, 0, row);
        table.Controls.Add(control, 1, row);
    }

    private sealed record OcrItem(string Display, string Tag)
    {
        public override string ToString() => Display;
    }

    private void RefreshOcrLanguages(string? selectedTag = null)
    {
        OcrService.ClearEngineCache();
        selectedTag ??= (_ocrLang.SelectedItem as OcrItem)?.Tag ?? _original.OcrLanguage;
        _ocrLang.Items.Clear();
        _ocrLang.Items.Add(new OcrItem("自动（跟随系统语言）", "auto"));
        try
        {
            foreach (var lang in OcrService.AvailableLanguages())
            {
                if (_ocrLang.Items.Cast<OcrItem>().Any(i => i.Tag == lang.LanguageTag)) continue;
                _ocrLang.Items.Add(new OcrItem($"{lang.LanguageTag} — {lang.DisplayName}", lang.LanguageTag));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("枚举 OCR 语言失败：" + ex.Message);
        }
        if (!_ocrLang.Items.Cast<OcrItem>().Any(i => i.Tag == selectedTag))
            _ocrLang.Items.Add(new OcrItem(selectedTag + "（未安装？）", selectedTag));
        _ocrLang.SelectedItem = _ocrLang.Items.Cast<OcrItem>().FirstOrDefault(i => i.Tag == selectedTag);
        if (_ocrLang.SelectedIndex < 0) _ocrLang.SelectedIndex = 0;
    }

    private void InstallEnglishOcr()
    {
        if (OcrService.AvailableLanguages().Any(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "英文 OCR 语言包已安装，可直接在「OCR 语言」中选择 en-US。", "ShotTrans",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            "将通过 Windows 更新安装英文 OCR 语言包，需要联网并请求管理员权限。\n\n继续？",
            "ShotTrans", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        try
        {
            const string script =
                "Add-WindowsCapability -Online -Name 'Language.OCR~~~en-US~0.0.1.0'; " +
                "Write-Host ''; Write-Host '完成后请关闭此窗口，回到 ShotTrans 设置点击“刷新语言”并选择 en-US。'; " +
                "$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')";
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -NoExit -Command \"" + script + "\"")
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
            MessageBox.Show(this,
                "安装窗口已打开。安装完成后回到此页点击「刷新语言」，并选择 en-US。",
                "ShotTrans", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户取消了 UAC
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "启动安装失败：" + ex.Message, "ShotTrans",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadValues(AppConfig c)
    {
        if (!Hotkey.TryParse(c.ScreenshotHotkey, out var shot, out _))
            Hotkey.TryParse("Ctrl+Alt+X", out shot, out _);
        if (!Hotkey.TryParse(c.SelectionHotkey, out var sel, out _))
            Hotkey.TryParse("Ctrl+Alt+Z", out sel, out _);
        _shotHotkey.Hotkey = shot;
        _selHotkey.Hotkey = sel;

        RefreshOcrLanguages(c.OcrLanguage);

        _fontMin.Value = Math.Clamp(c.FontMinSize, 6, 48);
        _fontMax.Value = Math.Clamp(c.FontMaxSize, (int)_fontMin.Value, 64);
        _opacity.Value = (decimal)Math.Round(Math.Clamp(c.BackgroundOpacity, 0.3, 1.0) * 100);
        _fadeSeconds.Value = Math.Clamp(c.StatusBarFadeSeconds, 0, 30);
        _align.Items.Clear();
        _align.Items.Add("左对齐");
        _align.Items.Add("居中");
        _align.SelectedIndex = c.AlignCenter ? 1 : 0;

        _theme.Items.Clear();
        _theme.Items.Add("跟随系统");
        _theme.Items.Add("明亮");
        _theme.Items.Add("黑暗");
        _theme.SelectedIndex = c.UiTheme switch { "light" => 1, "dark" => 2, _ => 0 };

        _protectCode.Checked = c.ProtectCodeIdentifiers;
        _selExplain.Checked = c.SelectionExplainEnabled;
        _selBidirectional.Checked = c.SelectionBidirectionalEnabled;
        _history.Checked = c.HistoryEnabled;
        _autoStart.Checked = c.AutoStart;
        _debugLog.Checked = c.EnableDebugLog;

        _baseUrl.Text = c.TranslationBaseUrl;
        _apiKey.Text = c.TranslationApiKey;
        _model.Text = c.TranslationModel;
        _temperature.Value = (decimal)Math.Clamp(c.TranslationTemperature, 0, 2);
        _timeout.Value = Math.Clamp(c.TranslationTimeoutSeconds, 3, 120);
        _glossary.Text = c.Glossary.Replace("\r\n", "\n");
    }

    private AppConfig ReadValues()
    {
        var c = _original.Clone();
        c.ScreenshotHotkey = _shotHotkey.Hotkey.ToDisplayString();
        c.SelectionHotkey = _selHotkey.Hotkey.ToDisplayString();
        c.OcrLanguage = _ocrLang.SelectedItem is OcrItem item ? item.Tag : "auto";
        c.FontMinSize = (int)_fontMin.Value;
        c.FontMaxSize = (int)_fontMax.Value;
        c.BackgroundOpacity = (double)_opacity.Value / 100.0;
        c.StatusBarFadeSeconds = (int)_fadeSeconds.Value;
        c.AlignCenter = _align.SelectedIndex == 1;
        c.UiTheme = _theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
        c.ProtectCodeIdentifiers = _protectCode.Checked;
        c.SelectionExplainEnabled = _selExplain.Checked;
        c.SelectionBidirectionalEnabled = _selBidirectional.Checked;
        c.HistoryEnabled = _history.Checked;
        c.AutoStart = _autoStart.Checked;
        c.EnableDebugLog = _debugLog.Checked;
        c.TranslationBaseUrl = _baseUrl.Text.Trim();
        c.TranslationApiKey = _apiKey.Text.Trim();
        c.TranslationModel = _model.Text.Trim();
        c.TranslationTemperature = (double)_temperature.Value;
        c.TranslationTimeoutSeconds = (int)_timeout.Value;
        c.Glossary = _glossary.Text.Replace("\r\n", "\n");
        c.FirstRunDone = true;
        return c;
    }

    private bool ValidateValues(AppConfig c)
    {
        if (!Hotkey.Validate(_shotHotkey.Hotkey, out string err1))
        {
            MessageBox.Show(this, "截图翻译热键：" + err1, "ShotTrans", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _tabs.SelectedIndex = 0;
            _shotHotkey.Focus();
            return false;
        }
        if (!Hotkey.Validate(_selHotkey.Hotkey, out string err2))
        {
            MessageBox.Show(this, "划词翻译热键：" + err2, "ShotTrans", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _tabs.SelectedIndex = 0;
            _selHotkey.Focus();
            return false;
        }
        if (_shotHotkey.Hotkey == _selHotkey.Hotkey)
        {
            MessageBox.Show(this, "两个热键不能相同。", "ShotTrans", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _tabs.SelectedIndex = 0;
            return false;
        }
        if (!string.IsNullOrWhiteSpace(c.TranslationBaseUrl))
        {
            try { TranslationService.BuildEndpoint(c.TranslationBaseUrl); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "ShotTrans", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _tabs.SelectedIndex = 1;
                _baseUrl.Focus();
                return false;
            }
        }
        return true;
    }

    private void SaveAndClose()
    {
        var c = ReadValues();
        if (!ValidateValues(c)) return;
        string? error;
        try
        {
            error = _apply(c);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        if (error != null)
        {
            MessageBox.Show(this, error, "ShotTrans", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private async Task RunTestAsync()
    {
        _test.Enabled = false;
        _testResult.ForeColor = Color.DimGray;
        _testResult.Text = "正在连接…";
        try
        {
            var c = ReadValues();
            if (string.IsNullOrWhiteSpace(c.TranslationBaseUrl) || string.IsNullOrWhiteSpace(c.TranslationModel))
            {
                _testResult.ForeColor = Color.Firebrick;
                _testResult.Text = "请先填写 Base URL 和模型。";
                return;
            }
            var service = new TranslationService(c);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(c.TranslationTimeoutSeconds + 5, 8, 120)));
            var result = await service.TestAsync(cts.Token);
            _testResult.ForeColor = result.Success ? Color.SeaGreen : Color.Firebrick;
            _testResult.Text = result.Success
                ? $"成功（{result.Elapsed.TotalSeconds:0.0}s）：{result.Message}"
                : "失败：" + result.Message;
        }
        catch (Exception ex)
        {
            _testResult.ForeColor = Color.Firebrick;
            _testResult.Text = "失败：" + ex.Message;
        }
        finally
        {
            _test.Enabled = true;
        }
    }
}
