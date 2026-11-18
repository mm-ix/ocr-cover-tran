param(
    [int]$Cycles = 100,
    [string]$GreenExe = "",
    [int]$Port = 8765
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ui-common.ps1")

if ([string]::IsNullOrWhiteSpace($GreenExe)) {
    $GreenExe = (Resolve-Path (Join-Path $PSScriptRoot "..\publish\green\ShotTrans.exe")).Path
}
if (-not (Test-Path $GreenExe)) { throw "找不到绿色版：$GreenExe" }
Assert-DesktopUnlocked

$dir = New-TestDir "stability"
$report = New-Object System.Text.StringBuilder
function Log($s) { $report.AppendLine($s) | Out-Null; Write-Output $s }

$server = $null
$app = $null
$page = $null
try {
    Log "== 稳定性测试：$Cycles 次连续触发 =="
    Log ("exe: " + $GreenExe)
    $env:SHOTRANS_DEBUG_CAPTURE_OVERLAY = "1"
    $server = Start-FakeServer -Port $Port
    Log ("fake server pid=" + $server.Id)

    Write-TestConfig -Dir $dir -Url "http://127.0.0.1:$Port/v1" -Model "fake" -HistoryEnabled $false
    $app = Start-TestApp -Exe $GreenExe -Dir $dir
    Log ("app pid=" + $app.Id)

    $page = Start-NativeTestPage (Join-Path $dir "ShotTrans.exe") $dir
    Log ("test page pid=" + $page.Process.Id)
    Start-Sleep -Milliseconds 800

    # 预热 3 次，排除首次 JIT / 单文件解压的影响
    foreach ($i in 1..3) {
        HotkeyScreenshot
        Start-Sleep -Milliseconds 300
        Drag 40 20 1760 620
        Start-Sleep -Milliseconds 650
        Press 0x1B
        Start-Sleep -Milliseconds 150
    }

    $proc = Get-Process -Id $app.Id
    $memBefore = $proc.WorkingSet64
    $privateBefore = $proc.PrivateMemorySize64
    Log ("memory before: WS=" + [math]::Round($memBefore / 1MB, 1) + "MB private=" + [math]::Round($privateBefore / 1MB, 1) + "MB")

    $total = [System.Diagnostics.Stopwatch]::StartNew()
    $failedCycle = 0
    for ($i = 1; $i -le $Cycles; $i++) {
        if ($app.HasExited) { throw "应用在第 $i 次触发前已退出" }
        HotkeyScreenshot
        Start-Sleep -Milliseconds 260
        Drag 40 20 1760 620
        Start-Sleep -Milliseconds 650

        if ($i -eq 2 -or $i -eq $Cycles) {
            Screenshot $dir ("cycle-" + $i.ToString("000") + ".png") | Out-Null
        }

        Press 0x1B
        Start-Sleep -Milliseconds 140

        if ($i % 10 -eq 0) {
            $p = Get-Process -Id $app.Id
            Log ("cycle $i / $Cycles  WS=" + [math]::Round($p.WorkingSet64 / 1MB, 1) + "MB")
        }
    }
    $total.Stop()

    Start-Sleep -Milliseconds 800
    $proc = Get-Process -Id $app.Id
    $memAfter = $proc.WorkingSet64
    $privateAfter = $proc.PrivateMemorySize64
    $logPath = Get-LogPath $dir
    $logText = if (Test-Path $logPath) { Get-Content $logPath -Raw -Encoding UTF8 } else { "" }
    $errorCount = ([regex]::Matches($logText, "\[ERROR\]")).Count
    $ocrCount = ([regex]::Matches($logText, "OCR 完成")).Count
    $transCount = ([regex]::Matches($logText, "翻译完成")).Count
    $visible = [WinIo]::CountVisibleWindows([uint32]$app.Id)

    Log ("elapsed: " + [math]::Round($total.Elapsed.TotalSeconds, 1) + "s  avg=" + [math]::Round($total.Elapsed.TotalMilliseconds / $Cycles, 0) + "ms/cycle")
    Log ("memory after:  WS=" + [math]::Round($memAfter / 1MB, 1) + "MB private=" + [math]::Round($privateAfter / 1MB, 1) + "MB")
    Log ("memory growth: WS=" + [math]::Round(($memAfter - $memBefore) / 1MB, 1) + "MB")
    Log ("visible windows left: $visible")
    Log ("log OCR 完成=$ocrCount 翻译完成=$transCount ERROR=$errorCount")
    Log ("app alive: " + (-not $app.HasExited))

    $memGrowthMb = ($memAfter - $memBefore) / 1MB
    $pass = (-not $app.HasExited) -and ($visible -eq 0) -and ($errorCount -eq 0) -and ($memGrowthMb -lt 120) -and ($ocrCount -ge $Cycles -and $transCount -ge $Cycles)
    Log ("RESULT: " + ($(if ($pass) { "PASS" } else { "FAIL" })))

    $reportPath = Join-Path $dir "stability-report.txt"
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
