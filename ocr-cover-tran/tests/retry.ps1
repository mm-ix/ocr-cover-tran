param(
    [string]$GreenExe = "",
    [int]$Port = 8766
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ui-common.ps1")

if ([string]::IsNullOrWhiteSpace($GreenExe)) {
    $GreenExe = (Resolve-Path (Join-Path $PSScriptRoot "..\publish\green\ShotTrans.exe")).Path
}
if (-not (Test-Path $GreenExe)) { throw "找不到绿色版：$GreenExe" }
Assert-DesktopUnlocked

$dir = New-TestDir "retry"
$report = New-Object System.Text.StringBuilder
function Log($s) { $report.AppendLine($s) | Out-Null; Write-Output $s }

$server = $null
$app = $null
$page = $null
try {
    Log "== 失败重试测试（首次请求返回 500） =="
    $env:SHOTRANS_DEBUG_CAPTURE_OVERLAY = "1"
    $server = Start-FakeServer -Port $Port -FailFirst
    Write-TestConfig -Dir $dir -Url "http://127.0.0.1:$Port/v1" -Model "fake" -HistoryEnabled $false
    $app = Start-TestApp -Exe $GreenExe -Dir $dir
    $page = Start-NativeTestPage (Join-Path $dir "ShotTrans.exe") $dir
    Start-Sleep -Milliseconds 800

    HotkeyScreenshot
    Start-Sleep -Milliseconds 300
    Drag 40 20 1760 620
    Start-Sleep -Milliseconds 2200
    $errorShot = Screenshot $dir "retry-01-error.png"
    Log ("error screenshot: " + $errorShot)

    Log "press R to retry"
    Press 0x52
    Start-Sleep -Milliseconds 2500
    $okShot = Screenshot $dir "retry-02-success.png"
    Log ("success screenshot: " + $okShot)

    $logPath = Get-LogPath $dir
    $logText = if (Test-Path $logPath) { Get-Content $logPath -Raw -Encoding UTF8 } else { "" }
    $hasFailure = $logText.Contains("[ERROR]") -and $logText.Contains("翻译失败")
    $retryOk = $logText.Contains("重试翻译成功")
    $stats = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/stats" -TimeoutSec 2
    Log ("server requests: " + $stats.requests)
    Log ("log has translate failure: $hasFailure")
    Log ("log has retry success: $retryOk")

    Press 0x1B
    Start-Sleep -Milliseconds 300

    $pass = $hasFailure -and $retryOk -and ($stats.requests -ge 2)
    Log ("RESULT: " + ($(if ($pass) { "PASS" } else { "FAIL" })))

    $reportPath = Join-Path $dir "retry-report.txt"
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
