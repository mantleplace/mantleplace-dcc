<#
.SYNOPSIS
    Builds and packages the Mantle Place Revit plugin as a release zip.

.DESCRIPTION
    Produces MantlePlace-Revit-<version>.zip. Its one root folder is MantlePlace.bundle, an Autodesk
    Application Plugin Bundle (ADR 0018), holding PackageContents.xml and a Contents folder with
    everything Revit loads, with README.txt, Install.cmd, Uninstall.cmd, Deploy-MantlePlaceRevit.ps1
    and Uninstall-MantlePlaceRevit.ps1 beside them. The same archive therefore installs from the
    Autodesk storefront and by hand: the copy-first install in README.txt reads Contents exactly as
    before.

    PackageContents.xml is built by a pure function in MantlePlace.Revit.Core
    (AutodeskBundleManifest), reached through tools/WriteBundleManifest, from the version the build
    was compiled with. This script fails when that version differs from the one
    Directory.Build.props declares, the same stale-build check the shim gets.

    THIS CANNOT RUN IN CI, and no workflow should ever pretend otherwise. Building the plugin shim
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
$manifestWriterProject = Join-Path $PSScriptRoot 'WriteBundleManifest/WriteBundleManifest.csproj'
$bundleFolderName = 'MantlePlace.bundle'
$bundleManifestFileName = 'PackageContents.xml'
$iconSource = Join-Path $addinProject 'Resources/MantlePlaceMark_48.png'
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
# of kilobytes, and this plugin has no telemetry to fall back on.
$payload = @(Get-ChildItem -LiteralPath $buildOutput -File | Where-Object { $_.Extension -in '.dll', '.pdb', '.json' })
if ($payload.Count -eq 0) {
    throw "No assemblies in $buildOutput."
}

$topLevel = @(
    (Join-Path $packagingDir 'README.txt'),
    (Join-Path $packagingDir 'Install.cmd'),
    (Join-Path $packagingDir 'Uninstall.cmd'),
    (Join-Path $PSScriptRoot 'Deploy-MantlePlaceRevit.ps1'),
    (Join-Path $PSScriptRoot 'Uninstall-MantlePlaceRevit.ps1')
)
foreach ($file in $topLevel) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "Missing $file. The package is incomplete without it."
    }
}

$manifest = Join-Path $addinProject $addinFileName
if (-not (Test-Path -LiteralPath $manifest)) {
    throw "The .addin manifest was not found at $manifest."
}

if (-not (Test-Path -LiteralPath $iconSource)) {
    throw "The bundle icon was not found at $iconSource. PackageContents.xml names it."
}

$zipName = "MantlePlace-Revit-$declaredVersion.zip"
$zipPath = Join-Path $OutputDirectory $zipName
$staging = Join-Path $OutputDirectory "staging-$declaredVersion"

if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
New-Item -ItemType Directory -Path $staging -Force | Out-Null

# The bundle root. Everything below goes inside it, so the zip has exactly one top-level folder.
$bundleRoot = Join-Path $staging $bundleFolderName
New-Item -ItemType Directory -Path $bundleRoot -Force | Out-Null

$contents = Join-Path $bundleRoot 'Contents'
New-Item -ItemType Directory -Path $contents -Force | Out-Null

Copy-Item -LiteralPath $manifest -Destination $contents
foreach ($file in $payload) {
    Copy-Item -LiteralPath $file.FullName -Destination $contents
}
foreach ($file in $topLevel) {
    Copy-Item -LiteralPath $file -Destination $bundleRoot
}

# The bundle icon, in a subfolder on purpose: the by-hand install copies only the files directly
# in Contents, so the icon travels for the storefront and is never written to a Revit add-ins folder.
$iconDir = Join-Path $contents 'Resources'
New-Item -ItemType Directory -Path $iconDir -Force | Out-Null
Copy-Item -LiteralPath $iconSource -Destination $iconDir

# ---------------------------------------------------------------------------
# PackageContents.xml: built by the core's pure function from the version the build was compiled
# with, then checked against the declared one. A mismatch means the build is stale (or the wrong
# one is being packaged), and a zip whose manifest tells the storefront a different version from
# the one in its file name is worse than no zip.
# ---------------------------------------------------------------------------
$bundleManifestPath = Join-Path $bundleRoot $bundleManifestFileName
$null = & dotnet run --project $manifestWriterProject -c $Configuration --no-build -- $bundleManifestPath
if ($LASTEXITCODE -ne 0) {
    throw "Writing $bundleManifestFileName failed with exit code $LASTEXITCODE. If the tool is not built, run without -SkipBuild first."
}

[xml]$bundleManifest = Get-Content -LiteralPath $bundleManifestPath -Raw
$manifestVersion = $bundleManifest.ApplicationPackage.AppVersion
if ($manifestVersion -ne $declaredVersion) {
    throw "$bundleManifestFileName carries $manifestVersion but $propsFile declares $declaredVersion. The build is stale -- rebuild without -SkipBuild. Nothing was packaged."
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

# Written entry by entry rather than with Compress-Archive: Windows PowerShell 5.1 stores names with
# backslashes, which some extractors keep as part of a file name, and a bundle that extracts with
# a backslash inside "Contents" loads nothing. Forward slashes, always.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stagingFull = [System.IO.Path]::GetFullPath($staging).TrimEnd('\', '/')
$zipStream = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::Create)
try {
    $archive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in (Get-ChildItem -LiteralPath $staging -Recurse -File)) {
            $entryName = $file.FullName.Substring($stagingFull.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    $zipStream.Dispose()
}
Remove-Item -LiteralPath $staging -Recurse -Force

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $zipPath).Length

Write-Host ''
Write-Host "Packaged $zipName" -ForegroundColor Green
Write-Host "  $zipPath"
Write-Host "  $size bytes"
Write-Host "  sha256 $hash"
Write-Host "  built from $productVersion"
Write-Host "  root folder $bundleFolderName, $bundleManifestFileName version $manifestVersion"
Write-Host ''
Write-Host 'Not released yet. Before publishing:' -ForegroundColor Yellow
Write-Host '  - install THIS zip into Revit 2025, 2026 and 2027, confirm the ribbon, complete one'
Write-Host '    real import in each (MANTLEPLACE_BUNDLE_ZIP skips the file picker, so it can be driven'
Write-Host '    unattended). 2027 is the leg that matters: .NET 8 assembly, .NET 10 runtime.'
Write-Host "  - tag revit-$declaredVersion  (no leading v; see docs/adr/0001-per-host-release-tracks.md)"
Write-Host '  - state the source commit, the sha256 above, and what was and was not verified.'
