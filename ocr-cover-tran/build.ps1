param(
    [string]$Dotnet = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($Dotnet)) {
    $candidate = Join-Path (Split-Path $root -Parent) ".dotnet\sdk\dotnet.exe"
    if (Test-Path $candidate) { $Dotnet = $candidate } else { $Dotnet = "dotnet" }
}
if (-not (Test-Path $Dotnet) -and $Dotnet -ne "dotnet") {
    throw "找不到 dotnet：$Dotnet"
}

$out = Join-Path $root "publish\green"
$project = Join-Path $root "src\ShotTrans\ShotTrans.csproj"

& $Dotnet publish $project `
    -c Release -r win-x64 --self-contained true `
    -p:PublishTrimmed=true -p:TrimMode=partial -p:_SuppressWinFormsTrimError=true `
    -p:TrimmerRootAssembly=ShotTrans `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $out

if ($LASTEXITCODE -ne 0) { throw "发布失败（exit $LASTEXITCODE）" }

# 清理测试/运行产物，保证绿色目录可分发
$cleanItems = @("config.json", "history.db", "logs", "selftest-output", "stress-output",
                "selftest-report.txt", "run-selftest.txt", "run-stress.txt")
foreach ($item in $cleanItems) {
    Remove-Item (Join-Path $out $item) -Recurse -Force -ErrorAction SilentlyContinue
}
Remove-Item (Join-Path $out "ShotTrans.pdb") -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root "README.md") (Join-Path $out "README.md") -Force
Copy-Item (Join-Path $root "使用说明.md") (Join-Path $out "使用说明.md") -Force

$exe = Join-Path $out "ShotTrans.exe"
$mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Output "绿色版：$exe  ($mb MB)"
