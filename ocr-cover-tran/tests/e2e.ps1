param(
    [string]$AppDir = "",
    [string]$GreenExe = "",
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ui-common.ps1")

if ([string]::IsNullOrWhiteSpace($AppDir)) { $AppDir = Join-Path $PSScriptRoot "..\test-artifacts\e2e-app" }
if ([string]::IsNullOrWhiteSpace($GreenExe)) { $GreenExe = (Resolve-Path (Join-Path $PSScriptRoot "..\publish\green\ShotTrans.exe")).Path }
if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $PSScriptRoot "..\test-artifacts\e2e" }

$AppDir = (Resolve-Path $AppDir).Path
if (-not (Test-Path (Join-Path $AppDir "config.json"))) {
    throw "AppDir 缺少 config.json（请先配置翻译接口用于真实翻译测试）: $AppDir"
}
if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Assert-DesktopUnlocked

$exe = Join-Path $AppDir "ShotTrans.exe"
if (-not (Test-Path $exe)) { Copy-Item $GreenExe $exe -Force }

$report = New-Object System.Text.StringBuilder
function Log($s) { $report.AppendLine($s) | Out-Null; Write-Output $s }

$app = $null
$page = $null
try {
    # 测试截图需要看到覆盖层；生产默认 WDA_EXCLUDEFROMCAPTURE
    $env:SHOTRANS_DEBUG_CAPTURE_OVERLAY = "1"
    $env:SHOTRANS_DEBUG_INPUT = "1"

    Get-Process ShotTrans -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
    Remove-Item (Join-Path $AppDir "logs\shotrans.log") -ErrorAction SilentlyContinue

    Log "== ShotTrans UI E2E =="
    $app = Start-Process -FilePath $exe -WorkingDirectory $AppDir -PassThru
    Start-Sleep -Milliseconds 2700
    $page = Start-NativeTestPage $exe $AppDir
    Start-Sleep -Milliseconds 500

    Screenshot $OutDir "00-before.png" | Out-Null

    Log "== 1. 热键 + 框选 =="
    HotkeyScreenshot
    Start-Sleep -Milliseconds 500
    Screenshot $OutDir "01-selection.png" | Out-Null
    Drag 40 20 1760 620
    Start-Sleep -Milliseconds 1200
    Screenshot $OutDir "02-translating.png" | Out-Null
    Start-Sleep -Milliseconds 2800
    Screenshot $OutDir "03-result.png" | Out-Null
    Start-Sleep -Milliseconds 3600
    Screenshot $OutDir "04-faded.png" | Out-Null

    Log "== 2. Space 对比 =="
    Key 0x20 $false
    Start-Sleep -Milliseconds 450
    Screenshot $OutDir "05-original.png" | Out-Null
    Key 0x20 $true
    Start-Sleep -Milliseconds 250

    Log "== 3. Ctrl+C 复制 =="
    CtrlC
    Start-Sleep -Milliseconds 450
    Screenshot $OutDir "06-copied.png" | Out-Null
    $clip = [WinIo]::ReadClipboardText()
    [System.IO.File]::WriteAllText((Join-Path $OutDir "06-clipboard.txt"), $clip, (New-Object System.Text.UTF8Encoding($false)))
    $clipHasCjk = ($clip -match '[一-鿿]')
    Log ("clipboard has CJK: $clipHasCjk (len=$($clip.Length))")
    Log ("clipboard: " + (($clip -replace "`r?`n", " | ")).Substring(0, [Math]::Min(160, ($clip -replace "`r?`n", " | ").Length)))

    Log "== 4. 点击空白处关闭 =="
    Click 1500 800
    Start-Sleep -Milliseconds 500
    Screenshot $OutDir "07-closed.png" | Out-Null

    Log "== 5. 无文字区域 =="
    HotkeyScreenshot
    Start-Sleep -Milliseconds 450
    Drag 200 700 800 900
    Start-Sleep -Milliseconds 450
    Screenshot $OutDir "08-no-text.png" | Out-Null
    Start-Sleep -Milliseconds 1400

    Log "== 6. 划词翻译 + 剪贴板恢复 =="
    [WinIo]::WriteClipboardText("SHOTRANS-E2E-MARKER") | Out-Null
    Start-Sleep -Milliseconds 250
    Drag 60 590 1500 590
    Start-Sleep -Milliseconds 300
    HotkeySelection
    Start-Sleep -Milliseconds 2400
    Screenshot $OutDir "09-selection-popup.png" | Out-Null
    $afterClip = [WinIo]::ReadClipboardText()
    $restored = ($afterClip -eq "SHOTRANS-E2E-MARKER")
    Log ("clipboard restored: $restored (value='$afterClip')")
    Press 0x1B
    Start-Sleep -Milliseconds 500
    Screenshot $OutDir "10-popup-closed.png" | Out-Null

    $logPath = Get-LogPath $AppDir
    $logText = if (Test-Path $logPath) { Get-Content $logPath -Raw -Encoding UTF8 } else { "" }
    $errors = ([regex]::Matches($logText, "\[ERROR\]")).Count
    $translated = $logText.Contains("翻译完成")
    $overlayShown = $logText.Contains("覆盖层已显示")
    Log ("log ERROR=$errors translated=$translated overlayShown=$overlayShown")
    if (Test-Path $logPath) { Get-Content $logPath -Tail 20 -Encoding UTF8 | ForEach-Object { Log ("LOG " + $_) } }

    $requiredShots = @("01-selection.png", "03-result.png", "05-original.png", "07-closed.png", "08-no-text.png", "09-selection-popup.png")
    $shotsOk = ($requiredShots | Where-Object { -not (Test-Path (Join-Path $OutDir $_)) }).Count -eq 0
    $pass = $shotsOk -and $clipHasCjk -and $restored -and $translated -and $overlayShown -and ($errors -eq 0)
    Log ("screenshots present: $shotsOk")
    Log ("RESULT: " + ($(if ($pass) { "PASS" } else { "FAIL" })))

    [System.IO.File]::WriteAllText((Join-Path $OutDir "e2e-report.txt"), $report.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    Write-Output ("report: " + (Join-Path $OutDir "e2e-report.txt"))
    if (-not $pass) { exit 1 }
}
finally {
    if ($page) { try { Stop-Process -Id $page.Process.Id -Force } catch { } }
    if ($app) { try { Stop-Process -Id $app.Id -Force } catch { } }
    Remove-Item Env:SHOTRANS_DEBUG_CAPTURE_OVERLAY -ErrorAction SilentlyContinue
    Remove-Item Env:SHOTRANS_DEBUG_INPUT -ErrorAction SilentlyContinue
}
