$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetPath = Join-Path $PSScriptRoot '..\assets'
[IO.Directory]::CreateDirectory($assetPath) | Out-Null
$iconStream = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter($iconStream)
$sizes = @(16, 32, 48, 64, 256)
$images = @()

try {
    foreach ($size in $sizes) {
        $bitmap = New-Object Drawing.Bitmap($size, $size)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $path = New-Object Drawing.Drawing2D.GraphicsPath
        $background = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(28, 60, 102))
        $wave = New-Object Drawing.Pen([Drawing.Color]::FromArgb(238, 238, 238), 16)
        $pngStream = New-Object IO.MemoryStream
        try {
            $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.ScaleTransform($size / 256.0, $size / 256.0)
            $path.AddArc(8, 8, 64, 64, 180, 90)
            $path.AddArc(184, 8, 64, 64, 270, 90)
            $path.AddArc(184, 184, 64, 64, 0, 90)
            $path.AddArc(8, 184, 64, 64, 90, 90)
            $path.CloseFigure()
            $graphics.FillPath($background, $path)
            $wave.StartCap = [Drawing.Drawing2D.LineCap]::Round
            $wave.EndCap = [Drawing.Drawing2D.LineCap]::Round
            foreach ($index in 0..4) {
                $halfHeight = @(16, 40, 64, 40, 16)[$index]
                $graphics.DrawLine($wave, 64 + 32 * $index, 128 - $halfHeight, 64 + 32 * $index, 128 + $halfHeight)
            }
            $bitmap.Save($pngStream, [Drawing.Imaging.ImageFormat]::Bmp)
            $bmpBytes = $pngStream.ToArray()
            $maskBytes = [int]([Math]::Ceiling($size / 32.0) * 4 * $size)
            $iconFrame = New-Object byte[] ($bmpBytes.Length - 14 + $maskBytes)
            [Array]::Copy($bmpBytes, 14, $iconFrame, 0, $bmpBytes.Length - 14)
            [BitConverter]::GetBytes([int]($size * 2)).CopyTo($iconFrame, 8)
            $images += ,$iconFrame
            if ($size -eq 256) { $bitmap.Save((Join-Path $assetPath 'soundboard-logo.png'), [Drawing.Imaging.ImageFormat]::Png) }
        }
        finally { $pngStream.Dispose(); $wave.Dispose(); $background.Dispose(); $path.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    foreach ($index in 0..($sizes.Count - 1)) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $images[$index].Length
    }
    foreach ($image in $images) { $writer.Write([byte[]]$image) }
    $writer.Flush()
    [IO.File]::WriteAllBytes((Join-Path $assetPath 'soundboard-logo.ico'), $iconStream.ToArray())
}
finally { $writer.Dispose(); $iconStream.Dispose() }
