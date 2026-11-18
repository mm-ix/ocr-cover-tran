param(
    [int]$Cycles = 100,
    [string]$GreenExe = "",
    [int]$Port = 8768
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ui-common.ps1")

if ([string]::IsNullOrWhiteSpace($GreenExe)) {
    $GreenExe = (Resolve-Path (Join-Path $PSScriptRoot "..\publish\green\ShotTrans.exe")).Path
}
if (-not (Test-Path $GreenExe)) { throw "找不到绿色版：$GreenExe" }

$dir = New-TestDir "stress"
$server = $null
try {
    $server = Start-FakeServer -Port $Port
    Write-TestConfig -Dir $dir -Url "http://127.0.0.1:$Port/v1" -Model "fake" -HistoryEnabled $false
    $target = Join-Path $dir "ShotTrans.exe"
    Copy-Item $GreenExe $target -Force

    Write-Output "== 核心链路压力测试：$Cycles 次（OCR + HTTP + 覆盖层生命周期） =="
    $p = Start-Process -FilePath $target -WorkingDirectory $dir -ArgumentList @("--stress", "$Cycles") -PassThru
    if (-not $p.WaitForExit(600000)) {
        try { Stop-Process -Id $p.Id -Force } catch { }
        throw "压力测试超时"
    }
    $reportPath = Join-Path $dir "stress-output\stress-report.txt"
    if (Test-Path $reportPath) {
        $text = Get-Content $reportPath -Raw -Encoding UTF8
        Write-Output $text
        if ($p.ExitCode -ne 0) { throw "压力测试失败（exit $($p.ExitCode)）" }
        Write-Output "report: $reportPath"
    } else {
        throw "未生成 stress-report.txt（exit $($p.ExitCode)）"
    }
}
finally {
    if ($server) { Stop-FakeServer $server }
}
