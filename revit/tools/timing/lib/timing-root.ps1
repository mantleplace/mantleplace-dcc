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
