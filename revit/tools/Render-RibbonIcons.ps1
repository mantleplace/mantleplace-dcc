<#
.SYNOPSIS
    Renders the Revit ribbon's command glyphs from their committed SVG sources.

.DESCRIPTION
    Writes <Command><Theme>_<size>.png into the add-in's Resources folder, for every command, both
    Revit UI themes and both ribbon slots -- sixteen files from four sources. The PNGs are committed;
    this script is how they are REGENERATED, not a build step. Nothing in the build, the packaging
    script or CI runs it.

    THE MARK IS NOT THIS SCRIPT'S JOB. MantlePlaceMark_*.png beside the output of this script is
    rendered by tools/brand-assets from a private vector source that is not in this repository and
    cannot be, which is exactly why the two are separate scripts rather than one. See
    src/README.md beside the sources, and ADR 0009.

    Why a renderer at all, rather than committing PNGs somebody drew: the binaries rule in the root
    CLAUDE.md protects a stranger's first clone, and the only honest way to add sixteen binaries to a
    repository that counts them is for every one to be reproducible from a text source that is
    committed beside it. The SVGs are the source; this is the method; the PNGs are the output.

    Output is not guaranteed byte-identical across Windows releases -- WPF's rasteriser is the
    platform's, not ours. The provenance here is the method and this script, the same standing this
    repository already gives tools/brand-assets. Check `git diff --stat` before committing.

.PARAMETER OutputDirectory
    Where the PNGs are written. Defaults to the add-in's Resources folder, which is where the
    Resource glob in MantlePlace.Revit.Addin.csproj picks them up. Unlike tools/brand-assets, whose
    input is private and whose path must therefore be typed every time, both halves of this script
    live in the repository, so a default is a fact rather than a guess about somebody's disk.

.PARAMETER SourceDirectory
    Where the SVGs are read from. Defaults to Resources/src beside the output.

.EXAMPLE
    ./revit/tools/Render-RibbonIcons.ps1   # from the repository root
#>
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

# ---------------------------------------------------------------------------------------------
# What is drawn. Everything above the rendering below is a decision; everything below it is
# mechanism. Same split as tools/brand-assets (mark_geometry.py beside render_mark.py) and the same
# reason -- the arguable part should be readable without reading any WPF.
# ---------------------------------------------------------------------------------------------

# One glyph per command, from the MIT-licensed Tabler icon set. `Accent` holds the indices of the
# strokes drawn in the brand orange rather than the theme foreground, counted over the drawn paths
# of the source file in document order. Indices are stable because the source is COMMITTED: these
# point into src/*.svg in this repository, not into whatever Tabler ships today.
$glyphs = @(
    @{
        Stem   = 'Vault'
        Source = 'building-bank.svg'
        Accent = @()
        # The vault is the remote library, and a bank is the one building everyone reads as "what
        # is kept here is yours and is locked up". Its strokes are all axis-aligned, which is what
        # survives a 16 px render.
    },
    @{
        Stem   = 'ImportBundle'
        Source = 'package-import.svg'
        Accent = @(4, 5)
        # The panel's primary action, and the only glyph in the set with a separable action
        # element: a box, plus an arrow going into it. The arrow is the accent and the box is not,
        # so the ribbon spends one orange on one element of one button.
    },
    @{
        Stem   = 'ProbeTerrain'
        Source = 'ruler-measure.svg'
        Accent = @()
        # The label already says Terrain, so the glyph carries the verb. A ruler is the tool that
        # measures without moving anything, which is the whole of what this command promises.
    },
    @{
        Stem   = 'OpenLogs'
        Source = 'logs.svg'
        Accent = @()
        # Tabler's own name for it. Lines of record, and at 16 px it is the only glyph in the set
        # that is nothing but axis-aligned rules -- unmistakable next to the other three.
    }
)

# Revit's two UI themes, as foreground greys.
#
# Neither is pure black or pure white. Autodesk's ribbon glyphs are not either: full black on the
# light ribbon reads heavier than every Autodesk button beside ours, and full white on the dark
# ribbon blooms. These are the near-black and near-white that sit at the same weight as the tab
# next door, which is the whole ambition -- the plugin should look like it came with Revit.
#
# Written as PowerShell hex literals rather than "#RRGGBB" strings. A six-digit hex triplet in a
# tracked file is indistinguishable from a tracker citation to `ci-public-hygiene`, which refuses
# one and is right to: in Markdown a bare number auto-links to an unrelated issue in this public
# repository. The gate exempts the CSS spelling only, and this is not CSS.
$themes = @(
    @{ Name = 'Light'; Foreground = 0x3C3C3C },
    @{ Name = 'Dark'; Foreground = 0xE6E6E6 }
)

# The brand orange is the mark's own tile colour, unmodified, in BOTH themes: a second orange mixed
# for legibility on one of them would be a second brand colour, which is the thing this repository
# does not do. The value is the one tools/brand-assets/mark_geometry.py mattes the monogram out of,
# and the one the reference host names MantlePlacePalette::Mantle() in
# unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlacePalette.h. Two hosts, one spelling
# -- and if a Revit-side constant for it is ever introduced, this literal is what it replaces.
$accentColour = 0xFF7110

# The two slots a Revit ribbon button has: Image at 16 px and LargeImage at 32 px. The mark ships in
# five sizes because a private renderer can afford to; these ship in the two the ribbon asks for, and
# MantlePlace.Revit.Core.RibbonGlyphs is what decides which of the two a scaled display is handed.
$sizes = @(16, 32)

# Tabler draws on a 24 unit grid with a 2 unit stroke, and that ratio is kept rather than rounded to
# whole pixels at each size. A hand-tuned thickness per size would be a second design, maintained by
# hand, drifting from the source it claims to render -- and the sizes that would benefit most (16 px)
# are the ones where the difference is least visible under antialiasing.
$sourceGrid = 24.0
$sourceStroke = 2.0

# ---------------------------------------------------------------------------------------------
# Mechanism.
# ---------------------------------------------------------------------------------------------

function Get-GlyphStroke {
    <#
        .SYNOPSIS
            The path data of one Tabler SVG, in document order, drawn paths only.

        .DESCRIPTION
            Every Tabler outline icon opens with <path stroke="none" d="M0 0h24v24H0z" fill="none"/>,
            which is the 24x24 bounding box and not a stroke. Dropping it by its stroke="none" rather
            than by its position is deliberate: the position is a convention of the generator, the
            attribute is what the file says.
    #>
    param([string]$Path)

    [xml]$svg = Get-Content -Raw -LiteralPath $Path
    # GetAttribute rather than the property shortcut: Set-StrictMode turns a missing attribute into
    # a terminating error, and "has no stroke attribute" is the normal case for every drawn path.
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
    # Tabler's own stroke-linecap and stroke-linejoin. Not a style choice here: a butt cap on a
    # two-unit stroke at 16 px loses the ends of every short rule in logs.svg.
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

    # 96 DPI, because the size asked for is the size in pixels. A ribbon slot is measured in logical
    # pixels and the file is chosen to match the display scale (RibbonGlyphs), so a DPI claim baked
    # into the file would be a second, contradictory answer to the same question.
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
