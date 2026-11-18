param(
    [string]$GreenExe = "",
    [int]$Port = 8773
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ui-common.ps1")

if ([string]::IsNullOrWhiteSpace($GreenExe)) {
    $GreenExe = (Resolve-Path (Join-Path $PSScriptRoot "..\publish\green\ShotTrans.exe")).Path
}
Assert-DesktopUnlocked
Remove-Item Env:SHOTRANS_DEBUG_CAPTURE_OVERLAY -ErrorAction SilentlyContinue

$dir = New-TestDir "exclude"
$report = New-Object System.Text.StringBuilder
function Log($s) { $report.AppendLine($s) | Out-Null; Write-Output $s }

$server = $null
$app = $null
$page = $null
try {
    Log "== F-14 排除自身截图测试（不设置测试环境变量） =="
    $server = Start-FakeServer -Port $Port
    Write-TestConfig -Dir $dir -Url "http://127.0.0.1:$Port/v1" -Model "fake" -HistoryEnabled $false
    $app = Start-TestApp -Exe $GreenExe -Dir $dir
    $page = Start-NativeTestPage (Join-Path $dir "ShotTrans.exe") $dir
    Start-Sleep -Milliseconds 800

    Screenshot $dir "exclude-00-before.png" | Out-Null
    HotkeyScreenshot
    Start-Sleep -Milliseconds 500
    Screenshot $dir "exclude-01-selection.png" | Out-Null
    Drag 40 20 1760 620
    Start-Sleep -Milliseconds 1600
    Screenshot $dir "exclude-02-overlay.png" | Out-Null
    Start-Sleep -Milliseconds 200
    Press 0x1B
    Start-Sleep -Milliseconds 400

    $logText = if (Test-Path (Get-LogPath $dir)) { Get-Content (Get-LogPath $dir) -Raw -Encoding UTF8 } else { "" }
    $overlayShown = $logText.Contains("覆盖层已显示")
    $translated = $logText.Contains("翻译完成")
    $errors = ([regex]::Matches($logText, "\[ERROR\]")).Count
    Log ("log overlayShown=$overlayShown translated=$translated errors=$errors")
    Log "请人工查看 exclude-01-selection.png / exclude-02-overlay.png：若看不到暗化层/译文，则 F-14 排除成功。"
    $reportPath = Join-Path $dir "exclude-report.txt"
    [System.IO.File]::WriteAllText($reportPath, $report.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    Write-Output ("report: " + $reportPath)
}
finally {
    if ($page) { try { Stop-Process -Id $page.Process.Id -Force } catch { } }
    if ($app) { Stop-TestApp $app }
    if ($server) { Stop-FakeServer $server }
}
