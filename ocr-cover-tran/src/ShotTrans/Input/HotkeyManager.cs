using ShotTrans.Interop;

namespace ShotTrans.Input;

internal sealed class HotkeyManager : IDisposable
{
    private const int IdScreenshot = 1;
    private const int IdSelection = 2;

    private readonly HotkeyWindow _window;
    private Hotkey _screenshot;
    private Hotkey _selection;
    private bool _shotRegistered;
    private bool _selRegistered;
    private bool _disposed;

    public event Action? ScreenshotPressed;
    public event Action? SelectionPressed;

    public Hotkey ScreenshotHotkey => _screenshot;
    public Hotkey SelectionHotkey => _selection;

    public HotkeyManager()
    {
        _window = new HotkeyWindow();
        _window.HotkeyPressed += id =>
        {
            if (id == IdScreenshot) ScreenshotPressed?.Invoke();
            else if (id == IdSelection) SelectionPressed?.Invoke();
        };
    }

    public bool TryRegisterInitial(AppConfig config, out string error)
    {
        if (!Hotkey.TryParse(config.ScreenshotHotkey, out var shot, out error)) return false;
        if (!Hotkey.TryParse(config.SelectionHotkey, out var sel, out error)) return false;
        if (shot == sel)
        {
            error = "截图翻译和划词翻译不能使用同一个快捷键";
            return false;
        }

        _screenshot = shot;
        _selection = sel;
        if (!Register(IdScreenshot, shot, out error)) return false;
        _shotRegistered = true;
        if (!Register(IdSelection, sel, out error))
        {
            NativeMethods.UnregisterHotKey(_window.Handle, IdScreenshot);
            _shotRegistered = false;
            return false;
        }
        _selRegistered = true;
        return true;
    }

    /// <summary>原子式重注册：失败时回滚到旧设置。</summary>
    public bool TryReregister(Hotkey shot, Hotkey sel, out string error)
    {
        error = "";
        if (shot == sel)
        {
            error = "截图翻译和划词翻译不能使用同一个快捷键";
            return false;
        }

        var oldShot = _screenshot;
        var oldSel = _selection;
        var oldShotReg = _shotRegistered;
        var oldSelReg = _selRegistered;

        UnregisterAll();
        if (!Register(IdScreenshot, shot, out error))
        {
            Restore(oldShot, oldSel, oldShotReg, oldSelReg);
            return false;
        }
        _shotRegistered = true;
        if (!Register(IdSelection, sel, out error))
        {
            NativeMethods.UnregisterHotKey(_window.Handle, IdScreenshot);
            _shotRegistered = false;
            Restore(oldShot, oldSel, oldShotReg, oldSelReg);
            return false;
        }
        _selRegistered = true;
        _screenshot = shot;
        _selection = sel;
        return true;
    }

    private void Restore(Hotkey oldShot, Hotkey oldSel, bool oldShotReg, bool oldSelReg)
    {
        _screenshot = oldShot;
        _selection = oldSel;
        if (oldShotReg)
        {
            Register(IdScreenshot, oldShot, out _);
            _shotRegistered = true;
        }
        if (oldSelReg)
        {
            Register(IdSelection, oldSel, out _);
            _selRegistered = true;
        }
    }

    private bool Register(int id, Hotkey hotkey, out string error)
    {
        error = "";
        if (hotkey.IsEmpty)
        {
            error = "快捷键无效";
            return false;
        }
        if (!NativeMethods.RegisterHotKey(_window.Handle, id, hotkey.Modifiers | NativeHotkeyValues.ModNoRepeat, hotkey.VirtualKey))
        {
            int code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            error = $"注册快捷键 {hotkey.ToDisplayString()} 失败（可能已被其他程序占用，错误码 {code}）";
            Log.Warn(error);
            return false;
        }
        return true;
    }

    public void UnregisterAll()
    {
        if (_disposed) return;
        try
        {
            if (_shotRegistered) NativeMethods.UnregisterHotKey(_window.Handle, IdScreenshot);
            if (_selRegistered) NativeMethods.UnregisterHotKey(_window.Handle, IdSelection);
        }
        catch { }
        _shotRegistered = false;
        _selRegistered = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
        try { _window.DestroyHandle(); } catch { }
    }

    private sealed class HotkeyWindow : NativeWindow
    {
        private static readonly IntPtr HWND_MESSAGE = new(-3);
        public event Action<int>? HotkeyPressed;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "ShotTrans.HotkeyWindow",
                Parent = HWND_MESSAGE
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY)
            {
                HotkeyPressed?.Invoke(m.WParam.ToInt32());
                return;
            }
            base.WndProc(ref m);
        }
    }
}
