<#
.SYNOPSIS
    Renders the JetQueries mark (src/JetQueries.Wpf/Assets/Logo.xaml) into src/JetQueries.Wpf/Assets/JetQueries.ico.

.DESCRIPTION
    Every size Windows asks for (16, 20, 24, 32, 40, 48, 64, 128, 256) is rendered from the vector drawing with WPF and
    stored as PNG inside the .ico. Run it after changing Logo.xaml; the .ico is committed, so builds do not need it.
    Windows PowerShell (STA) is required for WPF:  powershell -ExecutionPolicy Bypass -File scripts/make-icon.ps1
#>
[CmdletBinding()]
param(
    [string]$PreviewDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'src/JetQueries.Wpf/Assets'
$xaml = Get-Content -Raw -Encoding UTF8 (Join-Path $assets 'Logo.xaml')
$dictionary = [System.Windows.Markup.XamlReader]::Parse($xaml)
$drawing = $dictionary['Brand.LogoDrawing']

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @()
foreach ($size in $sizes) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $scale = $size / 256.0
    $context.PushTransform((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))
    $context.DrawDrawing($drawing)
    $context.Pop()
    $context.Close()

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    $images += , @{ Size = $size; Bytes = $stream.ToArray() }
    if ($PreviewDir) {
        New-Item -ItemType Directory -Force -Path $PreviewDir | Out-Null
        [System.IO.File]::WriteAllBytes((Join-Path $PreviewDir "logo-$size.png"), $stream.ToArray())
    }
}

# ICO: a header, one directory entry per image, then the PNG data.
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($out)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$image.Bytes.Length)
    $writer.Write([UInt32]$offset)
    $offset += $image.Bytes.Length
}
foreach ($image in $images) {
    $writer.Write($image.Bytes)
}
$writer.Flush()
$target = Join-Path $assets 'JetQueries.ico'
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
Write-Host "Wrote $target ($($images.Count) sizes, $($out.Length) bytes)"
