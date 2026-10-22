[CmdletBinding()]
param()

$startupDir = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startupDir 'BtAudioSink.lnk'

if (Test-Path -LiteralPath $shortcutPath) {
    Remove-Item -LiteralPath $shortcutPath -Force
    Write-Host "已删除开机自启快捷方式：$shortcutPath"
}
else {
    Write-Host '没有找到开机自启快捷方式（未安装过）。'
}
