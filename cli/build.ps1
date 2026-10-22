param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

if (Test-Path -LiteralPath $dist) {
    Remove-Item -LiteralPath $dist -Recurse -Force
}

dotnet publish (Join-Path $root 'btaudio-cli.csproj') `
    -c $Configuration -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o $dist

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败（退出码 $LASTEXITCODE）"
}

Write-Host "`n构建完成："
Get-ChildItem -LiteralPath $dist | Select-Object Name, Length | Format-Table -AutoSize
