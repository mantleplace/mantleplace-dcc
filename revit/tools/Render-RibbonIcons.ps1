[CmdletBinding()]
param(
    [string]$OutputDirectory,

    [string]$SourceDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationCore, WindowsBase

$revitRoot = Split-Path -Parent $PSScriptRoot
$resources = Join-Path $revitRoot 'src/MantlePlace.Revit.Addin/Resources'

if (-not $OutputDirectory) { $OutputDirectory = $resources }
if (-not $SourceDirectory) { $SourceDirectory = Join-Path $resources 'src' }

$glyphs = @(
    @{
        Stem   = 'Vault'
        Source = 'building-bank.svg'
        Accent = @()
    },
    @{
        Stem   = 'ImportBundle'
        Source = 'package-import.svg'
        Accent = @(4, 5)
    },
    @{
        Stem   = 'ProbeTerrain'
        Source = 'ruler-measure.svg'
        Accent = @()
    },
    @{
        Stem   = 'OpenLogs'
        Source = 'logs.svg'
        Accent = @()
    },
    @{
        Stem   = 'PrepareForHandover'
        Source = 'package-export.svg'
        Accent = @()
    },
    @{
        Stem   = 'OpenInCurator'
        Source = 'map-pin.svg'
        Accent = @()
    }
)

$themes = @(
    @{ Name = 'Light'; Foreground = 0x3C3C3C },
    @{ Name = 'Dark'; Foreground = 0xE6E6E6 }
)

$accentColour = 0xFF7110

$sizes = @(16, 32)

$sourceGrid = 24.0
$sourceStroke = 2.0

function Get-GlyphStroke {
    param([string]$Path)

    [xml]$svg = Get-Content -Raw -LiteralPath $Path
    $drawn = @(
        $svg.svg.path |
            Where-Object { $_.GetAttribute('stroke') -ne 'none' } |
            ForEach-Object { $_.GetAttribute('d') })

    if ($drawn.Count -eq 0) {
        throw "No drawable path in '$Path'. A Tabler outline icon has at least one path without stroke=`"none`"."
    }

    return $drawn
}

function ConvertTo-MediaColour {
    param([int]$Rgb)

    return [System.Windows.Media.Color]::FromRgb(
        [byte](($Rgb -shr 16) -band 0xFF),
        [byte](($Rgb -shr 8) -band 0xFF),
        [byte]($Rgb -band 0xFF))
}

function New-GlyphPen {
    param([System.Windows.Media.Color]$Colour, [double]$Thickness)

    $brush = [System.Windows.Media.SolidColorBrush]::new($Colour)
    $brush.Freeze()

    $pen = [System.Windows.Media.Pen]::new($brush, $Thickness)
    $pen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $pen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $pen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
    $pen.Freeze()

    return $pen
}

function Save-GlyphPng {
    param(
        [string[]]$PathData,
        [int[]]$AccentIndices,
        [System.Windows.Media.Color]$Foreground,
        [System.Windows.Media.Color]$Accent,
        [int]$Size,
        [string]$Destination
    )

    $scale = $Size / $sourceGrid
    $foregroundPen = New-GlyphPen -Colour $Foreground -Thickness ($sourceStroke * $scale)
    $accentPen = New-GlyphPen -Colour $Accent -Thickness ($sourceStroke * $scale)

    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    try {
        $context.PushTransform([System.Windows.Media.ScaleTransform]::new($scale, $scale))
        for ($i = 0; $i -lt $PathData.Count; $i++) {
            $pen = if ($AccentIndices -contains $i) { $accentPen } else { $foregroundPen }
            $context.DrawGeometry($null, $pen, [System.Windows.Media.Geometry]::Parse($PathData[$i]))
        }
        $context.Pop()
    }
    finally {
        $context.Close()
    }

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)

    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))

    $stream = [System.IO.File]::Create($Destination)
    try {
        $encoder.Save($stream)
    }
    finally {
        $stream.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $SourceDirectory)) {
    throw "No SVG sources at '$SourceDirectory'."
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$accent = ConvertTo-MediaColour $accentColour
$written = 0

foreach ($glyph in $glyphs) {
    $sourcePath = Join-Path $SourceDirectory $glyph.Source
    if (-not (Test-Path -LiteralPath $sourcePath)) {
        throw "Missing glyph source '$sourcePath'. The sources are committed beside their licence; do not rely on a download."
    }

    $pathData = Get-GlyphStroke -Path $sourcePath

    foreach ($index in $glyph.Accent) {
        if ($index -lt 0 -or $index -ge $pathData.Count) {
            throw "Glyph '$($glyph.Stem)' accents stroke $index, but '$($glyph.Source)' draws $($pathData.Count)."
        }
    }

    foreach ($theme in $themes) {
        $foreground = ConvertTo-MediaColour $theme.Foreground

        foreach ($size in $sizes) {
            $fileName = "$($glyph.Stem)$($theme.Name)_$size.png"
            $destination = Join-Path $OutputDirectory $fileName

            Save-GlyphPng `
                -PathData $pathData `
                -AccentIndices $glyph.Accent `
                -Foreground $foreground `
                -Accent $accent `
                -Size $size `
                -Destination $destination

            Write-Verbose "wrote $fileName"
            $written++
        }
    }
}

Write-Host "Rendered $written glyph PNGs into $OutputDirectory"
Write-Host "Check the diff before committing: git diff --stat"
