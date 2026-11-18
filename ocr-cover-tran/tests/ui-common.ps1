$ErrorActionPreference = "Stop"

# 必须在加载 WinForms / 使用 GDI 之前设置 DPI 感知，否则 SetCursorPos / 截图会被系统按 150% 缩放。
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DpiInit {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public static void Run() {
        try { if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) SetProcessDPIAware(); }
        catch { try { SetProcessDPIAware(); } catch { } }
    }
}
"@
[DpiInit]::Run()

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

public static class WinIo {
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hDest, int x, int y, int w, int h, IntPtr hSrc, int sx, int sy, uint rop);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool OpenClipboard(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError=true)] public static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr GetClipboardData(uint fmt);
    [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SetClipboardData(uint fmt, IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr GlobalFree(IntPtr h);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GlobalUnlock(IntPtr h);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public static string ReadClipboardText(int timeoutMs = 800) {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline) {
            if (!OpenClipboard(IntPtr.Zero)) { System.Threading.Thread.Sleep(20); continue; }
            try {
                IntPtr h = GetClipboardData(13);
                if (h == IntPtr.Zero) return "";
                IntPtr p = GlobalLock(h);
                if (p == IntPtr.Zero) return "";
                try { return Marshal.PtrToStringUni(p) ?? ""; }
                finally { GlobalUnlock(h); }
            } finally { CloseClipboard(); }
        }
        return "";
    }

    public static bool WriteClipboardText(string text, int timeoutMs = 800) {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline) {
            if (!OpenClipboard(IntPtr.Zero)) { System.Threading.Thread.Sleep(20); continue; }
            IntPtr mem = IntPtr.Zero;
            bool ok = false;
            try {
                if (!EmptyClipboard()) continue;
                var bytes = Encoding.Unicode.GetBytes(text + "\0");
                mem = GlobalAlloc(0x0002, (UIntPtr)bytes.Length);
                if (mem == IntPtr.Zero) continue;
                IntPtr p = GlobalLock(mem);
                if (p == IntPtr.Zero) continue;
                try { Marshal.Copy(bytes, 0, p, bytes.Length); } finally { GlobalUnlock(mem); }
                if (SetClipboardData(13, mem) != IntPtr.Zero) { ok = true; mem = IntPtr.Zero; }
            } finally {
                if (mem != IntPtr.Zero) GlobalFree(mem);
                CloseClipboard();
            }
            if (ok) return true;
            System.Threading.Thread.Sleep(20);
        }
        return false;
    }

    public static IntPtr FindWindowByTitle(string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, 512);
            if (sb.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static bool ActivateWindow(IntPtr h) {
        if (h == IntPtr.Zero) return false;
        IntPtr fg = GetForegroundWindow();
        uint pid; uint fgThread = GetWindowThreadProcessId(fg, out pid);
        uint myThread = GetCurrentThreadId();
        AttachThreadInput(myThread, fgThread, true);
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        ShowWindow(h, 9);
        SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
        bool ok = SetForegroundWindow(h);
        SetWindowPos(h, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002);
        AttachThreadInput(myThread, fgThread, false);
        return ok;
    }

    public static bool Grab(int x, int y, int w, int h, string path) {
        bool ok = false;
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb)) {
            using (var g = Graphics.FromImage(bmp)) {
                IntPtr dd = g.GetHdc();
                IntPtr sd = GetDC(IntPtr.Zero);
                ok = BitBlt(dd, 0, 0, w, h, sd, x, y, 0x00CC0020 | 0x40000000);
                g.ReleaseHdc(dd);
                ReleaseDC(IntPtr.Zero, sd);
            }
            bmp.Save(path, ImageFormat.Png);
        }
        return ok;
    }

    public static int CountVisibleWindows(uint pid) {
        int count = 0;
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h)) count++;
            return true;
        }, IntPtr.Zero);
        return count;
    }
}
"@

$KEYUP = 0x0002
$MOUSEDOWN = 0x0002
$MOUSEUP = 0x0004

function Assert-DesktopUnlocked {
    if ([WinIo]::GetForegroundWindow() -eq [IntPtr]::Zero) {
        throw "当前桌面已锁定（GetForegroundWindow=0），请先解锁 RDP 会话。"
    }
}

function Screenshot([string]$OutDir, [string]$Name) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $path = Join-Path $OutDir $Name
    for ($i = 0; $i -lt 4; $i++) {
        $ok = [WinIo]::Grab($b.Left, $b.Top, $b.Width, $b.Height, $path)
        if ($ok -and (Test-Path $path) -and ((Get-Item $path).Length -gt 1000)) { return $path }
        Start-Sleep -Milliseconds 250
    }
    throw "截图失败（会话可能已锁定）"
}

function Key([byte]$vk, [bool]$up = $false) {
    [WinIo]::keybd_event($vk, 0, ($(if ($up) { $KEYUP } else { 0 })), [UIntPtr]::Zero)
}
function Press([byte]$vk) { Key $vk $false; Start-Sleep -Milliseconds 30; Key $vk $true }
function HotkeyScreenshot {
    Key 0x11; Key 0x12; Key 0x58; Start-Sleep -Milliseconds 40
    Key 0x58 $true; Key 0x12 $true; Key 0x11 $true
}
function HotkeySelection {
    Key 0x11; Key 0x12; Key 0x5A; Start-Sleep -Milliseconds 40
    Key 0x5A $true; Key 0x12 $true; Key 0x11 $true
}
function Drag([int]$x1, [int]$y1, [int]$x2, [int]$y2) {
    [WinIo]::SetCursorPos($x1, $y1) | Out-Null
    Start-Sleep -Milliseconds 120
    [WinIo]::mouse_event($MOUSEDOWN, 0, 0, 0, [UIntPtr]::Zero)
    $steps = 24
    for ($i = 1; $i -le $steps; $i++) {
        $x = [int]($x1 + ($x2 - $x1) * $i / $steps)
        $y = [int]($y1 + ($y2 - $y1) * $i / $steps)
        [WinIo]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Milliseconds 18
    }
    [WinIo]::mouse_event($MOUSEUP, 0, 0, 0, [UIntPtr]::Zero)
}
function Click([int]$x, [int]$y) {
    [WinIo]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 80
    [WinIo]::mouse_event($MOUSEDOWN, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 30
    [WinIo]::mouse_event($MOUSEUP, 0, 0, 0, [UIntPtr]::Zero)
}
function CtrlC {
    Key 0x11
    Start-Sleep -Milliseconds 60
    Key 0x43
    Start-Sleep -Milliseconds 40
    Key 0x43 $true
    Start-Sleep -Milliseconds 30
    Key 0x11 $true
}
function AltF4 {
    Key 0x12; Key 0x73; Start-Sleep -Milliseconds 30
    Key 0x73 $true; Key 0x12 $true
}

function Wait-Server([int]$Port, [int]$TimeoutMs = 8000) {
    $deadline = [DateTime]::Now.AddMilliseconds($TimeoutMs)
    while ([DateTime]::Now -lt $deadline) {
        try {
            $r = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/stats" -TimeoutSec 1
            if ($null -ne $r) { return }
        } catch { }
        Start-Sleep -Milliseconds 150
    }
    throw "本地假翻译服务未就绪 (port $Port)"
}

function Start-FakeServer([int]$Port, [switch]$FailFirst) {
    $serverArgs = @((Join-Path $PSScriptRoot "fake_server.py"), "$Port")
    if ($FailFirst) { $serverArgs += "--fail-first" }
    $p = Start-Process -FilePath "python" -ArgumentList $serverArgs -PassThru -WindowStyle Hidden
    Wait-Server -Port $Port
    return $p
}

function Stop-FakeServer($Process) {
    try { if ($Process -and -not $Process.HasExited) { Stop-Process -Id $Process.Id -Force } } catch { }
}

function New-TestDir([string]$Name) {
    $root = Join-Path $PSScriptRoot "..\test-artifacts"
    $dir = Join-Path $root $Name
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    return $dir
}

function Write-TestConfig([string]$Dir, [string]$Url, [string]$Model = "fake", [bool]$HistoryEnabled = $false) {
    $cfg = [ordered]@{
        ScreenshotHotkey = "Ctrl+Alt+X"
        SelectionHotkey = "Ctrl+Alt+Z"
        OcrLanguage = "auto"
        FontMinSize = 8
        FontMaxSize = 24
        BackgroundOpacity = 0.92
        AlignCenter = $false
        StatusBarFadeSeconds = 3
        FontFamily = "Microsoft YaHei UI"
        TranslationBaseUrl = $Url
        TranslationApiKey = ""
        TranslationModel = $Model
        TranslationTemperature = 0.2
        TranslationTimeoutSeconds = 20
        ProtectCodeIdentifiers = $true
        Glossary = ""
        HistoryEnabled = $HistoryEnabled
        AutoStart = $false
        EnableDebugLog = $true  # 测试脚本需要读取日志
        FirstRunDone = $true
    }
    $json = $cfg | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText((Join-Path $Dir "config.json"), $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Start-TestApp([string]$Exe, [string]$Dir, [string[]]$Arguments = @()) {
    $target = Join-Path $Dir "ShotTrans.exe"
    Copy-Item $Exe $target -Force
    $params = @{ FilePath = $target; WorkingDirectory = $Dir; PassThru = $true }
    if ($Arguments -and $Arguments.Count -gt 0) { $params.ArgumentList = $Arguments }
    $p = Start-Process @params
    Start-Sleep -Milliseconds 1200
    return $p
}

function Stop-TestApp($Process) {
    try { if ($Process -and -not $Process.HasExited) { Stop-Process -Id $Process.Id -Force } } catch { }
    Start-Sleep -Milliseconds 400
}

function Focus-TestPage {
    $page = (Resolve-Path (Join-Path $PSScriptRoot "..\test-artifacts\test-page.html")).Path
    $uri = "file:///" + ($page -replace '\\', '/')
    $chrome = $null
    try { $chrome = (Get-Process chrome -ErrorAction SilentlyContinue | Select-Object -First 1).Path } catch { }
    if (-not $chrome) {
        foreach ($p in @("C:\Program Files\Google\Chrome\Application\chrome.exe", "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe")) {
            if (Test-Path $p) { $chrome = $p; break }
        }
    }
    if ($chrome) {
        Start-Process -FilePath $chrome -ArgumentList "--new-window", $uri | Out-Null
    } else {
        Start-Process $uri | Out-Null
    }
    Start-Sleep -Milliseconds 2500
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shell.AppActivate("ShotTrans E2E Test Page") | Out-Null
    } catch { }
    Start-Sleep -Milliseconds 500
}

function Get-LogPath([string]$Dir) { Join-Path $Dir "logs\shotrans.log" }

function Start-NativeTestPage([string]$Exe, [string]$Dir) {
    $p = Start-Process -FilePath $Exe -WorkingDirectory $Dir -ArgumentList @("--test-page") -PassThru
    $deadline = [DateTime]::Now.AddSeconds(8)
    $h = [IntPtr]::Zero
    while ([DateTime]::Now -lt $deadline) {
        $h = [WinIo]::FindWindowByTitle("ShotTrans E2E Test Page")
        if ($h -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 120
    }
    if ($h -eq [IntPtr]::Zero) {
        try { Stop-Process -Id $p.Id -Force } catch { }
        throw "测试窗口未出现"
    }
    [WinIo]::ActivateWindow($h) | Out-Null
    Start-Sleep -Milliseconds 600
    return [pscustomobject]@{ Process = $p; Handle = $h }
}

