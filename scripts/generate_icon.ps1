param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\SerialBridge.App\Assets\app.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)

function New-Color([string]$hex) {
    return [System.Windows.Media.ColorConverter]::ConvertFromString($hex)
}

function New-Brush([string]$hex) {
    $brush = [System.Windows.Media.SolidColorBrush]::new((New-Color $hex))
    $brush.Freeze()
    return $brush
}

function New-Pen([string]$hex, [double]$thickness) {
    $pen = [System.Windows.Media.Pen]::new((New-Brush $hex), $thickness)
    $pen.Freeze()
    return $pen
}

function New-PolygonGeometry([double[][]]$points) {
    $geometry = [System.Windows.Media.StreamGeometry]::new()
    $context = $geometry.Open()
    try {
        $context.BeginFigure(
            [System.Windows.Point]::new($points[0][0], $points[0][1]),
            $true,
            $true)
        for ($index = 1; $index -lt $points.Count; $index++) {
            $context.LineTo(
                [System.Windows.Point]::new($points[$index][0], $points[$index][1]),
                $true,
                $false)
        }
    }
    finally {
        $context.Close()
    }
    $geometry.Freeze()
    return $geometry
}

function New-IconFrame([int]$size) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    try {
        $scale = $size / 256.0
        $drawing.PushTransform([System.Windows.Media.ScaleTransform]::new($scale, $scale))

        $gradient = [System.Windows.Media.LinearGradientBrush]::new()
        $gradient.StartPoint = [System.Windows.Point]::new(0.5, 0)
        $gradient.EndPoint = [System.Windows.Point]::new(0.5, 1)
        [void]$gradient.GradientStops.Add([System.Windows.Media.GradientStop]::new((New-Color '#86A8F3'), 0))
        [void]$gradient.GradientStops.Add([System.Windows.Media.GradientStop]::new((New-Color '#2F5AC8'), 0.58))
        [void]$gradient.GradientStops.Add([System.Windows.Media.GradientStop]::new((New-Color '#2147AA'), 1))
        $gradient.Freeze()

        $body = [System.Windows.Rect]::new(7, 7, 242, 242)
        $drawing.DrawRoundedRectangle($gradient, (New-Pen '#163B99' 5), $body, 50, 50)
        $inner = [System.Windows.Rect]::new(11, 11, 234, 234)
        $drawing.DrawRoundedRectangle($null, (New-Pen '#B8D3FF' 2), $inner, 46, 46)

        $white = New-Brush '#FFFFFF'
        if ($size -le 20) {
            # At 16/20 px one bold two-headed arrow avoids the white blob produced by stacked details.
            $twoWayArrow = New-PolygonGeometry @(
                @(30, 128), @(86, 72), @(86, 104), @(170, 104),
                @(170, 72), @(226, 128), @(170, 184), @(170, 152),
                @(86, 152), @(86, 184)
            )
            $drawing.DrawGeometry($white, $null, $twoWayArrow)
        }
        elseif ($size -le 32) {
            # Tray sizes omit endpoint details so the bidirectional flow remains legible.
            $rightArrow = New-PolygonGeometry @(
                @(43, 67), @(143, 67), @(143, 43), @(211, 99),
                @(143, 155), @(143, 131), @(43, 131)
            )
            $leftArrow = New-PolygonGeometry @(
                @(213, 135), @(113, 135), @(113, 111), @(45, 167),
                @(113, 223), @(113, 199), @(213, 199)
            )
            $drawing.DrawGeometry($white, $null, $rightArrow)
            $drawing.DrawGeometry($white, $null, $leftArrow)
        }
        else {
            $terminalRect = [System.Windows.Rect]::new(28, 105, 57, 47)
            $drawing.DrawRoundedRectangle($white, $null, $terminalRect, 11, 11)
            foreach ($x in @(44, 57, 70)) {
                $drawing.DrawEllipse((New-Brush '#2147AA'), $null, [System.Windows.Point]::new($x, 129), 4.5, 4.5)
            }

            $rightArrow = New-PolygonGeometry @(
                @(88, 83), @(139, 83), @(139, 57), @(177, 97),
                @(139, 137), @(139, 111), @(88, 111), @(78, 101), @(78, 93)
            )
            $leftArrow = New-PolygonGeometry @(
                @(174, 145), @(123, 145), @(123, 119), @(85, 159),
                @(123, 199), @(123, 173), @(174, 173), @(184, 163), @(184, 155)
            )
            $drawing.DrawGeometry($white, $null, $rightArrow)
            $drawing.DrawGeometry($white, $null, $leftArrow)

            $drawing.DrawEllipse($white, $null, [System.Windows.Point]::new(205, 129), 28, 28)
            $drawing.DrawEllipse((New-Brush '#2147AA'), $null, [System.Windows.Point]::new(205, 129), 13, 13)
        }

        $drawing.Pop()
    }
    finally {
        $drawing.Close()
    }

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    [void]$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        return ,$stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

$frames = foreach ($size in $sizes) {
    [PSCustomObject]@{ Size = $size; Data = (New-IconFrame $size) }
}

$fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [System.IO.Path]::GetDirectoryName($fullOutputPath)
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$file = [System.IO.File]::Open($fullOutputPath, [System.IO.FileMode]::Create)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)

    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Data.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Data.Length
    }
    foreach ($frame in $frames) {
        $writer.Write($frame.Data)
    }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Host "Generated $($frames.Count) PNG icon frames: $fullOutputPath"
