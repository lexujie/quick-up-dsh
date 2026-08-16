# Generates dsh-launcher.ico (256x256) for the launcher executable.
# Usage: powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File tools\make-icon.ps1 [output.ico]
param(
    [string]$Out = ''
)
Add-Type -AssemblyName System.Drawing

if ($Out -eq '') { $Out = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\dsh-launcher.ico' }
$Out = [System.IO.Path]::GetFullPath($Out)

$size = 256
$bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.Clear([System.Drawing.Color]::Transparent)

# 圆角深蓝渐变背景
$rect = New-Object System.Drawing.Rectangle(10, 10, 236, 236)
$d = 52
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
$path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
$path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
$path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
$path.CloseFigure()
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $rect,
    [System.Drawing.Color]::FromArgb(15, 23, 42),
    [System.Drawing.Color]::FromArgb(37, 99, 235),
    45)
$g.FillPath($brush, $path)

# 白色粗体 "D"
$font = New-Object System.Drawing.Font('Segoe UI', 152, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = [System.Drawing.StringAlignment]::Center
$sf.LineAlignment = [System.Drawing.StringAlignment]::Center
$g.DrawString('D', $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF(0, 0, $size, $size)), $sf)

# 右下角小闪电点缀
$bolt = New-Object System.Drawing.Drawing2D.GraphicsPath
$bolt.AddPolygon([System.Drawing.Point[]]@(
    (New-Object System.Drawing.Point(168, 168)),
    (New-Object System.Drawing.Point(150, 208)),
    (New-Object System.Drawing.Point(172, 208)),
    (New-Object System.Drawing.Point(160, 236)),
    (New-Object System.Drawing.Point(198, 196)),
    (New-Object System.Drawing.Point(176, 196)),
    (New-Object System.Drawing.Point(190, 168))
))
$boltBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 251, 191, 36))
$g.FillPath($boltBrush, $bolt)
$boltBrush.Dispose()

$g.Dispose()

$h = $bmp.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($h)
$fs = [System.IO.File]::Create($Out)
$icon.Save($fs)
$fs.Close()
$icon.Dispose()
$bmp.Dispose()

Write-Host "icon written: $Out"
