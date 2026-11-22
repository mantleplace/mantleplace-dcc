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

$canvasWidth = 355
$canvasHeight = 266

$themes = @(
    @{ Name = 'Light'; Foreground = 0x3C3C3C },
    @{ Name = 'Dark'; Foreground = 0xE6E6E6 }
)

$accentColour = 0xFF7110

$elementBox = 96.0
$sourceGrid = 24.0
$sourceStroke = 2.0
$strokeWidth = $elementBox * $sourceStroke / $sourceGrid

$canvasCentreX = $canvasWidth / 2.0
$iconCentreX = $canvasCentreX - ($elementBox / 2.0)

$bundleGap = 14.0
$bundleStep = $elementBox + $bundleGap

$vaultLayout = @{
    Bank    = @{ Source = 'building-bank.svg'; X = $iconCentreX; Y = 10.0 }
    Bundles = @(
        @{ Source = 'package.svg'; X = $iconCentreX - $bundleStep; Y = 144.0; Accent = $false },
        @{ Source = 'package.svg'; X = $iconCentreX; Y = 120.0; Accent = $true },
        @{ Source = 'package.svg'; X = $iconCentreX + $bundleStep; Y = 144.0; Accent = $false }
    )
}

$importLayout = @{
    Package   = @{ Source = 'package.svg'; X = $iconCentreX; Y = 0.0 }

    ArrowX    = $canvasCentreX
    ArrowTop  = 96.0
    ArrowFoot = 122.0
    ArrowHalf = 11.5
    ArrowBarb = 11.0
}

$surface = @{
    CentreX    = $canvasCentreX
    CentreY    = 137.0
    HalfWidth  = 132.0
    HalfHeight = 44.0
    Relief     = 26.0
    BaseDrop   = 26.0
    Divisions  = 3
    Samples    = 24
}

function Get-IconStroke {
    param([string]$Path)

    [xml]$svg = Get-Content -Raw -LiteralPath $Path
    $drawn = @(
        $svg.svg.path |
            Where-Object { $_.GetAttribute('stroke') -ne 'none' } |
            ForEach-Object { $_.GetAttribute('d') })

    if ($drawn.Count -eq 0) {
        throw "No drawable path in '$Path'. A Tabler outline icon has at least one path that is not stroke none."
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

function New-VignettePen {
    param([System.Windows.Media.Color]$Colour)

    $brush = [System.Windows.Media.SolidColorBrush]::new($Colour)
    $brush.Freeze()

    $pen = [System.Windows.Media.Pen]::new($brush, $strokeWidth)
    $pen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $pen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $pen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
    $pen.Freeze()

    return $pen
}

function New-Stroke {
    param([string]$Data, [bool]$Accent = $false)

    return @{ Geometry = [System.Windows.Media.Geometry]::Parse($Data); Accent = $Accent }
}

function Get-IconStrokes {
    param([string]$SourcePath, [double]$X, [double]$Y, [bool]$Accent = $false)

    $scale = $elementBox / $sourceGrid
    $strokes = @()

    foreach ($data in Get-IconStroke -Path $SourcePath) {
        $geometry = [System.Windows.Media.Geometry]::Parse($data).Clone()
        $group = [System.Windows.Media.TransformGroup]::new()
        $group.Children.Add([System.Windows.Media.ScaleTransform]::new($scale, $scale))
        $group.Children.Add([System.Windows.Media.TranslateTransform]::new($X, $Y))
        $geometry.Transform = $group
        $strokes += @{ Geometry = $geometry; Accent = $Accent }
    }

    return $strokes
}

function Get-SurfaceHeight {
    param([double]$U, [double]$V)

    return (0.55 * [Math]::Sin([Math]::PI * $U) * [Math]::Sin([Math]::PI * $V)) +
           (0.30 * [Math]::Sin((3.2 * $U) + 0.7) * [Math]::Cos((2.6 * $V) + 0.4))
}

function Get-SurfacePoint {
    param([double]$U, [double]$V, [switch]$Base)

    $x = $surface.CentreX + (($U - $V) * $surface.HalfWidth)
    $y = $surface.CentreY + (($U + $V) * $surface.HalfHeight)

    if ($Base) {
        $y += $surface.BaseDrop
    }
    else {
        $y -= (Get-SurfaceHeight -U $U -V $V) * $surface.Relief
    }

    return @{ X = $x; Y = $y }
}

function ConvertTo-PolylineData {
    param([object[]]$Points)

    $invariant = [System.Globalization.CultureInfo]::InvariantCulture
    $parts = foreach ($point in $Points) {
        [string]::Format($invariant, '{0:0.###},{1:0.###}', $point.X, $point.Y)
    }

    return 'M ' + ($parts -join ' L ')
}

function Get-ToposolidStrokes {
    $strokes = @()
    $divisions = $surface.Divisions
    $samples = $surface.Samples

    for ($i = 0; $i -le $divisions; $i++) {
        $fixed = $i / [double]$divisions

        foreach ($alongV in @($true, $false)) {
            $points = @()
            for ($s = 0; $s -le $samples; $s++) {
                $moving = $s / [double]$samples
                $u = if ($alongV) { $fixed } else { $moving }
                $v = if ($alongV) { $moving } else { $fixed }
                $points += Get-SurfacePoint -U $u -V $v
            }

            $strokes += New-Stroke -Data (ConvertTo-PolylineData -Points $points)
        }
    }

    $strokes += New-Stroke -Data (ConvertTo-PolylineData -Points @(
        (Get-SurfacePoint -U 1.0 -V 0.0 -Base),
        (Get-SurfacePoint -U 1.0 -V 1.0 -Base),
        (Get-SurfacePoint -U 0.0 -V 1.0 -Base)))

    foreach ($corner in @(@(1.0, 0.0), @(1.0, 1.0), @(0.0, 1.0))) {
        $strokes += New-Stroke -Data (ConvertTo-PolylineData -Points @(
            (Get-SurfacePoint -U $corner[0] -V $corner[1]),
            (Get-SurfacePoint -U $corner[0] -V $corner[1] -Base)))
    }

    return $strokes
}

function Get-ArrowStrokes {
    $x = $importLayout.ArrowX
    $top = $importLayout.ArrowTop
    $foot = $importLayout.ArrowFoot
    $barb = $importLayout.ArrowBarb

    return @(
        (New-Stroke -Data (ConvertTo-PolylineData -Points @(
            @{ X = $x; Y = $top }, @{ X = $x; Y = $foot })) -Accent $true),
        (New-Stroke -Data (ConvertTo-PolylineData -Points @(
            @{ X = $x - $importLayout.ArrowHalf; Y = $foot - $barb },
            @{ X = $x; Y = $foot },
            @{ X = $x + $importLayout.ArrowHalf; Y = $foot - $barb })) -Accent $true)
    )
}

function Get-VignetteStrokes {
    param([string]$Stem, [string]$SourceRoot)

    switch ($Stem) {
        'Vault' {
            $strokes = @()
            $strokes += Get-IconStrokes `
                -SourcePath (Join-Path $SourceRoot $vaultLayout.Bank.Source) `
                -X $vaultLayout.Bank.X -Y $vaultLayout.Bank.Y

            foreach ($bundle in $vaultLayout.Bundles) {
                $strokes += Get-IconStrokes `
                    -SourcePath (Join-Path $SourceRoot $bundle.Source) `
                    -X $bundle.X -Y $bundle.Y -Accent $bundle.Accent
            }

            return $strokes
        }

        'ImportBundle' {
            $strokes = @()
            $strokes += Get-IconStrokes `
                -SourcePath (Join-Path $SourceRoot $importLayout.Package.Source) `
                -X $importLayout.Package.X -Y $importLayout.Package.Y
            $strokes += Get-ArrowStrokes
            $strokes += Get-ToposolidStrokes

            return $strokes
        }

        default { throw "No vignette is composed for '$Stem'." }
    }
}

function Save-VignettePng {
    param(
        [object[]]$Strokes,
        [System.Windows.Media.Color]$Foreground,
        [System.Windows.Media.Color]$Accent,
        [string]$Destination
    )

    $foregroundPen = New-VignettePen -Colour $Foreground
    $accentPen = New-VignettePen -Colour $Accent

    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    try {
        foreach ($stroke in $Strokes) {
            $pen = if ($stroke.Accent) { $accentPen } else { $foregroundPen }
            $context.DrawGeometry($null, $pen, $stroke.Geometry)
        }
    }
    finally {
        $context.Close()
    }

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $canvasWidth, $canvasHeight, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
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

foreach ($required in @('building-bank.svg', 'package.svg')) {
    $path = Join-Path $SourceDirectory $required
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing vignette source '$path'. The sources are committed beside their licence; do not rely on a download."
    }
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$accent = ConvertTo-MediaColour $accentColour
$written = 0

foreach ($stem in @('Vault', 'ImportBundle')) {
    $strokes = Get-VignetteStrokes -Stem $stem -SourceRoot $SourceDirectory

    foreach ($theme in $themes) {
        $fileName = '{0}Vignette{1}.png' -f $stem, $theme.Name
        $destination = Join-Path $OutputDirectory $fileName

        Save-VignettePng `
            -Strokes $strokes `
            -Foreground (ConvertTo-MediaColour $theme.Foreground) `
            -Accent $accent `
            -Destination $destination

        Write-Verbose "wrote $fileName"
        $written++
    }
}

Write-Host "Rendered $written vignette PNGs into $OutputDirectory"
Write-Host "Check the diff before committing: git diff --stat"
