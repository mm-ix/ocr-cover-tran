param(
    [string]$GreenExe = "",
    [int]$Port = 8772
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ui-common.ps1")

if ([string]::IsNullOrWhiteSpace($GreenExe)) {
    $GreenExe = (Resolve-Path (Join-Path $PSScriptRoot "..\publish\green\ShotTrans.exe")).Path
}
if (-not (Test-Path $GreenExe)) { throw "找不到绿色版：$GreenExe" }
Assert-DesktopUnlocked

$dir = New-TestDir "multimonitor"
$report = New-Object System.Text.StringBuilder
function Log($s) { $report.AppendLine($s) | Out-Null; Write-Output $s }

$server = $null
$app = $null
$page = $null
try {
    Log "== 多显示器 / 150% DPI 测试 =="
    $screens = [System.Windows.Forms.Screen]::AllScreens
    foreach ($s in $screens) { Log ("screen " + $s.DeviceName + " " + $s.Bounds + " primary=" + $s.Primary) }
    $secondary = $screens | Where-Object { -not $_.Primary } | Select-Object -First 1
    if (-not $secondary) { throw "未检测到副显示器" }
    $sb = $secondary.Bounds
    Log ("secondary bounds: " + $sb)

    $env:SHOTRANS_DEBUG_CAPTURE_OVERLAY = "1"
    $server = Start-FakeServer -Port $Port
    Write-TestConfig -Dir $dir -Url "http://127.0.0.1:$Port/v1" -Model "fake" -HistoryEnabled $false
    $app = Start-TestApp -Exe $GreenExe -Dir $dir
    $page = Start-NativeTestPage (Join-Path $dir "ShotTrans.exe") $dir
    Start-Sleep -Milliseconds 500

    # 把测试窗口移到副屏（副屏是竖屏，测试窗口左侧文字区域可见）
    [WinIo]::SetWindowPos($page.Handle, [IntPtr]::Zero, $sb.X + 40, 120, 1800, 1000, 0x0040) | Out-Null
    [WinIo]::ActivateWindow($page.Handle) | Out-Null
    Start-Sleep -Milliseconds 700

    # 1) 副屏内框选
    Log "== 副屏内框选 =="
    $x1 = $sb.X + 120; $y1 = 140; $x2 = [Math]::Min($sb.Right - 40, $x1 + 1280); $y2 = 620
    HotkeyScreenshot
    Start-Sleep -Milliseconds 400
    Drag $x1 $y1 $x2 $y2
    Start-Sleep -Milliseconds 1600
    CtrlC
    Start-Sleep -Milliseconds 400
    $clipSecondary = [WinIo]::ReadClipboardText()
    $okSecondary = $clipSecondary -match '[一-鿿]'
    Log ("副屏剪贴板含中文: $okSecondary (len=$($clipSecondary.Length))")
    Screenshot $dir "secondary-overlay-primary-crop.png" | Out-Null
    [WinIo]::Grab($sb.X, $sb.Y, $sb.Width, $sb.Height, (Join-Path $dir "secondary-overlay.png")) | Out-Null
    Press 0x1B
    Start-Sleep -Milliseconds 400

    # 2) 跨屏框选：主屏右侧 -> 副屏左侧
    Log "== 跨屏框选 =="
    $csx1 = 2400; $csy1 = 300
    $csx2 = $sb.X + 520; $csy2 = 700
    HotkeyScreenshot
    Start-Sleep -Milliseconds 400
    Drag $csx1 $csy1 $csx2 $csy2
    Start-Sleep -Milliseconds 1600
    CtrlC
    Start-Sleep -Milliseconds 400
    $clipCross = [WinIo]::ReadClipboardText()
    $okCross = $clipCross -match '[一-鿿]'
    Log ("跨屏剪贴板含中文: $okCross (len=$($clipCross.Length))")
    [WinIo]::Grab($sb.X, $sb.Y, $sb.Width, $sb.Height, (Join-Path $dir "cross-overlay.png")) | Out-Null
    Press 0x1B
    Start-Sleep -Milliseconds 300

    $logPath = Get-LogPath $dir
    $logText = if (Test-Path $logPath) { Get-Content $logPath -Raw -Encoding UTF8 } else { "" }
    $errors = ([regex]::Matches($logText, "\[ERROR\]")).Count
    $ocrLines = [regex]::Matches($logText, "OCR 完成：([^\r\n]+)") | ForEach-Object { $_.Groups[1].Value }
    Log ("log ERROR count: $errors")
    foreach ($l in $ocrLines) { Log ("OCR " + $l) }

    $pass = $okSecondary -and $okCross -and ($errors -eq 0)
    Log ("RESULT: " + ($(if ($pass) { "PASS" } else { "FAIL" })))
    $reportPath = Join-Path $dir "multimonitor-report.txt"
    [System.IO.File]::WriteAllText($reportPath, $report.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    Write-Output ("report: " + $reportPath)
    if (-not $pass) { exit 1 }
}
finally {
    if ($page) { try { Stop-Process -Id $page.Process.Id -Force } catch { } }
    if ($app) { Stop-TestApp $app }
    if ($server) { Stop-FakeServer $server }
    Remove-Item Env:SHOTRANS_DEBUG_CAPTURE_OVERLAY -ErrorAction SilentlyContinue
}
