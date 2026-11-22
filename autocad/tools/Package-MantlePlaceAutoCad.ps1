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

$bundleRoot = Join-Path $staging $bundleFolderName
$contents = Join-Path $bundleRoot 'Contents'
New-Item -ItemType Directory -Path $contents -Force | Out-Null

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
