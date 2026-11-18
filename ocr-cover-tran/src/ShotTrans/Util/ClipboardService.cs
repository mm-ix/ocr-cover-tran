using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ShotTrans.Util;

/// <summary>
/// 直接使用 Win32 剪贴板 API，带超时重试，避免 WinForms/OLE 剪贴板在所有者异常时阻塞 UI。
/// 只处理 CF_UNICODETEXT（本工具复制/取词所需）。
/// </summary>
internal static class ClipboardService
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public static bool TrySetText(string text, int timeoutMs = 800)
    {
        text ??= "";
        long deadline = Environment.TickCount64 + Math.Max(50, timeoutMs);
        while (Environment.TickCount64 < deadline)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(20);
                continue;
            }

            bool ok = false;
            IntPtr hGlobal = IntPtr.Zero;
            try
            {
                if (!EmptyClipboard()) continue;

                byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
                hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
                if (hGlobal == IntPtr.Zero) continue;

                IntPtr target = GlobalLock(hGlobal);
                if (target == IntPtr.Zero) continue;
                try { Marshal.Copy(bytes, 0, target, bytes.Length); }
                finally { GlobalUnlock(hGlobal); }

                if (SetClipboardData(CF_UNICODETEXT, hGlobal) != IntPtr.Zero)
                {
                    ok = true;
                    hGlobal = IntPtr.Zero; // 所有权交给系统
                }
            }
            catch (Exception ex)
            {
                Log.Warn("写剪贴板失败：" + ex.Message);
            }
            finally
            {
                if (hGlobal != IntPtr.Zero) GlobalFree(hGlobal);
                CloseClipboard();
            }

            if (ok) return true;
            Thread.Sleep(20);
        }
        Log.Warn("写剪贴板超时（剪贴板可能被其他程序占用）");
        return false;
    }

    public static string? TryGetText(int timeoutMs = 800)
    {
        long deadline = Environment.TickCount64 + Math.Max(50, timeoutMs);
        while (Environment.TickCount64 < deadline)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(20);
                continue;
            }

            try
            {
                IntPtr handle = GetClipboardData(CF_UNICODETEXT);
                if (handle == IntPtr.Zero) return null;
                IntPtr ptr = GlobalLock(handle);
                if (ptr == IntPtr.Zero) return null;
                try
                {
                    return Marshal.PtrToStringUni(ptr);
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读剪贴板失败：" + ex.Message);
                return null;
            }
            finally
            {
                CloseClipboard();
            }
        }
        Log.Warn("读剪贴板超时（剪贴板可能被其他程序占用）");
        return null;
    }

    public static bool HasText()
    {
        try { return IsClipboardFormatAvailable(CF_UNICODETEXT); }
        catch { return false; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);
}
