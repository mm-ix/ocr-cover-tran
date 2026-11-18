using System.Runtime.InteropServices;
using ShotTrans.Interop;

namespace ShotTrans.Util;

internal static class ClipboardHelper
{
    /// <summary>
    /// 模拟 Ctrl+C 读取当前选区。开始前备份剪贴板文本，结束后恢复。
    /// 使用 Win32 剪贴板 API + 超时，避免剪贴板被其他程序占用时卡死界面。
    /// </summary>
    public static async Task<string?> CaptureSelectedTextAsync()
    {
        string? previousText = ClipboardService.TryGetText();
        bool hadText = previousText != null;
        try
        {
            uint seq = NativeMethods.GetClipboardSequenceNumber();

            // 快捷键按住时 Ctrl/Alt 仍处于按下状态，先释放再发送 Ctrl+C。
            ReleaseModifiers();
            await Task.Delay(90).ConfigureAwait(true);

            SendCtrlC();

            long deadline = Environment.TickCount64 + 1000;
            while (Environment.TickCount64 < deadline)
            {
                await Task.Delay(25).ConfigureAwait(true);
                uint now = NativeMethods.GetClipboardSequenceNumber();
                if (now == seq) continue;
                seq = now;
                var text = ClipboardService.TryGetText();
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }

            return ClipboardService.TryGetText()?.Trim();
        }
        finally
        {
            if (hadText)
                ClipboardService.TrySetText(previousText ?? "");
        }
    }

    private static void ReleaseModifiers()
    {
        var inputs = new List<NativeMethods.INPUT>();
        void Up(int vk) => inputs.Add(KeyInput(vk, up: true));
        Up(NativeMethods.VK_MENU);
        Up(NativeMethods.VK_CONTROL);
        Up(NativeMethods.VK_SHIFT);
        Up(NativeMethods.VK_LWIN);
        Up(NativeMethods.VK_RWIN);
        Send(inputs);
    }

    private static void SendCtrlC()
    {
        var inputs = new[]
        {
            KeyInput(NativeMethods.VK_CONTROL, up: false),
            KeyInput(NativeMethods.VK_C, up: false),
            KeyInput(NativeMethods.VK_C, up: true),
            KeyInput(NativeMethods.VK_CONTROL, up: true)
        };
        Send(inputs);
    }

    private static NativeMethods.INPUT KeyInput(int vk, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.INPUTUNION
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = (ushort)vk,
                wScan = 0,
                dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = UIntPtr.Zero
            }
        }
    };

    private static void Send(IReadOnlyList<NativeMethods.INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        var arr = inputs as NativeMethods.INPUT[] ?? inputs.ToArray();
        NativeMethods.SendInput((uint)arr.Length, arr, Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
