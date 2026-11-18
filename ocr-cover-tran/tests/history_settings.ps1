param(
    [string]$GreenExe = "",
    [int]$Port = 8767
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ui-common.ps1")

if ([string]::IsNullOrWhiteSpace($GreenExe)) {
    $GreenExe = (Resolve-Path (Join-Path $PSScriptRoot "..\publish\green\ShotTrans.exe")).Path
}
if (-not (Test-Path $GreenExe)) { throw "找不到绿色版：$GreenExe" }
Assert-DesktopUnlocked

$dir = New-TestDir "history-settings"
$report = New-Object System.Text.StringBuilder
function Log($s) { $report.AppendLine($s) | Out-Null; Write-Output $s }

$server = $null
$app = $null
$page = $null
try {
    Log "== 历史记录 + 设置界面测试 =="
    $env:SHOTRANS_DEBUG_CAPTURE_OVERLAY = "1"
    $server = Start-FakeServer -Port $Port
    Write-TestConfig -Dir $dir -Url "http://127.0.0.1:$Port/v1" -Model "fake" -HistoryEnabled $true

    $app = Start-TestApp -Exe $GreenExe -Dir $dir
    $page = Start-NativeTestPage (Join-Path $dir "ShotTrans.exe") $dir
    Start-Sleep -Milliseconds 800

    HotkeyScreenshot
    Start-Sleep -Milliseconds 300
    Drag 40 20 1760 620
    Start-Sleep -Milliseconds 1600
    Press 0x1B
    Start-Sleep -Milliseconds 1200

    $db = Join-Path $dir "history.db"
    $query = & python -c "import sqlite3,sys,json; p=sys.argv[1]; c=sqlite3.connect(p); r=c.execute('select count(*), max(original), max(translated) from history').fetchone(); print(json.dumps({'count':r[0],'original':r[1],'translated':r[2]}, ensure_ascii=False))" $db 2>&1
    Log ("history db: " + $db)
    Log ("history query: " + $query)
    $historyCount = 0
    try { $historyCount = ($query | ConvertFrom-Json).count } catch { }

    Stop-TestApp $app
    $app = $null
    if ($page) { try { Stop-Process -Id $page.Process.Id -Force } catch { }; $page = $null }
    Start-Sleep -Milliseconds 500

    $app = Start-TestApp -Exe $GreenExe -Dir $dir -Arguments @("--history")
    Start-Sleep -Milliseconds 1500
    $historyShot = Screenshot $dir "history-window.png"
    Log ("history window screenshot: " + $historyShot)
    AltF4
    Start-Sleep -Milliseconds 600
    Stop-TestApp $app
    $app = $null
    Start-Sleep -Milliseconds 400

    $app = Start-TestApp -Exe $GreenExe -Dir $dir -Arguments @("--settings")
    Start-Sleep -Milliseconds 1500
    $settingsShot = Screenshot $dir "settings-window.png"
    Log ("settings screenshot: " + $settingsShot)
    Press 0x1B
    Start-Sleep -Milliseconds 600

    $logPath = Get-LogPath $dir
    $logText = if (Test-Path $logPath) { Get-Content $logPath -Raw -Encoding UTF8 } else { "" }
    $errors = ([regex]::Matches($logText, "\[ERROR\]")).Count
    Log ("history rows: $historyCount")
    Log ("log ERROR count: $errors")

    $pass = ($historyCount -ge 1) -and (Test-Path $historyShot) -and (Test-Path $settingsShot) -and ($errors -eq 0)
    Log ("RESULT: " + ($(if ($pass) { "PASS" } else { "FAIL" })))
    $reportPath = Join-Path $dir "history-settings-report.txt"
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
