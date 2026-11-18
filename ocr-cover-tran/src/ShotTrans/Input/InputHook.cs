using System.Runtime.InteropServices;
using ShotTrans.Interop;

namespace ShotTrans.Input;

/// <summary>会话期间的全局键鼠监听（低级钩子）。</summary>
internal sealed class InputHook : IDisposable
{
    private readonly SynchronizationContext? _context;
    private readonly Action<Action> _post;

    private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
    private NativeMethods.LowLevelMouseProc? _mouseProc;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private bool _disposed;

    private readonly HashSet<int> _downKeys = new();
    private bool _spaceIsDown;
    private bool _swallowCUp;

    public bool HandleEscape { get; set; }
    public bool HandleSpace { get; set; }
    public bool HandleRetry { get; set; }
    public bool HandleCopy { get; set; }
    public bool HandleRightClick { get; set; } = true;
    public bool HandleWheel { get; set; }

    public event Action? EscapePressed;
    public event Action? SpaceDown;
    public event Action? SpaceUp;
    public event Action? RetryPressed;
    public event Action? CopyPressed;
    public event Action<Point, bool>? MouseDown;
    /// <summary>滚轮滚动：参数为屏幕坐标与滚轮增量（120 为一格）。</summary>
    public event Action<Point, int>? WheelScrolled;
    /// <summary>返回 true 时吞掉滚轮事件（避免同时滚动下层窗口）。</summary>
    public Func<Point, bool>? WheelFilter { get; set; }

    public InputHook()
    {
        _context = SynchronizationContext.Current;
        _post = action =>
        {
            if (_context != null) _context.Post(_ => { try { action(); } catch (Exception ex) { Log.Error("输入钩子回调异常", ex); } }, null);
            else { try { action(); } catch (Exception ex) { Log.Error("输入钩子回调异常", ex); } }
        };
    }

    public void InstallKeyboard()
    {
        if (_keyboardHook != IntPtr.Zero) return;
        _keyboardProc = KeyboardProc;
        _keyboardHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL, _keyboardProc, NativeMethods.GetModuleHandle(null), 0);
        if (_keyboardHook == IntPtr.Zero)
            Log.Warn("安装键盘钩子失败，Esc/Space/R/Ctrl+C 可能不可用：" + Marshal.GetLastWin32Error());
        else if (Environment.GetEnvironmentVariable("SHOTRANS_DEBUG_INPUT") == "1")
            Log.Info("键盘钩子已安装");
    }

    public void InstallMouse()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseProc = MouseProc;
        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL, _mouseProc, NativeMethods.GetModuleHandle(null), 0);
        if (_mouseHook == IntPtr.Zero)
            Log.Warn("安装鼠标钩子失败，点击外部关闭可能不可用：" + Marshal.GetLastWin32Error());
        else if (Environment.GetEnvironmentVariable("SHOTRANS_DEBUG_INPUT") == "1")
            Log.Info("鼠标钩子已安装");
    }

    public void Install()
    {
        InstallKeyboard();
        InstallMouse();
    }

    public void UninstallAll()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
            _keyboardProc = null;
        }
        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            _mouseProc = null;
        }
        _downKeys.Clear();
        _spaceIsDown = false;
        _swallowCUp = false;
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        try
        {
            int msg = wParam.ToInt32();
            bool isDown = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
            bool isUp = msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
            var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)info.vkCode;

            if (Environment.GetEnvironmentVariable("SHOTRANS_DEBUG_INPUT") == "1" && isDown)
                Log.Info($"key down vk=0x{vk:X2} ctrl={IsCtrlDown()} alt={IsAltDown()} shift={IsShiftDown()}");

            if (isDown)
            {
                bool firstPress = _downKeys.Add(vk);
                if (vk == NativeMethods.VK_SPACE && HandleSpace && _spaceIsDown)
                    return (IntPtr)1; // 吞掉长按产生的重复事件
                if (!firstPress) return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

                if (vk == NativeMethods.VK_ESCAPE && HandleEscape)
                {
                    _post(() => EscapePressed?.Invoke());
                    return (IntPtr)1;
                }

                if (vk == NativeMethods.VK_SPACE && HandleSpace && !_spaceIsDown && IsPlainNow())
                {
                    _spaceIsDown = true;
                    _post(() => SpaceDown?.Invoke());
                    return (IntPtr)1;
                }

                if (vk == NativeMethods.VK_R && HandleRetry && IsPlainNow())
                {
                    _post(() => RetryPressed?.Invoke());
                    return (IntPtr)1;
                }

                if (vk == NativeMethods.VK_C && HandleCopy && IsCtrlOnlyNow())
                {
                    _swallowCUp = true;
                    _post(() => CopyPressed?.Invoke());
                    return (IntPtr)1;
                }
            }
            else if (isUp)
            {
                _downKeys.Remove(vk);
                if (vk == NativeMethods.VK_C && _swallowCUp)
                {
                    _swallowCUp = false;
                    return (IntPtr)1;
                }
                if (vk == NativeMethods.VK_SPACE && _spaceIsDown && HandleSpace)
                {
                    _spaceIsDown = false;
                    _post(() => SpaceUp?.Invoke());
                    return (IntPtr)1;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("键盘钩子异常", ex);
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private bool IsPlainNow()
    {
        return !IsCtrlDown() && !IsAltDown() && !IsShiftDown()
               && (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LWIN) & 0x8000) == 0
               && (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RWIN) & 0x8000) == 0;
    }

    private bool IsCtrlOnlyNow()
    {
        return IsCtrlDown() && !IsAltDown() && !IsShiftDown();
    }

    private bool IsCtrlDown()
    {
        return _downKeys.Contains(0x11) || _downKeys.Contains(0xA2) || _downKeys.Contains(0xA3)
               || (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
    }

    private bool IsAltDown()
    {
        return _downKeys.Contains(0x12) || _downKeys.Contains(0xA4) || _downKeys.Contains(0xA5)
               || (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;
    }

    private bool IsShiftDown()
    {
        return _downKeys.Contains(0x10) || _downKeys.Contains(0xA0) || _downKeys.Contains(0xA1)
               || (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        try
        {
            int msg = wParam.ToInt32();
            if (msg == NativeMethods.WM_MOUSEWHEEL && HandleWheel)
            {
                var wheelInfo = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var wheelPt = new Point(wheelInfo.pt.X, wheelInfo.pt.Y);
                if (WheelFilter?.Invoke(wheelPt) ?? true)
                {
                    int delta = unchecked((short)(wheelInfo.mouseData >> 16));
                    _post(() => WheelScrolled?.Invoke(wheelPt, delta));
                    return (IntPtr)1;
                }
            }

            if (msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN)
            {
                bool isRight = msg == NativeMethods.WM_RBUTTONDOWN;
                if (!isRight && MouseDown == null) return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

                var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var pt = new Point(info.pt.X, info.pt.Y);
                _post(() => MouseDown?.Invoke(pt, isRight));

                if (isRight && HandleRightClick)
                    return (IntPtr)1;
            }
        }
        catch (Exception ex)
        {
            Log.Error("鼠标钩子异常", ex);
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UninstallAll();
        EscapePressed = null;
        SpaceDown = null;
        SpaceUp = null;
        RetryPressed = null;
        CopyPressed = null;
        MouseDown = null;
        WheelScrolled = null;
        WheelFilter = null;
    }
}
