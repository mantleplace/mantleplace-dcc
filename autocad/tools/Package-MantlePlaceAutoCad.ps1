<#
.SYNOPSIS
    Builds and packages the Mantle Place AutoCAD plugin as a release zip.

.DESCRIPTION
    Produces MantlePlace-AutoCad-<version>.zip. Its one root folder is MantlePlaceAutoCad.bundle, an
    Autodesk Application Plugin Bundle (ADR 0018), holding PackageContents.xml and a Contents folder,
    with README.txt, Install.cmd and Deploy-MantlePlaceAutoCad.ps1 beside them. The same archive
    installs from the Autodesk storefront and by hand: the copy-first install in README.txt copies the
    bundle folder as it is (ADR 0005).

    Contents holds the two builds the plugin ships: net8 (AutoCAD and Civil 3D 2025 and 2026, which run
    .NET 8) and net10 (2027, which runs .NET 10). Each holds the shell, the Civil 3D part and the
    plugin's own core; PackageContents.xml names, for every year, the shell of the build that year
    loads. No assembly is shared with the Revit plugin: the Revit sources it reuses are compiled into
    this plugin's core.

    PackageContents.xml is built by the same pure function the Revit package uses
    (AutodeskBundleManifest, given the AutoCAD host), reached through tools/WriteBundleManifest, from
    the version the build was compiled with. This script fails when that version differs from the one
    Directory.Build.props declares, the same stale-build check the shell gets, and when a module the
    manifest names is not in the bundle.

    THIS CANNOT RUN IN CI, and no workflow should ever pretend otherwise. Building the shell needs the
    AutoCAD and Civil 3D API assemblies from a licensed install, which are neither vendored nor
    redistributable, and this repository must never carry a self-hosted runner (a fork's pull request
    would execute on the build machine). A release is built on the same private licensed machine that
    runs the Unreal engine-compile gate.

    The .NET 8 build must compile against an API directory that still references .NET 8 (an AutoCAD
    install that has not been updated to .NET 10, or the ObjectARX SDK's managed assemblies); an
    updated 2025 or 2026 install fails with CS1705. Pass it with -ApiDirNet8.

    Packaging is not the release gate. Producing this zip proves the plugin COMPILES. What proves it
    works is loading the same output in AutoCAD and Civil 3D 2025, 2026 and 2027 -- in 2025 and 2026
    on .NET 8 -- and running MANTLEPLACEABOUT in each.

.PARAMETER Configuration
    Release by default, and there is rarely a reason to change it.

.PARAMETER OutputDirectory
    Where the zip is written. Defaults to autocad/dist, which is git-ignored.

.PARAMETER SkipBuild
    Package whatever is already in bin/. The version check below still runs, so a stale build is
    caught rather than shipped.

.PARAMETER BundleOnly
    Stop after laying out MantlePlaceAutoCad.bundle under -OutputDirectory\staging-<version>; write no
    zip. This is how Deploy-MantlePlaceAutoCad.ps1 installs a source build, so a deploy and a release
    are one layout.

.PARAMETER ApiDirNet8
    The AutoCAD API directory the .NET 8 build compiles against; see above. Default: the 2025 install.

.EXAMPLE
    ./Package-MantlePlaceAutoCad.ps1 -ApiDirNet8 'C:\ObjectARX\2025\inc'
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutputDirectory,

    [switch]$SkipBuild,

    [switch]$BundleOnly,

    [string]$ApiDirNet8
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$shellFileName = 'MantlePlace.AutoCad.Addin.dll'
$civilFileName = 'MantlePlace.AutoCad.Civil3d.dll'
$bundleFolderName = 'MantlePlaceAutoCad.bundle'
$bundleManifestFileName = 'PackageContents.xml'

# The two builds: the folder under Contents (AutodeskBundleManifest.AutoCadNet8Folder and
# AutoCadNet10Folder; the manifest's module paths are checked against what is staged below) and the
# target framework whose output goes in it.
$builds = @(
    @{ Folder = 'net8'; Framework = 'net8.0-windows' },
    @{ Folder = 'net10'; Framework = 'net10.0-windows' }
)

$autoCadRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $autoCadRoot
$solution = Join-Path $autoCadRoot 'MantlePlace.AutoCad.slnx'
$shellProject = Join-Path $autoCadRoot 'src/MantlePlace.AutoCad.Addin'
$civilProject = Join-Path $autoCadRoot 'src/MantlePlace.AutoCad.Civil3d'
$manifestWriterProject = Join-Path $PSScriptRoot 'WriteBundleManifest/WriteBundleManifest.csproj'
$iconSource = Join-Path $repoRoot 'revit/src/MantlePlace.Revit.Addin/Resources/MantlePlaceMark_48.png'
$packagingDir = Join-Path $autoCadRoot 'packaging'
$propsFile = Join-Path $autoCadRoot 'Directory.Build.props'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $autoCadRoot 'dist'
}

if (-not $SkipBuild) {
    Write-Host "Building $Configuration ..." -ForegroundColor Cyan
    $buildArgs = @('build', $solution, '-c', $Configuration)
    if (-not [string]::IsNullOrWhiteSpace($ApiDirNet8)) {
        $buildArgs += "-p:AutoCadApiDirNet8=$ApiDirNet8"
    }
    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE. Nothing was packaged."
    }
}

# ---------------------------------------------------------------------------
# The version has one home (autocad/Directory.Build.props) and this is where that claim is checked,
# for each build: the props edit that never made it into a build (packaging a stale bin/), and the
# <Version> element going missing, which is not blank but MSBuild's default 1.0.0.
# ---------------------------------------------------------------------------
[xml]$props = Get-Content -LiteralPath $propsFile -Raw
$versionNode = $props.SelectSingleNode('//PropertyGroup/Version')
if ($null -eq $versionNode) {
    throw "No <Version> in $propsFile. That element is the plugin's version and its only home."
}
$declaredVersion = $versionNode.InnerText.Trim()

$productVersion = $null
foreach ($build in $builds) {
    $shellOutput = Join-Path $shellProject "bin/$Configuration/$($build.Framework)"
    $shellPath = Join-Path $shellOutput $shellFileName
    if (-not (Test-Path -LiteralPath $shellPath)) {
        throw "$shellFileName is missing from $shellOutput. Run without -SkipBuild first."
    }

    $buildProductVersion = (Get-Item -LiteralPath $shellPath).VersionInfo.ProductVersion
    # The SDK appends '+<commit sha>' as semver build metadata. It is worth having (a log can name
    # the exact source) but it is not part of the version, and comparing it would fail every build.
    $builtVersion = $buildProductVersion
    $metadata = $builtVersion.IndexOf('+')
    if ($metadata -ge 0) {
        $builtVersion = $builtVersion.Substring(0, $metadata)
    }
    if ($builtVersion -ne $declaredVersion) {
        throw "$($build.Folder)/$shellFileName reports $builtVersion but $propsFile declares $declaredVersion. The build is stale -- rebuild without -SkipBuild."
    }
    if ($null -eq $productVersion) { $productVersion = $buildProductVersion }
}

$topLevel = @(
    (Join-Path $packagingDir 'README.txt'),
    (Join-Path $packagingDir 'Install.cmd'),
    (Join-Path $PSScriptRoot 'Deploy-MantlePlaceAutoCad.ps1')
)
foreach ($file in $topLevel) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "Missing $file. The package is incomplete without it."
    }
}

if (-not (Test-Path -LiteralPath $iconSource)) {
    throw "The bundle icon was not found at $iconSource. PackageContents.xml names it."
}

$zipName = "MantlePlace-AutoCad-$declaredVersion.zip"
$zipPath = Join-Path $OutputDirectory $zipName
$staging = Join-Path $OutputDirectory "staging-$declaredVersion"

if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
New-Item -ItemType Directory -Path $staging -Force | Out-Null

# The bundle root. Everything below goes inside it, so the zip has exactly one top-level folder.
$bundleRoot = Join-Path $staging $bundleFolderName
$contents = Join-Path $bundleRoot 'Contents'
New-Item -ItemType Directory -Path $contents -Force | Out-Null

# .pdb travels on purpose: a stack trace out of a curator's AutoCAD is worth far more than the handful
# of kilobytes, and this plugin has no telemetry to fall back on.
foreach ($build in $builds) {
    $destination = Join-Path $contents $build.Folder
    New-Item -ItemType Directory -Path $destination -Force | Out-Null

    $sources = @(
        (Join-Path $shellProject "bin/$Configuration/$($build.Framework)"),
        (Join-Path $civilProject "bin/$Configuration/$($build.Framework)")
    )
    foreach ($source in $sources) {
        if (-not (Test-Path -LiteralPath $source)) {
            throw "No build output at $source. Run without -SkipBuild first."
        }
        # The core is built into both folders; the same file is overwritten with itself.
        foreach ($file in @(Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Extension -in '.dll', '.pdb' })) {
            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        }
    }

    foreach ($required in @($shellFileName, $civilFileName)) {
        if (-not (Test-Path -LiteralPath (Join-Path $destination $required))) {
            throw "$required is missing from the $($build.Folder) build."
        }
    }
}

foreach ($file in $topLevel) {
    Copy-Item -LiteralPath $file -Destination $bundleRoot
}

# The bundle icon, in a subfolder on purpose: a by-hand install copies the bundle folder whole, and the
# storefront reads the icon from here. It is the Revit plugin's render of the monogram (ADR 0009), copied
# at package time so no second binary is committed.
$iconDir = Join-Path $contents 'Resources'
New-Item -ItemType Directory -Path $iconDir -Force | Out-Null
Copy-Item -LiteralPath $iconSource -Destination $iconDir

# ---------------------------------------------------------------------------
# PackageContents.xml: built by the core's pure function from the version the build was compiled
# with, then checked against the declared one and against what is in the bundle. A mismatch means the
# build is stale (or the wrong one is being packaged), and a manifest that names a module the bundle
# does not hold, or tells the storefront a different version from the one in the file name, is worse
# than no zip.
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
foreach ($entry in @($bundleManifest.ApplicationPackage.Components.ComponentEntry)) {
    $module = Join-Path $bundleRoot ($entry.ModuleName -replace '^\./', '')
    if (-not (Test-Path -LiteralPath $module)) {
        throw "$bundleManifestFileName names $($entry.ModuleName), which is not in the bundle. Nothing was packaged."
    }
    if ($entry.Version -ne $declaredVersion) {
        throw "A component entry carries $($entry.Version) but $propsFile declares $declaredVersion. Nothing was packaged."
    }
}

if ($BundleOnly) {
    Write-Host "Laid out $bundleRoot (version $declaredVersion, built from $productVersion)." -ForegroundColor Green
    return
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
Write-Host '  - install THIS zip into AutoCAD and Civil 3D 2025, 2026 and 2027, and run MANTLEPLACEABOUT in each.'
Write-Host '    2025 and 2026 load the net8 build and must run on .NET 8; 2027 loads the net10 build.'
Write-Host "  - tag autocad-$declaredVersion  (no leading v; see docs/adr/0001-per-host-release-tracks.md)"
Write-Host '  - state the source commit, the sha256 above, and what was and was not verified.'
