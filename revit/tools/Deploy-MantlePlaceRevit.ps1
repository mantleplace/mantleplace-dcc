<#
.SYNOPSIS
    Installs the Mantle Place Revit add-in into the per-user Revit add-ins folders, from a source
    build or from an extracted release.

.DESCRIPTION
    Two callers, one script, on purpose. Maintainers run it against the source tree, where it builds
    first; a curator runs it against an extracted release zip with -PayloadDirectory, where there is
    nothing to build. Keeping them in one file is what gives the installer a curator runs any test
    coverage at all -- CI can never execute it, because CI can never build this add-in (no Revit on
    a hosted runner), so the only proof it works is that maintainers run the same file every day.

    Copying by hand is how a machine ends up running a plugin months older than the source tree: the
    symptom is a bug that was already fixed, reported against code that already has the fix, and the
    only tell is a file timestamp nobody thinks to check. This script makes the deployed bits a
    function of its input, and prints the version and timestamp of what it wrote.

    Note for a curator: hand-copying is a perfectly good way to install a RELEASE, and the zip's
    README leads with it. A release is a fixed version -- install 0.1.0 and it stays 0.1.0, with no
    source tree to drift from. The staleness hazard above is a maintainer problem, not yours; this
    script is here because one step beats three.

    One build serves Revit 2025, 2026 and 2027: the shim is a single net8.0-windows assembly
    compiled against the OLDEST supported API (2025). Do not "upgrade" RevitApiDir to a newer
    install -- that silently drops the older hosts (CS1705 explains why, in revit/CLAUDE.md).

    Windows PowerShell 5.1 compatible, deliberately. That is what is on a curator machine by
    default; PowerShell 7 is not. No ternaries, no null-coalescing, no pipeline chain operators --
    add one and the installer breaks for the people it exists for while working fine on every
    machine we test it on.

.PARAMETER PayloadDirectory
    Install the files already in this directory, without building. This is the release path: point
    it at the extracted zip's Contents folder. The add-in manifest is expected inside it. When this
    is given, -Configuration and -SkipBuild are unused and no part of the source tree is touched.

.PARAMETER Configuration
    Debug (default) or Release. Source-tree path only.

.PARAMETER RevitVersions
    Which per-user add-in folders to install into. Defaults to every supported host. A version
    whose folder does not exist is created -- Revit reads the folder whether or not it pre-exists.

.PARAMETER SkipBuild
    Install whatever is already in bin/ without rebuilding. Source-tree path only.

.EXAMPLE
    ./Deploy-MantlePlaceRevit.ps1
    ./Deploy-MantlePlaceRevit.ps1 -Configuration Release -RevitVersions 2025
    ./Deploy-MantlePlaceRevit.ps1 -PayloadDirectory .\Contents
#>
[CmdletBinding()]
param(
    [string]$PayloadDirectory,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string[]]$RevitVersions = @('2025', '2026', '2027'),

    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$addinFileName = 'MantlePlace.addin'
$shimFileName = 'MantlePlace.Revit.Addin.dll'
$fromRelease = -not [string]::IsNullOrWhiteSpace($PayloadDirectory)

# ---------------------------------------------------------------------------
# Refuse before writing anything if Revit is open.
#
# A loaded add-in DLL is file-locked, so a copy across three folders fails PART WAY THROUGH: some
# files replaced, some not, in whichever folders got there first. That leaves a machine running a
# MIXED build -- a strictly worse version of the stale-build failure this script exists to prevent,
# and silent in exactly the same way. Checking first is the only way the failure stays honest.
# ---------------------------------------------------------------------------
$running = @(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    throw "Revit is running, so its add-in files are locked and only some of them would be replaced. Close Revit and run this again. Nothing was installed."
}

if ($fromRelease) {
    if (-not (Test-Path -LiteralPath $PayloadDirectory)) {
        throw "No such directory: $PayloadDirectory. Point -PayloadDirectory at the Contents folder inside the extracted release zip."
    }

    # Resolved here and nowhere else: this branch must never reach for the parent of $PSScriptRoot,
    # because in a release the script sits in an extracted zip and that parent is a Downloads folder.
    $source = (Resolve-Path -LiteralPath $PayloadDirectory).Path
    $manifest = Join-Path $source $addinFileName
    if (-not (Test-Path -LiteralPath $manifest)) {
        throw "No $addinFileName in $source. That folder is not a Mantle Place payload."
    }
}
else {
    $revitRoot = Split-Path -Parent $PSScriptRoot
    $solution = Join-Path $revitRoot 'MantlePlace.Revit.slnx'
    $addinProject = Join-Path $revitRoot 'src/MantlePlace.Revit.Addin'
    $source = Join-Path $addinProject "bin/$Configuration/net8.0-windows"

    if (-not $SkipBuild) {
        Write-Host "Building $Configuration ..." -ForegroundColor Cyan
        # Building the solution rather than the shim alone: the shim is the only project CI never
        # compiles, so a local build is the ONLY thing that catches a break in it.
        & dotnet build $solution -c $Configuration
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed with exit code $LASTEXITCODE. Nothing was deployed."
        }
    }

    if (-not (Test-Path -LiteralPath $source)) {
        throw "No build output at $source. Run without -SkipBuild first."
    }

    # The manifest lives beside the sources, not in bin: it is content, not a build artifact, and it
    # names the assembly WITHOUT a path so Revit resolves it beside the manifest it was loaded from.
    $manifest = Join-Path $addinProject $addinFileName
    if (-not (Test-Path -LiteralPath $manifest)) {
        throw "Add-in manifest not found at $manifest."
    }
}

# .pdb travels on purpose: a stack trace out of a curator's Revit is worth far more than the
# handful of kilobytes, and this add-in has no telemetry to fall back on.
$payload = @(Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Extension -in '.dll', '.pdb', '.json' })
if ($payload.Count -eq 0) {
    throw "No assemblies in $source."
}

$shim = $payload | Where-Object { $_.Name -eq $shimFileName } | Select-Object -First 1
if ($null -eq $shim) {
    throw "$shimFileName is missing from $source. Without it Revit has nothing to load."
}

# The version the assembly declares, not one passed in: what is printed is what is being copied.
$version = $shim.VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($version)) {
    $version = 'unknown version'
}

$addinsRoot = Join-Path $env:APPDATA 'Autodesk/Revit/Addins'

foreach ($revitVersion in $RevitVersions) {
    $destination = Join-Path $addinsRoot $revitVersion
    if (-not (Test-Path -LiteralPath $destination)) {
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
    }

    Copy-Item -LiteralPath $manifest -Destination $destination -Force
    foreach ($file in $payload) {
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }

    $stamp = (Get-Item -LiteralPath (Join-Path $destination $shimFileName)).LastWriteTime
    Write-Host "Installed to $destination" -ForegroundColor Green
    Write-Host "  Mantle Place $version  $stamp  ($($payload.Count) files + manifest)"
}

Write-Host ''
Write-Host 'Restart Revit to load the new build. Revit reads the add-ins folder only at startup.' -ForegroundColor Yellow
