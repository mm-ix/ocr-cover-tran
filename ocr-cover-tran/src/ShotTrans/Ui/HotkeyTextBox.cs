using ShotTrans.Input;

namespace ShotTrans.Ui;

/// <summary>按键捕获输入框（只读，点击后按下组合键）。</summary>
internal sealed class HotkeyTextBox : TextBox
{
    private Hotkey _hotkey;
    private Hotkey _snapshot;
    private bool _capturing;
    private bool _invalid;

    public event EventHandler? HotkeyChanged;

    public Hotkey Hotkey
    {
        get => _hotkey;
        set
        {
            _hotkey = value;
            _capturing = false;
            UpdateText();
        }
    }

    public HotkeyTextBox()
    {
        ReadOnly = true;
        Cursor = Cursors.Hand;
        BackColor = Color.White;
        ShortcutsEnabled = false;
        TextAlign = HorizontalAlignment.Left;
        UpdateText();
    }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        StartCapture();
    }

    protected override void OnLeave(EventArgs e)
    {
        if (_capturing)
        {
            _capturing = false;
            UpdateText();
        }
        base.OnLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        StartCapture();
    }

    private void StartCapture()
    {
        _snapshot = _hotkey;
        _capturing = true;
        _invalid = false;
        Text = "请按下快捷键…（Esc 取消）";
        SelectAll();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_capturing) StartCapture();

        e.Handled = true;
        e.SuppressKeyPress = true;

        if (e.KeyCode == Keys.Escape)
        {
            _hotkey = _snapshot;
            _capturing = false;
            _invalid = false;
            UpdateText();
            return;
        }
        if (e.KeyCode == Keys.Tab)
        {
            _capturing = false;
            UpdateText();
            Parent?.SelectNextControl(this, true, true, true, true);
            return;
        }
        if (e.KeyCode is Keys.Back or Keys.Delete &&
            (e.Modifiers & (Keys.Control | Keys.Alt | Keys.Shift)) == 0)
        {
            _hotkey = default;
            _capturing = false;
            UpdateText();
            HotkeyChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var candidate = Hotkey.FromKeys(e.KeyData);
        if (candidate.IsEmpty)
        {
            Text = "请按下组合键…（Esc 取消）";
            SelectAll();
            return;
        }

        if (!Hotkey.Validate(candidate, out string error))
        {
            _invalid = true;
            Text = error;
            SelectAll();
            return;
        }

        _hotkey = candidate;
        _capturing = false;
        _invalid = false;
        UpdateText();
        HotkeyChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateText()
    {
        if (_capturing) return;
        Text = _hotkey.IsEmpty ? "（未设置）" : _hotkey.ToDisplayString();
        if (_invalid) ForeColor = Color.Firebrick;
        else ForeColor = SystemColors.WindowText;
    }
}
