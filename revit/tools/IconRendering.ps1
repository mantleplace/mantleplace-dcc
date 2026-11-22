function Get-IconStroke {
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

function New-IconPen {
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
