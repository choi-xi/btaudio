[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$startScript = Join-Path $PSScriptRoot 'start.ps1'
$startupDir = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startupDir 'BtAudioSink.lnk'

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = (Get-Command powershell.exe).Source
$shortcut.Arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$startScript`""
$shortcut.WorkingDirectory = Split-Path $PSScriptRoot -Parent
$shortcut.WindowStyle = 7
$shortcut.Description = '电脑作为蓝牙音频接收端（btaudio watch 后台守护）'
$shortcut.Save()

Write-Host "已创建开机自启快捷方式："
Write-Host "  $shortcutPath"
Write-Host "下次登录 Windows 会自动启动 btaudio watch（隐藏窗口）。"
