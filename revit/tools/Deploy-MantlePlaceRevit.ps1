<#
.SYNOPSIS
    Builds the Revit add-in and installs it into the per-user Revit add-ins folders.

.DESCRIPTION
    Replaces the hand-copy procedure that used to live in README.md. Copying by hand is how a
    machine ends up running a plugin months older than the source tree: the symptom is a bug that
    was already fixed, reported against code that already has the fix, and the only tell is a file
    timestamp nobody thinks to check. This script makes the deployed bits a function of the source
    tree, and prints what it wrote so the timestamp is on screen rather than buried in AppData.

    One build serves Revit 2025, 2026 and 2027: the shim is a single net8.0-windows assembly
    compiled against the OLDEST supported API (2025). Do not "upgrade" RevitApiDir to a newer
    install -- that silently drops the older hosts (CS1705 explains why, in revit/CLAUDE.md).

.PARAMETER Configuration
    Debug (default) or Release.

.PARAMETER RevitVersions
    Which per-user add-in folders to install into. Defaults to every supported host. A version
    whose folder does not exist is created -- Revit reads the folder whether or not it pre-exists.

.PARAMETER SkipBuild
    Install whatever is already in bin/ without rebuilding.

.EXAMPLE
    ./Deploy-MantlePlaceRevit.ps1
    ./Deploy-MantlePlaceRevit.ps1 -Configuration Release -RevitVersions 2025
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string[]]$RevitVersions = @('2025', '2026', '2027'),

    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$revitRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $revitRoot 'MantlePlace.Revit.slnx'
$addinProject = Join-Path $revitRoot 'src/MantlePlace.Revit.Addin'
$output = Join-Path $addinProject "bin/$Configuration/net8.0-windows"

if (-not $SkipBuild) {
    Write-Host "Building $Configuration ..." -ForegroundColor Cyan
    # Building the solution rather than the shim alone: the shim is the only project CI never
    # compiles, so a local build is the ONLY thing that catches a break in it.
    & dotnet build $solution -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE. Nothing was deployed."
    }
}

if (-not (Test-Path $output)) {
    throw "No build output at $output. Run without -SkipBuild first."
}

# The manifest lives beside the sources, not in bin: it is content, not a build artifact, and it
# names the assembly WITHOUT a path so Revit resolves it beside the manifest it was loaded from.
$manifest = Join-Path $addinProject 'MantlePlace.addin'
if (-not (Test-Path $manifest)) {
    throw "Add-in manifest not found at $manifest."
}

# .pdb travels on purpose: a stack trace out of a curator's Revit is worth far more than the
# handful of kilobytes, and this add-in has no telemetry to fall back on.
$payload = @(Get-ChildItem -Path $output -File | Where-Object { $_.Extension -in '.dll', '.pdb', '.json' })
if ($payload.Count -eq 0) {
    throw "Build output at $output contains no assemblies."
}

$addinsRoot = Join-Path $env:APPDATA 'Autodesk/Revit/Addins'

foreach ($version in $RevitVersions) {
    $destination = Join-Path $addinsRoot $version
    if (-not (Test-Path $destination)) {
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
    }

    Copy-Item -Path $manifest -Destination $destination -Force
    foreach ($file in $payload) {
        Copy-Item -Path $file.FullName -Destination $destination -Force
    }

    $stamp = (Get-Item (Join-Path $destination 'MantlePlace.Revit.Addin.dll')).LastWriteTime
    Write-Host "Installed to $destination" -ForegroundColor Green
    Write-Host "  MantlePlace.Revit.Addin.dll  $stamp  ($($payload.Count) files + manifest)"
}

Write-Host ''
Write-Host 'Restart Revit to load the new build. Revit reads the add-ins folder only at startup.' -ForegroundColor Yellow
