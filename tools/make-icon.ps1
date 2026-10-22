# 生成应用图标 shared/app.ico（圆角圆底 + 白色喇叭）
# 用法：pwsh -File tools/make-icon.ps1
param(
    [string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'shared\app.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $graphics = [System.Drawing.Graphics]::FromImage($bmp)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(1, [int]($size * 0.04))
    $diameter = $size - 2 * $pad
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(37, 99, 235))
    $graphics.FillEllipse($bg, $pad, $pad, $diameter, $diameter)
    $bg.Dispose()

    $s = $size / 256.0
    $speaker = @(
        (New-Object System.Drawing.PointF (92 * $s), (104 * $s)),
        (New-Object System.Drawing.PointF (116 * $s), (104 * $s)),
        (New-Object System.Drawing.PointF (148 * $s), (72 * $s)),
        (New-Object System.Drawing.PointF (148 * $s), (184 * $s)),
        (New-Object System.Drawing.PointF (116 * $s), (152 * $s)),
        (New-Object System.Drawing.PointF (92 * $s), (152 * $s))
    )
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $graphics.FillPolygon($white, $speaker)
    $white.Dispose()

    $penWidth = [Math]::Max(1.0, $size / 22.0)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single]$penWidth)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $rect = New-Object System.Drawing.RectangleF (150 * $s), (86 * $s), (62 * $s), (84 * $s)
    $graphics.DrawArc($pen, $rect, -55.0, 110.0)
    $pen.Dispose()

    $graphics.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$payloads = New-Object System.Collections.Generic.List[byte[]]
foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $payloads.Add($stream.ToArray())
    $stream.Dispose()
}

$directory = Split-Path $Out -Parent
if (-not (Test-Path $directory)) {
    New-Item -ItemType Directory -Force $directory | Out-Null
}

$file = [System.IO.File]::Create($Out)
$writer = New-Object System.IO.BinaryWriter $file
try {
    $writer.Write([UInt16]0)              # reserved
    $writer.Write([UInt16]1)              # type: icon
    $writer.Write([UInt16]$sizes.Count)   # count

    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $size = $sizes[$i]
        $data = $payloads[$i]
        $dimension = if ($size -ge 256) { 0 } else { $size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)            # colors
        $writer.Write([byte]0)            # reserved
        $writer.Write([UInt16]1)          # planes
        $writer.Write([UInt16]32)         # bits per pixel
        $writer.Write([UInt32]$data.Length)
        $writer.Write([UInt32]$offset)
        $offset += $data.Length
    }

    foreach ($data in $payloads) {
        $writer.Write($data)
    }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Host "图标已生成：$Out（$([System.IO.File]::ReadAllBytes($Out).Length) 字节，$($sizes.Count) 个尺寸）"
