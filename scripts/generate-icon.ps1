# Render the code-native DiskPeek mark into a multi-resolution Windows icon.
# SPDX-License-Identifier: AGPL-3.0-or-later
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
$repo = Split-Path -Parent $PSScriptRoot
$drawings = @(
    @('#10151e', 'M48,0 L208,0 Q256,0 256,48 L256,208 Q256,256 208,256 L48,256 Q0,256 0,208 L0,48 Q0,0 48,0 Z'),
    @('#63d8cc', 'M56,40 L118,40 L118,216 L56,216 Q44,216 44,204 L44,52 Q44,40 56,40 Z'),
    @('#638bd5', 'M134,40 L200,40 Q212,40 212,52 L212,120 L134,120 Z'),
    @('#9374c4', 'M134,136 L212,136 L212,204 Q212,216 200,216 L134,216 Z')
)
$frames = @()
foreach ($size in @(16,24,32,48,64,128,256)) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $context.PushTransform([Windows.Media.ScaleTransform]::new($size / 256.0, $size / 256.0))
    foreach ($drawing in $drawings) {
        $brush = [Windows.Media.BrushConverter]::new().ConvertFromString($drawing[0])
        $context.DrawGeometry($brush, $null, [Windows.Media.Geometry]::Parse($drawing[1]))
    }
    $context.Pop()
    $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    $encoder.Save($stream)
    $frames += [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose()
}
$iconPath = Join-Path $repo 'assets\diskpeek.ico'
[IO.Directory]::CreateDirectory((Split-Path -Parent $iconPath)) | Out-Null
$writer = [IO.BinaryWriter]::new([IO.File]::Create($iconPath))
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
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose() }
Get-Item -LiteralPath $iconPath | Select-Object Name,Length
