function Resolve-TimingRoot([string]$WorkRoot) {
    $chosen = if ($WorkRoot) { $WorkRoot }
        elseif ($env:MANTLEPLACE_TIMING_ROOT) { $env:MANTLEPLACE_TIMING_ROOT }
        else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MantlePlaceTiming' }
    $full = [IO.Path]::GetFullPath($chosen, (Get-Location).ProviderPath).TrimEnd('\')
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..')).TrimEnd('\')
    if ($full -ieq $repo -or $full.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "the work root $full is inside the repository ($repo). Point -WorkRoot or MANTLEPLACE_TIMING_ROOT outside it."
    }
    New-Item -ItemType Directory -Force $full | Out-Null
    $full
}

function Assert-TimingRootReachable([string]$WorkRoot) {
    $chosen = if ($WorkRoot) { $WorkRoot } else { $env:MANTLEPLACE_TIMING_ROOT }
    if (-not $chosen) {
        throw 'MANTLEPLACE_TIMING_ROOT is not set and no -WorkRoot was given. Set MANTLEPLACE_TIMING_ROOT (or pass -WorkRoot) to a folder outside the repository that a launched Revit can reach; the %LOCALAPPDATA% default is not used for a run.'
    }
    $full = [IO.Path]::GetFullPath($chosen, (Get-Location).ProviderPath)
    $driveRoot = [IO.Path]::GetPathRoot($full)
    if (-not $driveRoot -or -not (Test-Path -LiteralPath $driveRoot)) {
        throw "the work root $full is unreachable: its drive ($driveRoot) does not exist. Point MANTLEPLACE_TIMING_ROOT (or -WorkRoot) at a folder that does."
    }
    try { New-Item -ItemType Directory -Force $full -ErrorAction Stop | Out-Null }
    catch { throw "the work root $full is unreachable: a folder cannot be made there ($($_.Exception.Message)). Point MANTLEPLACE_TIMING_ROOT (or -WorkRoot) at a folder that can be written." }
}
