param(
    [string]$GreenExe = "",
    [int]$Port = 8782
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ui-common.ps1")

if ([string]::IsNullOrWhiteSpace($GreenExe)) {
    $GreenExe = (Resolve-Path (Join-Path $PSScriptRoot "..\publish\green\ShotTrans.exe")).Path
}
Assert-DesktopUnlocked

$dir = New-TestDir "select-copy"
$report = New-Object System.Text.StringBuilder
function Log($s) { $report.AppendLine($s) | Out-Null; Write-Output $s }

$server = $null; $app = $null; $page = $null
try {
    Log "== 译文鼠标选择 + 右键复制测试 =="
    $env:SHOTRANS_DEBUG_CAPTURE_OVERLAY = "1"
    $env:SHOTRANS_DEBUG_INPUT = "1"
    $server = Start-FakeServer -Port $Port
    Write-TestConfig -Dir $dir -Url "http://127.0.0.1:$Port/v1" -Model "fake" -HistoryEnabled $false
    $app = Start-TestApp -Exe $GreenExe -Dir $dir
    $page = Start-NativeTestPage (Join-Path $dir "ShotTrans.exe") $dir
    Start-Sleep -Milliseconds 700

    # 1) 截图翻译：先 Ctrl+C 复制全部，作为对比基准
    HotkeyScreenshot
    Start-Sleep -Milliseconds 400
    Drag 40 20 1760 620
    Start-Sleep -Milliseconds 1600
    CtrlC
    Start-Sleep -Milliseconds 400
    $full = [WinIo]::ReadClipboardText()
    Log ("full clipboard length=" + $full.Length)

    # 从日志读取第一个译文块的屏幕区域
    $logPath = Get-LogPath $dir
    $logText = if (Test-Path $logPath) { Get-Content $logPath -Raw -Encoding UTF8 } else { "" }
    $m = [regex]::Matches($logText, "译文块屏幕区域：\{X=(\d+),Y=(\d+),Width=(\d+),Height=(\d+)\}")
    if ($m.Count -eq 0) { throw "未找到译文块坐标日志" }
    $g = $m[0].Groups
    $bx = [int]$g[1].Value; $by = [int]$g[2].Value; $bw = [int]$g[3].Value; $bh = [int]$g[4].Value
    Log ("block rect: $bx,$by $($bw)x$($bh)")

    # 2) 在第一个译文块内拖动选择
    $sx1 = $bx + 8
    $sx2 = $bx + [Math]::Max(40, [int]($bw * 0.5))
    $sy = $by + [Math]::Max(4, [int]($bh / 2))
    Drag $sx1 $sy $sx2 $sy
    Start-Sleep -Milliseconds 300
    Screenshot $dir "overlay-selection.png" | Out-Null
    CtrlC
    Start-Sleep -Milliseconds 400
    $selected = [WinIo]::ReadClipboardText()
    Log ("selected clipboard length=" + $selected.Length + " text=" + $selected)
    $selectionOk = ($selected.Length -gt 0) -and ($selected -match '[一-鿿]') -and ($selected -ne $full)

    # 3) 右键菜单复制选中
    [WinIo]::SetCursorPos($sx1, $sy) | Out-Null
    Start-Sleep -Milliseconds 100
    [WinIo]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [WinIo]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 400
    Press 0x28
    Start-Sleep -Milliseconds 100
    Press 0x0D
    Start-Sleep -Milliseconds 500
    $menuSelected = [WinIo]::ReadClipboardText()
    Log ("context-menu copy length=" + $menuSelected.Length + " text=" + $menuSelected)
    $menuOk = ($menuSelected.Length -gt 0) -and ($menuSelected -eq $selected)
    Press 0x1B
    Start-Sleep -Milliseconds 500

    # 4) 划词弹窗：选择译文并右键复制
    Drag 60 585 1200 585
    Start-Sleep -Milliseconds 300
    HotkeySelection
    Start-Sleep -Milliseconds 2400
    $popup = [WinIo]::FindWindowByTitle("ShotTrans.SelectionPopup")
    if ($popup -eq [IntPtr]::Zero) { throw "未找到划词弹窗" }
    $r = New-Object WinIo+RECT
    [WinIo]::GetWindowRect($popup, [ref]$r) | Out-Null
    Log ("popup rect: $($r.Left),$($r.Top) $($r.Right-$r.Left)x$($r.Bottom-$r.Top)")
    Screenshot $dir "popup.png" | Out-Null

    $px1 = $r.Left + 30
    $px2 = [Math]::Min($r.Right - 20, $r.Left + 280)
    $py = $r.Top + 70
    Drag $px1 $py $px2 $py
    Start-Sleep -Milliseconds 300
    CtrlC
    Start-Sleep -Milliseconds 400
    $popSelected = [WinIo]::ReadClipboardText()
    Log ("popup selected length=" + $popSelected.Length + " text=" + $popSelected)
    $popupSelOk = ($popSelected.Length -gt 0) -and ($popSelected -match '[一-鿿]')

    # 右键菜单复制
    [WinIo]::SetCursorPos([int](($px1 + $px2) / 2), $py) | Out-Null
    Start-Sleep -Milliseconds 100
    [WinIo]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [WinIo]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 400
    Press 0x28
    Start-Sleep -Milliseconds 100
    Press 0x0D
    Start-Sleep -Milliseconds 500
    $popMenu = [WinIo]::ReadClipboardText()
    Log ("popup menu copy length=" + $popMenu.Length + " text=" + $popMenu)
    $popupMenuOk = ($popMenu.Length -gt 0) -and ($popMenu -eq $popSelected)
    Press 0x1B
    Start-Sleep -Milliseconds 300

    $logText = if (Test-Path $logPath) { Get-Content $logPath -Raw -Encoding UTF8 } else { "" }
    $errors = ([regex]::Matches($logText, "\[ERROR\]")).Count
    Log ("log errors=$errors")
    $pass = $selectionOk -and $menuOk -and $popupSelOk -and $popupMenuOk -and ($errors -eq 0)
    Log ("RESULT: " + ($(if ($pass) { "PASS" } else { "FAIL" })))
    [System.IO.File]::WriteAllText((Join-Path $dir "select-copy-report.txt"), $report.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    Write-Output ("report: " + (Join-Path $dir "select-copy-report.txt"))
    if (-not $pass) { exit 1 }
}
finally {
    if ($page) { try { Stop-Process -Id $page.Process.Id -Force } catch { } }
    if ($app) { Stop-TestApp $app }
    if ($server) { Stop-FakeServer $server }
    Remove-Item Env:SHOTRANS_DEBUG_CAPTURE_OVERLAY -ErrorAction SilentlyContinue
    Remove-Item Env:SHOTRANS_DEBUG_INPUT -ErrorAction SilentlyContinue
}
