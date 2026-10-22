[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$release = Join-Path $root 'release'
$cleanVersion = $Version.Trim().TrimStart('v', 'V')

if ($cleanVersion -notmatch '^\d+\.\d+\.\d+([-.+][0-9A-Za-z.\-]+)?$') {
    throw "版本号不合法：$Version（示例：1.0.0、v1.0.0）"
}

if (Test-Path -LiteralPath $release) {
    Remove-Item -LiteralPath $release -Recurse -Force
}
New-Item -ItemType Directory -Path $release | Out-Null

$publishArgs = @(
    '-c', $Configuration
    '-r', 'win-x64'
    '--self-contained', 'true'
    '-p:PublishSingleFile=true'
    '-p:EnableCompressionInSingleFile=true'
    '-p:IncludeNativeLibrariesForSelfExtract=true'
    '-p:DebugType=none'
    "-p:Version=$cleanVersion"
)

function Invoke-Publish([string]$project, [string]$output) {
    Write-Host "`n==> 发布 $(Split-Path $project -Leaf)" -ForegroundColor Cyan
    dotnet publish $project @publishArgs -o $output
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败：$project（退出码 $LASTEXITCODE）"
    }
}

function New-Stage([string]$name) {
    $path = Join-Path $release $name
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    return $path
}

$guiRaw = Join-Path $release '_gui-raw'
$cliRaw = Join-Path $release '_cli-raw'
Invoke-Publish (Join-Path $root 'gui\btaudio-gui.csproj') $guiRaw
Invoke-Publish (Join-Path $root 'cli\btaudio-cli.csproj') $cliRaw

$guiExe = Join-Path $guiRaw 'btaudio-gui.exe'
$cliExe = Join-Path $cliRaw 'btaudio.exe'
if (-not (Test-Path -LiteralPath $guiExe)) { throw "未生成 $guiExe" }
if (-not (Test-Path -LiteralPath $cliExe)) { throw "未生成 $cliExe" }

$guiStage = New-Stage 'btaudio-gui-win-x64'
Copy-Item -LiteralPath $guiExe -Destination $guiStage
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $guiStage
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $guiStage
Compress-Archive -Path (Join-Path $guiStage '*') -DestinationPath (Join-Path $release 'btaudio-gui-win-x64.zip') -CompressionLevel Optimal

$cliStage = New-Stage 'btaudio-cli-win-x64'
Copy-Item -LiteralPath $cliExe -Destination $cliStage
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $cliStage
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $cliStage
Copy-Item -LiteralPath (Join-Path $root 'cli\scripts') -Destination (Join-Path $cliStage 'scripts') -Recurse
Compress-Archive -Path (Join-Path $cliStage '*') -DestinationPath (Join-Path $release 'btaudio-cli-win-x64.zip') -CompressionLevel Optimal

Remove-Item -LiteralPath $guiRaw, $cliRaw, $guiStage, $cliStage -Recurse -Force

$hashLines = foreach ($zip in Get-ChildItem -LiteralPath $release -Filter '*.zip' | Sort-Object Name) {
    $hash = (Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    '{0}  {1}' -f $hash, $zip.Name
}
$hashText = ($hashLines -join "`n") + "`n"
[System.IO.File]::WriteAllText((Join-Path $release 'SHA256SUMS.txt'), $hashText, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "`n==> 发布产物（版本 $cleanVersion）" -ForegroundColor Green
Get-ChildItem -LiteralPath $release | Select-Object Name, @{ n = 'Size(MB)'; e = { '{0:N1}' -f ($_.Length / 1MB) } } | Format-Table -AutoSize
Write-Host 'SHA256SUMS.txt 已生成。'
