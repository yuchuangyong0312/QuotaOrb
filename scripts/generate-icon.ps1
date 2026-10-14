# Original QuotaOrb quota-ring/Q geometry; no OS icon resources are copied.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'src\QuotaOrb\Assets\QuotaOrb.ico'
$frames = @()
foreach ($size in @(16,24,32,48,64,128,256)) {
    $canvas = New-Object System.Drawing.Bitmap 1024,1024
    $graphics = [Drawing.Graphics]::FromImage($canvas)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform(4,4)
    $background = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#22242B'))
    $track = New-Object Drawing.Pen ([Drawing.ColorTranslator]::FromHtml('#414653')),28
    $accent = New-Object Drawing.Pen ([Drawing.ColorTranslator]::FromHtml('#2ED6C7')),28
    $tail = New-Object Drawing.Pen ([Drawing.ColorTranslator]::FromHtml('#85BBFF')),28
    $accent.StartCap = $accent.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $tail.StartCap = $tail.EndCap = [Drawing.Drawing2D.LineCap]::Round
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.FillEllipse($background,10,10,236,236)
        $graphics.DrawEllipse($track,47,47,162,162)
        $graphics.DrawArc($accent,47,47,162,162,-90,270)
        $graphics.DrawLine($tail,164,164,204,204)
        $small = New-Object Drawing.Bitmap $size,$size
        $resize = [Drawing.Graphics]::FromImage($small)
        $stream = New-Object IO.MemoryStream
        try {
            $resize.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $resize.DrawImage($canvas,0,0,$size,$size)
            $small.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
            $frames += [pscustomobject]@{Size=$size;Bytes=$stream.ToArray()}
        } finally { $stream.Dispose(); $resize.Dispose(); $small.Dispose() }
    } finally { $tail.Dispose(); $accent.Dispose(); $track.Dispose(); $background.Dispose(); $graphics.Dispose(); $canvas.Dispose() }
}
$writer = New-Object IO.BinaryWriter ([IO.File]::Create($output))
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose() }
Get-Item -LiteralPath $output | Select-Object Name,Length
