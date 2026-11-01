param(
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\assets\branding'
}

$directory = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($directory) | Out-Null

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

function New-RoundedPath([System.Drawing.RectangleF]$rect, [single]$radius) {
    $diameter = $radius * 2.0
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($rect.X, $rect.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($rect.Right - $diameter, $rect.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($rect.Right - $diameter, $rect.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-TrayXBitmap([int]$size) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $padding = [single][Math]::Max(0.5, $size * 0.02)
    $radius = [single]($size * 0.24)
    $width = [single]($size - 2.0 * $padding)
    $rect = [System.Drawing.RectangleF]::new($padding, $padding, $width, $width)
    $path = New-RoundedPath $rect $radius

    $from = [System.Drawing.Color]::FromArgb(255, 124, 92, 255)
    $to = [System.Drawing.Color]::FromArgb(255, 255, 63, 148)
    $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect, $from, $to, [single]45.0)
    $graphics.FillPath($gradient, $path)
    $gradient.Dispose()

    if ($size -ge 24) {
        $borderWidth = [single][Math]::Max(0.75, $size * 0.018)
        $borderPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(46, 255, 255, 255), $borderWidth)
        $inset = [single]($size * 0.055)
        $innerWidth = [single]($size - 2.0 * $inset)
        $innerRect = [System.Drawing.RectangleF]::new($inset, $inset, $innerWidth, $innerWidth)
        $innerPath = New-RoundedPath $innerRect ([single]($radius * 0.82))
        $graphics.DrawPath($borderPen, $innerPath)
        $innerPath.Dispose()
        $borderPen.Dispose()
    }

    $stroke = [single][Math]::Max(1.4, $size * 0.11)
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, $stroke)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $start = [single]($size * 0.30)
    $end = [single]($size * 0.70)
    $graphics.DrawLine($pen, $start, $start, $end, $end)
    $graphics.DrawLine($pen, $end, $start, $start, $end)
    $pen.Dispose()

    $path.Dispose()
    $graphics.Dispose()
    return $bitmap
}

$images = New-Object System.Collections.ArrayList
foreach ($size in $sizes) {
    $bitmap = New-TrayXBitmap $size
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    [void]$images.Add([pscustomobject]@{ Size = $size; Data = $stream.ToArray() })
    $stream.Dispose()

    if ($size -eq 256) {
        $bitmap.Save((Join-Path $directory 'trayx-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    }

    $bitmap.Dispose()
}

$iconPath = Join-Path $directory 'trayx.ico'
$file = [System.IO.File]::Open($iconPath, [System.IO.FileMode]::Create)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)

    $offset = 6 + 16 * $images.Count
    foreach ($image in $images) {
        $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Data.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Data.Length
    }

    foreach ($image in $images) {
        $writer.Write($image.Data)
    }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Output "Icon written to $iconPath"
