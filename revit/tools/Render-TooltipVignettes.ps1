<#
.SYNOPSIS
    Renders the Revit ribbon's extended-tooltip vignettes.

.DESCRIPTION
    Writes <Command>Vignette<Theme>.png into the add-in's Resources folder -- four files, two
    commands, two Revit UI themes, one size each. The PNGs are committed; this script is how they are
    REGENERATED, not a build step. Nothing in the build, the packaging script or CI runs it.

    A VIGNETTE IS NOT A GLYPH, which is why this is not Render-RibbonIcons.ps1. That script's contract
    is exact and clean -- every <Command><Theme>_<size>.png from the SVGs in Resources/src, sixteen
    files from four sources, all of them one Tabler icon scaled -- and a vignette breaks all of it: a
    different name shape, no size in the name because there is no ladder to choose from, a canvas of
    355x266 rather than a square, and one of the two pictures with no SVG source at all. Folding them
    in would turn that synopsis into a list of exceptions. Note the reason is NOT the reason
    tools/brand-assets is separate -- that one's input is private. Both halves of this are here.

    REVIT CAPS A TOOLTIP IMAGE AT 355 PIXELS ON ITS LONGEST SIDE, and enforces it silently: hand it
    more and the picture is clipped or dropped with no error, on a surface that only appears after a
    hover delay. The canvas below is the largest 4:3 that fits. The authority for that number is
    MantlePlace.Revit.Core.Vignettes.MaxPixels, where the headless suite asserts it against these
    files' own PNG headers -- that assertion is the whole agreement between this script and the
    plugin, which is why no literal here is read from there. Change one and the other fails.

    WHY DRAWN AND NOT PHOTOGRAPHED: docs/adr/0010-tooltip-vignettes-are-drawn-not-photographed.md.

    Output is not guaranteed byte-identical across Windows releases -- WPF's rasteriser is the
    platform's, not ours, exactly as for Render-RibbonIcons.ps1 and tools/brand-assets. Check
    `git diff --stat` before committing.

.PARAMETER OutputDirectory
    Where the PNGs are written. Defaults to the add-in's Resources folder, which is where the Resource
    glob in MantlePlace.Revit.Addin.csproj picks them up.

.PARAMETER SourceDirectory
    Where the SVGs are read from. Defaults to Resources/src beside the output.

.EXAMPLE
    ./revit/tools/Render-TooltipVignettes.ps1   # from the repository root
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
# What is drawn. Everything above the mechanism below is a decision; everything below it is
# machinery. Same split as Render-RibbonIcons.ps1 and tools/brand-assets, and the same reason --
# the arguable part should be readable without reading any WPF.
# ---------------------------------------------------------------------------------------------

# The canvas, in pixels, at 96 DPI. 355 is Revit's cap on the longest side and 266 is the 4:3 height
# under it: the constraint at this size is drawing area rather than shape, and 4:3 is a third more
# canvas than a 16:9 picture of the same width. Mirrored by Vignettes.Width and Vignettes.Height.
$canvasWidth = 355
$canvasHeight = 266

# Revit's two UI themes, as foreground greys -- the same two Render-RibbonIcons.ps1 uses, for the
# same reason: the extended tooltip panel follows the UI theme, so a near-black drawing is not there
# on the dark one. Written as PowerShell hex literals rather than six-digit strings, because a hex
# triplet with a leading hash in a tracked file is indistinguishable from a tracker citation to
# ci-public-hygiene, which refuses one and is right to.
$themes = @(
    @{ Name = 'Light'; Foreground = 0x3C3C3C },
    @{ Name = 'Dark'; Foreground = 0xE6E6E6 }
)

# The mark's own tile colour, unmodified, in both themes -- the one accent this plugin spends, and
# the same literal Render-RibbonIcons.ps1 carries.
$accentColour = 0xFF7110

# ONE PEN FOR BOTH PICTURES, and its width is not a taste. Tabler draws on a 24 unit grid with a
# 2 unit stroke; every Tabler icon placed below is placed at 96 px, so the ratio fixes the stroke at
# 96 * 2 / 24 = 8. The computed surface is drawn with that same pen, which is what keeps a picture
# made by arranging icons and a picture made by projecting arithmetic reading as one family rather
# than as two products.
$elementBox = 96.0
$sourceGrid = 24.0
$sourceStroke = 2.0
$strokeWidth = $elementBox * $sourceStroke / $sourceGrid

# Every centred thing below is derived rather than typed. The canvas is one number; a literal 177.5
# repeated across three tables is that number copied three times, and a canvas edit would decentre
# the pictures without changing a line that mentions centring.
$canvasCentreX = $canvasWidth / 2.0
$iconCentreX = $canvasCentreX - ($elementBox / 2.0)

# The gap between the three bundles under the vault. Their pitch is one icon plus one gap, which is
# what keeps them evenly spread whatever the element box becomes.
$bundleGap = 14.0
$bundleStep = $elementBox + $bundleGap

# --- Vault: the vault, and the bundles in it, with the one you pick lifted clear -----------------
#
# A vault above and the bundles it holds below -- two things, because a third would not read at
# 355 px and the journey from vault to bundle to model needs three. The bank is the vault, and it is
# the same source the Vault ribbon glyph uses, so the button and its picture are visibly one thing.
# The middle bundle is raised clear of its neighbours and is the picture's single orange element,
# which is the whole sentence: THESE ARE YOURS, YOU PICK ONE.
#
# The sign-in precondition is deliberately absent. It is already the last sentence of the button's
# long description, where words say it better than a drawing of a person would.
$vaultLayout = @{
    Bank    = @{ Source = 'building-bank.svg'; X = $iconCentreX; Y = 10.0 }
    Bundles = @(
        @{ Source = 'package.svg'; X = $iconCentreX - $bundleStep; Y = 144.0; Accent = $false },
        @{ Source = 'package.svg'; X = $iconCentreX; Y = 120.0; Accent = $true },
        @{ Source = 'package.svg'; X = $iconCentreX + $bundleStep; Y = 144.0; Accent = $false }
    )
}

# --- Import Bundle: a zip on this disk becoming one toposolid ------------------------------------
#
# A package above, an arrow down, and a solid below. The arrow is the accent: it is the action, it is
# the one separable element, and it is the same decision the ImportBundle glyph already makes.
#
# ONLY THE TOPOSOLID IS DRAWN, and that is a promise being kept rather than a picture being kept
# simple. An import can also produce site boundaries, road centrelines, vegetation and an imagery
# drape -- every one of them conditional on what the bundle happens to carry (ImportStepKinds). A
# picture with trees in it promises trees. The toposolid is the one thing every bundle import
# produces, and the drape could not survive a two-colour line drawing anyway.
$importLayout = @{
    Package   = @{ Source = 'package.svg'; X = $iconCentreX; Y = 0.0 }

    # The arrow's own centre, rather than a reach into the solid's table. It is the same number
    # because all three parts of this picture are stacked on the canvas centre, not because the
    # arrow is a property of the surface.
    ArrowX    = $canvasCentreX
    ArrowTop  = 96.0
    ArrowFoot = 122.0
    ArrowHalf = 11.5
    ArrowBarb = 11.0
}

# The surface is COMPUTED, not drawn: an analytic height field sampled on a grid and projected
# isometrically. Nothing here was chosen by eye except the numbers, which is the point -- a drawing
# of terrain would be artwork somebody has to maintain, and this is arithmetic that regenerates.
#
# The base sits at a CONSTANT depth below the projection plane while the top rises and falls with the
# field, which is exactly what makes the result a toposolid rather than a surface: one solid, thick
# under a rise and thin under a hollow. A curator arriving from Revit's own Toposolid tool recognises
# that silhouette, and recognising it is the entire job of this picture.
$surface = @{
    CentreX    = $canvasCentreX  # so the solid sits under the package and the arrow
    CentreY    = 137.0   # chosen so the whole solid lands between the arrow's foot and the margin
    HalfWidth  = 132.0   # half the diamond's width; leaves a 45 px margin either side
    HalfHeight = 44.0    # the isometric squash: a unit step in u or v is this many pixels down.
                         # 132 over 44 is 3:1 rather than the 5:1 a wider, flatter diamond gives --
                         # below about 3:1 the projection stops reading as a solid seen from above
                         # and starts reading as a flat plate with a pattern printed on it
    Relief     = 26.0    # pixels per unit of height field, which runs about -0.85 to +0.85
    BaseDrop   = 26.0    # the flat base, below the projection plane. Thin under a hollow, never zero
    Divisions  = 3       # three divisions, so four grid lines each way and eight polylines. THE PEN
                         # SETS THIS, not taste: at four divisions the cells are 22 px tall, an 8 px
                         # stroke eats most of that, and the surface reads as a lattice, not ground
    Samples    = 24      # points per polyline, so a grid line reads as a curve and not a fold
}

# ---------------------------------------------------------------------------------------------
# Mechanism.
# ---------------------------------------------------------------------------------------------

function Get-IconStroke {
    <#
        .SYNOPSIS
            The path data of one Tabler SVG, in document order, drawn paths only.

        .DESCRIPTION
            Identical in intent to Render-RibbonIcons.ps1's Get-GlyphStroke, and duplicated rather
            than shared -- as are ConvertTo-MediaColour and the pen builder below it, three copies in
            all. A module imported by two callers is a third file to keep true, and these are forty
            lines between them whose contracts are a published file format and two WPF types.

            Every Tabler outline icon opens with a path whose stroke is none and whose data is the
            24x24 bounding box. It is dropped by that attribute rather than by its position: the
            position is a convention of the generator, the attribute is what the file says.
    #>
    param([string]$Path)

    [xml]$svg = Get-Content -Raw -LiteralPath $Path
    # GetAttribute rather than the property shortcut: Set-StrictMode turns a missing attribute into a
    # terminating error, and having no stroke attribute is the normal case for every drawn path.
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
    # Tabler's own linecap and linejoin, kept for the computed geometry too. A butt cap on a grid of
    # open polylines leaves a visible square notch at every end.
    $pen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $pen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $pen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
    $pen.Freeze()

    return $pen
}

function New-Stroke {
    <#
        .SYNOPSIS
            One stroke of a vignette: its geometry, and whether it takes the accent.

        .DESCRIPTION
            Parsed here rather than at draw time so that a computed stroke and a placed icon are the
            same shape. They are drawn by one loop, twice over -- once per theme -- and a loop that
            has to ask which of two shapes it is holding is a branch that exists only because two
            producers disagreed.
    #>
    param([string]$Data, [bool]$Accent = $false)

    return @{ Geometry = [System.Windows.Media.Geometry]::Parse($Data); Accent = $Accent }
}

function Get-IconStrokes {
    <#
        .SYNOPSIS
            One Tabler icon placed on the canvas, as strokes in canvas coordinates.

        .DESCRIPTION
            The geometry is transformed rather than the drawing context, so that every stroke in the
            picture -- icon and computed alike -- is drawn by the same unscaled pen. A context scale
            would multiply the pen with it and give each icon its own weight.
    #>
    param([string]$SourcePath, [double]$X, [double]$Y, [bool]$Accent = $false)

    $scale = $elementBox / $sourceGrid
    $strokes = @()

    foreach ($data in Get-IconStroke -Path $SourcePath) {
        # Clone, because Parse hands back a frozen geometry and a transform cannot be set on one.
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
    <#
        .SYNOPSIS
            The height field, at (u, v) in the unit square. Runs about -0.85 to +0.85.

        .DESCRIPTION
            A broad rise across the middle, plus a shorter wave across it at an angle that is not a
            multiple of the grid -- so no grid line lands along a crest and the whole thing cannot
            read as a folded plate. Analytic and seedless: the same four numbers give the same terrain
            on every machine, forever, which is what lets the output be called reproducible.
    #>
    param([double]$U, [double]$V)

    return (0.55 * [Math]::Sin([Math]::PI * $U) * [Math]::Sin([Math]::PI * $V)) +
           (0.30 * [Math]::Sin((3.2 * $U) + 0.7) * [Math]::Cos((2.6 * $V) + 0.4))
}

function Get-SurfacePoint {
    <#
        .SYNOPSIS
            Where (u, v) lands on the canvas -- on the top surface, or on the flat base.
    #>
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
    <#
        .SYNOPSIS
            A run of points as SVG path data.

        .DESCRIPTION
            FORMATTED IN THE INVARIANT CULTURE, NOT THE MACHINE'S. PowerShell's -f operator uses the
            CURRENT culture; [Geometry]::Parse only ever reads the invariant one. On a machine set to
            a comma-decimal locale the default would turn 177.5 into a comma-separated pair, which
            Parse reads as two coordinates or refuses outright -- so the picture would come out wrong
            or not at all, on somebody else's machine, for a reason nothing else here mentions. The
            sibling glyph script never meets this because it formats no numbers into path data.
    #>
    param([object[]]$Points)

    $invariant = [System.Globalization.CultureInfo]::InvariantCulture
    $parts = foreach ($point in $Points) {
        [string]::Format($invariant, '{0:0.###},{1:0.###}', $point.X, $point.Y)
    }

    return 'M ' + ($parts -join ' L ')
}

function Get-ToposolidStrokes {
    <#
        .SYNOPSIS
            The computed solid: its top surface as a grid, and the two near faces that give it depth.

        .DESCRIPTION
            The grid alone would be a surface. What makes it a SOLID is the last part -- the flat base
            under the two near edges, and the three verticals that join top to base at the near
            corners. In this projection (0,0) is the far corner and (1,1) the near one, so u=1 and v=1
            are the two edges whose faces the viewer can see; the other two are hidden behind the
            surface and drawing them would be drawing through it.
    #>
    $strokes = @()
    $divisions = $surface.Divisions
    $samples = $surface.Samples

    # The top surface, as a grid: lines of constant u, then lines of constant v.
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

    # The base, under the two near edges. Straight in projection: the base plane is flat, so its
    # image is linear in u and in v.
    $strokes += New-Stroke -Data (ConvertTo-PolylineData -Points @(
        (Get-SurfacePoint -U 1.0 -V 0.0 -Base),
        (Get-SurfacePoint -U 1.0 -V 1.0 -Base),
        (Get-SurfacePoint -U 0.0 -V 1.0 -Base)))

    # The three near corners, top down to base -- the thickness itself.
    foreach ($corner in @(@(1.0, 0.0), @(1.0, 1.0), @(0.0, 1.0))) {
        $strokes += New-Stroke -Data (ConvertTo-PolylineData -Points @(
            (Get-SurfacePoint -U $corner[0] -V $corner[1]),
            (Get-SurfacePoint -U $corner[0] -V $corner[1] -Base)))
    }

    return $strokes
}

function Get-ArrowStrokes {
    <#
        .SYNOPSIS
            The descending arrow, in the accent colour: the action, and the one orange element.
    #>
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

    # 96 DPI, and the cap is why there is no choice about it. Revit lays a tooltip image out at its
    # device-independent size, so a 2x render with 192 DPI in its header would lay out correctly and
    # be 710 px on its long side -- twice the 355 the ribbon accepts. One file per theme, crisp at
    # 100%, softened by Revit at 200%, and present at every scale.
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
