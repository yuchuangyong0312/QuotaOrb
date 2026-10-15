# Render every app icon from the original SVG. No downloaded art or OS icons.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
$root = Split-Path -Parent $PSScriptRoot
$assetDir = Join-Path $root 'src\QuotaOrb\Assets'
[xml]$svg = Get-Content -LiteralPath (Join-Path $assetDir 'QuotaOrb.svg') -Raw
$culture = [Globalization.CultureInfo]::InvariantCulture
function Number([string]$Value) { [double]::Parse($Value,$culture) }
$brushes = @{}
foreach ($gradient in $svg.svg.defs.linearGradient) {
    $brush = [Windows.Media.LinearGradientBrush]::new()
    $brush.StartPoint = [Windows.Point]::new((Number $gradient.x1),(Number $gradient.y1))
    $brush.EndPoint = [Windows.Point]::new((Number $gradient.x2),(Number $gradient.y2))
    foreach ($stop in $gradient.stop) {
        $brush.GradientStops.Add([Windows.Media.GradientStop]::new([Windows.Media.ColorConverter]::ConvertFromString($stop.'stop-color'),(Number $stop.offset)))
    }
    $brushes[$gradient.id] = $brush
}
function SvgBrush([string]$Value) {
    if ([string]::IsNullOrEmpty($Value) -or $Value -eq 'none') { return $null }
    if ($Value -match '^url\(#([a-zA-Z0-9_-]+)\)$') {
        if (-not $brushes.ContainsKey($Matches[1])) { throw 'Unknown SVG gradient.' }
        return $brushes[$Matches[1]]
    }
    return [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($Value))
}
$frames = @()
foreach ($size in @(16,20,24,32,40,48,64,128,256)) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    try {
        $drawing.PushTransform([Windows.Media.ScaleTransform]::new($size/256.0,$size/256.0))
        foreach ($shape in $svg.svg.ChildNodes) {
            if ($shape.NodeType -ne [Xml.XmlNodeType]::Element -or $shape.LocalName -in @('title','desc','defs')) { continue }
            $fill = SvgBrush $shape.GetAttribute('fill')
            $pen = $null
            if ($shape.HasAttribute('stroke')) {
                $pen = [Windows.Media.Pen]::new((SvgBrush $shape.GetAttribute('stroke')),(Number $shape.GetAttribute('stroke-width')))
                if ($shape.GetAttribute('stroke-linecap') -eq 'round') { $pen.StartLineCap = $pen.EndLineCap = [Windows.Media.PenLineCap]::Round }
            }
            switch ($shape.LocalName) {
                'rect' {
                    $bounds = [Windows.Rect]::new((Number $shape.x),(Number $shape.y),(Number $shape.width),(Number $shape.height))
                    $radius = Number $shape.rx
                    $drawing.DrawRoundedRectangle($fill,$pen,$bounds,$radius,$radius)
                }
                'circle' { $drawing.DrawEllipse($fill,$pen,[Windows.Point]::new((Number $shape.cx),(Number $shape.cy)),(Number $shape.r),(Number $shape.r)) }
                'path' { $drawing.DrawGeometry($fill,$pen,[Windows.Media.Geometry]::Parse($shape.d)) }
                default { throw ('Unsupported icon SVG element: '+$shape.LocalName) }
            }
        }
        $drawing.Pop()
    } finally { $drawing.Close() }
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        $bytes = $stream.ToArray()
        $frames += [pscustomobject]@{Size=$size;Bytes=$bytes}
        if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $assetDir 'QuotaOrb.png'),$bytes) }
    } finally { $stream.Dispose() }
}
$output = Join-Path $assetDir 'QuotaOrb.ico'
$writer = [IO.BinaryWriter]::new([IO.File]::Create($output))
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
Get-Item -LiteralPath $output,(Join-Path $assetDir 'QuotaOrb.png') | Select-Object Name,Length
