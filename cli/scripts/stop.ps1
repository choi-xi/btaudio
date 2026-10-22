[CmdletBinding()]
param(
    [string]$Exe = ''
)

# 先请求优雅退出（守护会释放蓝牙连接并写状态），超时才强制结束
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $base = Split-Path $PSScriptRoot -Parent
    $Exe = Join-Path $base 'btaudio.exe'
    if (-not (Test-Path -LiteralPath $Exe)) {
        $Exe = Join-Path $base 'dist\btaudio.exe'
    }
}
$exePath = [System.IO.Path]::GetFullPath($Exe)
if (Test-Path -LiteralPath $exePath) {
    & $exePath stop | Out-Host
}

$targets = @(Get-CimInstance Win32_Process -Filter "Name='btaudio.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match 'watch' })

if ($targets.Count -eq 0) {
    Write-Host 'btaudio watch 未在运行'
    exit 0
}

foreach ($target in $targets) {
    Write-Host "强制结束 btaudio watch（PID $($target.ProcessId)）"
    Stop-Process -Id $target.ProcessId -Force
}
