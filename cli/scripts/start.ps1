[CmdletBinding()]
param(
    [string]$Device = '',
    [string]$Exe = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $base = Split-Path $PSScriptRoot -Parent
    $Exe = Join-Path $base 'btaudio.exe'
    if (-not (Test-Path -LiteralPath $Exe)) {
        $Exe = Join-Path $base 'dist\btaudio.exe'
    }
}
$exePath = [System.IO.Path]::GetFullPath($Exe)

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "找不到 $exePath，请先运行 build.ps1"
}

$existing = @(Get-CimInstance Win32_Process -Filter "Name='btaudio.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match 'watch' })
if ($existing.Count -gt 0) {
    Write-Host "btaudio watch 已在运行（PID $($existing[0].ProcessId)）"
    exit 0
}

$arguments = @('watch')
if ($Device -ne '') {
    $arguments += @('--device', $Device)
}

Start-Process -FilePath $exePath -ArgumentList $arguments -WindowStyle Hidden
Start-Sleep -Seconds 3

& $exePath status
