<#
.SYNOPSIS
    Builds and packages the Mantle Place Revit add-in as a release zip.

.DESCRIPTION
    Produces MantlePlace-Revit-<version>.zip: a Contents folder holding everything Revit loads, with
    README.txt, Install.cmd and Deploy-MantlePlaceRevit.ps1 above it.

    Contents is not an arbitrary name. It is the folder name Autodesk's Application Plugin Bundle
    format uses, so if this ever ships as a .bundle for the Autodesk App Store, that is adding a
    PackageContents.xml rather than rearranging the archive.

    THIS CANNOT RUN IN CI, and no workflow should ever pretend otherwise. Building the add-in shim
    needs RevitAPI.dll from a licensed Revit 2025 install, which is neither vendored nor
    redistributable, and this repository must never carry a self-hosted runner (a fork's pull request
    would execute on the build machine). A release is built on the same private licensed machine that
    runs the Unreal engine-compile gate.

    Packaging is not the release gate. Producing this zip proves the plugin COMPILES. What proves it
    works is loading the same output in Revit 2025, 2026 and 2027 and completing a real import in
    each -- see revit/README.md, "Before a release". The 2027 leg is the one that matters most: it is
    the only one where a .NET 8 assembly is loaded by a .NET 10 runtime.

.PARAMETER Configuration
    Release by default, and there is rarely a reason to change it. Debug is accepted so a dry run of
    the packaging itself does not need a Release build first.

.PARAMETER OutputDirectory
    Where the zip is written. Defaults to revit/dist, which is git-ignored.

.PARAMETER SkipBuild
    Package whatever is already in bin/. The version check below still runs, so a stale build is
    caught rather than shipped.

.EXAMPLE
    ./Package-MantlePlaceRevit.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutputDirectory,

    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$shimFileName = 'MantlePlace.Revit.Addin.dll'
$addinFileName = 'MantlePlace.addin'

$revitRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $revitRoot 'MantlePlace.Revit.slnx'
$addinProject = Join-Path $revitRoot 'src/MantlePlace.Revit.Addin'
$buildOutput = Join-Path $addinProject "bin/$Configuration/net8.0-windows"
$packagingDir = Join-Path $revitRoot 'packaging'
$propsFile = Join-Path $revitRoot 'Directory.Build.props'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $revitRoot 'dist'
}

if (-not $SkipBuild) {
    Write-Host "Building $Configuration ..." -ForegroundColor Cyan
    & dotnet build $solution -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE. Nothing was packaged."
    }
}

if (-not (Test-Path -LiteralPath $buildOutput)) {
    throw "No build output at $buildOutput. Run without -SkipBuild first."
}

# ---------------------------------------------------------------------------
# The version has one home (revit/Directory.Build.props) and this is where that claim is checked.
#
# Comparing the declared version against the COMPILED one catches both failures worth catching: the
# props edit that never made it into a build (packaging a stale bin/), and the <Version> element
# going missing, which is not blank but MSBuild's default 1.0.0 -- a confident claim to be a 1.0,
# and how every Revit assembly in this tree's history described itself before that element existed.
# ---------------------------------------------------------------------------
[xml]$props = Get-Content -LiteralPath $propsFile -Raw
$versionNode = $props.SelectSingleNode('//PropertyGroup/Version')
if ($null -eq $versionNode) {
    throw "No <Version> in $propsFile. That element is the plugin's version and its only home."
}
$declaredVersion = $versionNode.InnerText.Trim()

$shimPath = Join-Path $buildOutput $shimFileName
if (-not (Test-Path -LiteralPath $shimPath)) {
    throw "$shimFileName is missing from $buildOutput."
}

$productVersion = (Get-Item -LiteralPath $shimPath).VersionInfo.ProductVersion

# The SDK appends '+<commit sha>' as semver build metadata, so ProductVersion reads
# '0.1.0+a36fcd8...'. That sha is worth having -- it is what lets a log name the exact source it was
# built from -- but it is not part of the version, and comparing it here would fail on every build.
$builtVersion = $productVersion
$metadata = $builtVersion.IndexOf('+')
if ($metadata -ge 0) {
    $builtVersion = $builtVersion.Substring(0, $metadata)
}

if ($builtVersion -ne $declaredVersion) {
    throw "$shimFileName reports $builtVersion but $propsFile declares $declaredVersion. The build is stale -- rebuild without -SkipBuild."
}

# .pdb travels on purpose: a stack trace out of a curator's Revit is worth far more than the handful
# of kilobytes, and this add-in has no telemetry to fall back on.
$payload = @(Get-ChildItem -LiteralPath $buildOutput -File | Where-Object { $_.Extension -in '.dll', '.pdb', '.json' })
if ($payload.Count -eq 0) {
    throw "No assemblies in $buildOutput."
}

$topLevel = @(
    (Join-Path $packagingDir 'README.txt'),
    (Join-Path $packagingDir 'Install.cmd'),
    (Join-Path $PSScriptRoot 'Deploy-MantlePlaceRevit.ps1')
)
foreach ($file in $topLevel) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "Missing $file. The package is incomplete without it."
    }
}

$manifest = Join-Path $addinProject $addinFileName
if (-not (Test-Path -LiteralPath $manifest)) {
    throw "Add-in manifest not found at $manifest."
}

$zipName = "MantlePlace-Revit-$declaredVersion.zip"
$zipPath = Join-Path $OutputDirectory $zipName
$staging = Join-Path $OutputDirectory "staging-$declaredVersion"

if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
New-Item -ItemType Directory -Path $staging -Force | Out-Null

$contents = Join-Path $staging 'Contents'
New-Item -ItemType Directory -Path $contents -Force | Out-Null

Copy-Item -LiteralPath $manifest -Destination $contents
foreach ($file in $payload) {
    Copy-Item -LiteralPath $file.FullName -Destination $contents
}
foreach ($file in $topLevel) {
    Copy-Item -LiteralPath $file -Destination $staging
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath
Remove-Item -LiteralPath $staging -Recurse -Force

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $zipPath).Length

Write-Host ''
Write-Host "Packaged $zipName" -ForegroundColor Green
Write-Host "  $zipPath"
Write-Host "  $size bytes"
Write-Host "  sha256 $hash"
Write-Host "  built from $productVersion"
Write-Host ''
Write-Host 'Not released yet. Before publishing:' -ForegroundColor Yellow
Write-Host '  - install THIS zip into Revit 2025, 2026 and 2027, confirm the ribbon, complete one'
Write-Host '    real import in each (MANTLEPLACE_BUNDLE_ZIP skips the file picker, so it can be driven'
Write-Host '    unattended). 2027 is the leg that matters: .NET 8 assembly, .NET 10 runtime.'
Write-Host "  - tag revit-$declaredVersion  (no leading v; see docs/adr/0001-per-host-release-tracks.md)"
Write-Host '  - state the source commit, the sha256 above, and what was and was not verified.'
