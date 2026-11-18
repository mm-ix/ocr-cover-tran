param(
    [string]$GreenExe = "",
    [int]$Port = 8775
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ui-common.ps1")

if ([string]::IsNullOrWhiteSpace($GreenExe)) {
    $GreenExe = (Resolve-Path (Join-Path $PSScriptRoot "..\publish\green\ShotTrans.exe")).Path
}
Assert-DesktopUnlocked

$dir = New-TestDir "selection"
$report = New-Object System.Text.StringBuilder
function Log($s) { $report.AppendLine($s) | Out-Null; Write-Output $s }

$server = $null
$app = $null
$page = $null
try {
    Log "== 框选尺寸显示 / Esc 取消 / 右键取消 / 覆盖层右键关闭 =="
    $env:SHOTRANS_DEBUG_CAPTURE_OVERLAY = "1"
    $server = Start-FakeServer -Port $Port
    Write-TestConfig -Dir $dir -Url "http://127.0.0.1:$Port/v1" -Model "fake" -HistoryEnabled $false
    $app = Start-TestApp -Exe $GreenExe -Dir $dir
    $page = Start-NativeTestPage (Join-Path $dir "ShotTrans.exe") $dir
    Start-Sleep -Milliseconds 800

    # 1) 拖拽中截图：显示尺寸标签
    HotkeyScreenshot
    Start-Sleep -Milliseconds 400
    [WinIo]::SetCursorPos(120, 120) | Out-Null
    Start-Sleep -Milliseconds 120
    [WinIo]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    for ($i = 1; $i -le 20; $i++) {
        [WinIo]::SetCursorPos(120 + 780 * $i / 20, 120 + 440 * $i / 20) | Out-Null
        Start-Sleep -Milliseconds 15
    }
    Start-Sleep -Milliseconds 150
    Screenshot $dir "selection-dragging.png" | Out-Null
    Press 0x1B
    Start-Sleep -Milliseconds 250
    [WinIo]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 300
    Screenshot $dir "selection-esc-cancelled.png" | Out-Null

    # 2) 右键取消框选
    HotkeyScreenshot
    Start-Sleep -Milliseconds 400
    [WinIo]::SetCursorPos(120, 120) | Out-Null
    Start-Sleep -Milliseconds 100
    [WinIo]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [WinIo]::SetCursorPos(900, 560) | Out-Null
    Start-Sleep -Milliseconds 150
    [WinIo]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [WinIo]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 250
    [WinIo]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 300
    Screenshot $dir "selection-right-cancelled.png" | Out-Null

    # 3) 结果覆盖层右键关闭
    HotkeyScreenshot
    Start-Sleep -Milliseconds 400
    Drag 40 20 1760 620
    Start-Sleep -Milliseconds 1500
    [WinIo]::SetCursorPos(1000, 400) | Out-Null
    [WinIo]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [WinIo]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500
    Screenshot $dir "result-rightclick-closed.png" | Out-Null

    $logText = if (Test-Path (Get-LogPath $dir)) { Get-Content (Get-LogPath $dir) -Raw -Encoding UTF8 } else { "" }
    $errors = ([regex]::Matches($logText, "\[ERROR\]")).Count
    $closed = $logText.Contains("覆盖层关闭")
    Log ("log errors=$errors overlayClosed=$closed")
    $pass = (Test-Path (Join-Path $dir "selection-dragging.png")) -and (Test-Path (Join-Path $dir "selection-esc-cancelled.png")) -and (Test-Path (Join-Path $dir "selection-right-cancelled.png")) -and (Test-Path (Join-Path $dir "result-rightclick-closed.png")) -and $closed -and ($errors -eq 0)
    Log ("RESULT: " + ($(if ($pass) { "PASS" } else { "FAIL" })))
    [System.IO.File]::WriteAllText((Join-Path $dir "selection-report.txt"), $report.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    Write-Output ("report: " + (Join-Path $dir "selection-report.txt"))
    if (-not $pass) { exit 1 }
}
finally {
    if ($page) { try { Stop-Process -Id $page.Process.Id -Force } catch { } }
    if ($app) { Stop-TestApp $app }
    if ($server) { Stop-FakeServer $server }
    Remove-Item Env:SHOTRANS_DEBUG_CAPTURE_OVERLAY -ErrorAction SilentlyContinue
}
