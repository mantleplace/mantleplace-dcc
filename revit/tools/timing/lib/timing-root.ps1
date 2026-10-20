# Where the timing harness keeps everything it makes: exported sources, builds, run folders, checkpoints and the
# pid records that keep runs apart. Dot-sourced by the three scripts beside this folder.
#
# The scripts live in the repository; nothing they make does. The work root is, in order: the -WorkRoot parameter,
# the MANTLEPLACE_TIMING_ROOT environment variable, or %LOCALAPPDATA%\MantlePlaceTiming. It is refused when it lies
# inside the repository the scripts belong to: a run folder holds a copy of a bundle zip (hundreds of MB), and this
# tree may be mounted inside a consuming project whose tools scan it.
function Resolve-TimingRoot([string]$WorkRoot) {
    $chosen = if ($WorkRoot) { $WorkRoot }
        elseif ($env:MANTLEPLACE_TIMING_ROOT) { $env:MANTLEPLACE_TIMING_ROOT }
        else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MantlePlaceTiming' }
    $full = [IO.Path]::GetFullPath($chosen, (Get-Location).ProviderPath).TrimEnd('\')
    # lib -> timing -> tools -> revit -> the repository root
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..')).TrimEnd('\')
    if ($full -ieq $repo -or $full.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "the work root $full is inside the repository ($repo). Point -WorkRoot or MANTLEPLACE_TIMING_ROOT outside it."
    }
    New-Item -ItemType Directory -Force $full | Out-Null
    $full
}

# The driver's preflight, run before anything is built or launched. A launched Revit finds the harness through
# the work root, so a root the run only fell back to (the %LOCALAPPDATA% default) or one that cannot be written
# costs a failed live run that is slow to diagnose. Refuses, naming MANTLEPLACE_TIMING_ROOT, when neither
# -WorkRoot nor the variable is set, when the root's drive does not exist, or when a folder cannot be made there.
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
