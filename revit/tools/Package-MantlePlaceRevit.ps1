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
$buildOutput = Join-Path $addinProject "bin/$Configuration"
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

$builtVersion = $productVersion
$metadata = $builtVersion.IndexOf('+')
if ($metadata -ge 0) {
    $builtVersion = $builtVersion.Substring(0, $metadata)
}

if ($builtVersion -ne $declaredVersion) {
    throw "$shimFileName reports $builtVersion but $propsFile declares $declaredVersion. The build is stale -- rebuild without -SkipBuild."
}

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

$iconDir = Join-Path $contents 'Resources'
New-Item -ItemType Directory -Path $iconDir -Force | Out-Null
Copy-Item -LiteralPath $iconSource -Destination $iconDir

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
