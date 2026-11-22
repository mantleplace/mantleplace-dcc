[CmdletBinding()]
param(
    [string]$PayloadDirectory,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string[]]$RevitVersions = @('2025', '2026', '2027'),

    [switch]$SkipBuild,

    [switch]$Launch,

    [string]$LaunchVersion = '2027'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$addinFileName = 'MantlePlace.addin'
$shimFileName = 'MantlePlace.Revit.Addin.dll'
$stampFileName = 'MantlePlace.install.json'

function Invoke-Git {
    $ErrorActionPreference = 'Continue'
    $output = & git @args 2>$null
    return $output
}
$fromRelease = -not [string]::IsNullOrWhiteSpace($PayloadDirectory)

if ($Launch -and ($RevitVersions -notcontains $LaunchVersion)) {
    throw "-LaunchVersion $LaunchVersion is not one of the versions being installed ($($RevitVersions -join ', ')). Nothing was installed."
}

$running = @(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    throw "Revit is running, so its plugin files are locked and only some of them would be replaced. Close Revit and run this again. Nothing was installed."
}

function Get-MantlePlaceBundleFolders {
    $roots = @()
    foreach ($base in @($env:APPDATA, $env:ProgramData, $env:ProgramFiles)) {
        if (-not [string]::IsNullOrWhiteSpace($base)) {
            $roots += (Join-Path $base 'Autodesk\ApplicationPlugins')
        }
    }

    $found = @()
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($bundle in @(Get-ChildItem -LiteralPath $root -Directory -Filter '*.bundle' -ErrorAction SilentlyContinue)) {
            $isMantlePlace = $bundle.Name -eq 'MantlePlace.bundle'
            if (-not $isMantlePlace) {
                $packageContents = Join-Path $bundle.FullName 'PackageContents.xml'
                if (Test-Path -LiteralPath $packageContents) {
                    try {
                        [xml]$package = Get-Content -LiteralPath $packageContents -Raw
                        $isMantlePlace = $package.ApplicationPackage.Name -eq 'Mantle Place'
                    }
                    catch {
                        $isMantlePlace = $false
                    }
                }
            }
            if ($isMantlePlace) { $found += $bundle.FullName }
        }
    }
    return $found
}

$bundleFolders = @(Get-MantlePlaceBundleFolders)
if ($bundleFolders.Count -gt 0) {
    throw "A Mantle Place bundle is already installed in $($bundleFolders -join ', '). Revit would load the plugin twice. Remove that folder (or uninstall Mantle Place from the Autodesk storefront), or keep it and do not run this script. Nothing was installed."
}

if ($fromRelease) {
    if (-not (Test-Path -LiteralPath $PayloadDirectory)) {
        throw "No such directory: $PayloadDirectory. Point -PayloadDirectory at the Contents folder inside the extracted release zip."
    }

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
    $source = Join-Path $addinProject "bin/$Configuration"

    if (-not $SkipBuild) {
        Write-Host "Building $Configuration ..." -ForegroundColor Cyan
        & dotnet build $solution -c $Configuration
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed with exit code $LASTEXITCODE. Nothing was deployed."
        }
    }

    if (-not (Test-Path -LiteralPath $source)) {
        throw "No build output at $source. Run without -SkipBuild first."
    }

    $manifest = Join-Path $addinProject $addinFileName
    if (-not (Test-Path -LiteralPath $manifest)) {
        throw "The .addin manifest was not found at $manifest."
    }

    $sourceSha = $null
    $sourceBranch = $null
    $sourceDirty = $false
    $sourceTree = $null
    if ($null -ne (Get-Command git -ErrorAction SilentlyContinue)) {
        $sourceSha = Invoke-Git -C $revitRoot rev-parse HEAD
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceSha)) { $sourceSha = $null }
        if ($null -ne $sourceSha) {
            $sourceBranch = Invoke-Git -C $revitRoot rev-parse --abbrev-ref HEAD
            if ($LASTEXITCODE -ne 0 -or $sourceBranch -eq 'HEAD') { $sourceBranch = $null }
            $status = @(Invoke-Git -C $revitRoot status --porcelain --untracked-files=all -- .)
            $sourceDirty = ($LASTEXITCODE -eq 0) -and ($status.Count -gt 0)
            $sourceTree = Invoke-Git -C $revitRoot rev-parse --show-toplevel
            if ($LASTEXITCODE -ne 0) { $sourceTree = $null }
            if ($null -ne $sourceTree) { $sourceTree = $sourceTree -replace '/', '\' }
        }
    }
}

$payload = @(Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Extension -in '.dll', '.pdb', '.json' })
if ($payload.Count -eq 0) {
    throw "No assemblies in $source."
}

$shim = $payload | Where-Object { $_.Name -eq $shimFileName } | Select-Object -First 1
if ($null -eq $shim) {
    throw "$shimFileName is missing from $source. Without it Revit has nothing to load."
}

$version = $shim.VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($version)) {
    $version = 'unknown version'
}

$addinsRoot = Join-Path $env:APPDATA 'Autodesk/Revit/Addins'

function Get-RuntimeMajor([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try {
        $tfm = (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).runtimeOptions.tfm
    }
    catch {
        return $null
    }
    if ($tfm -match '^net(\d+)\.') { return [int]$Matches[1] }
    return $null
}

$buildRuntime = Get-RuntimeMajor (Join-Path $source 'MantlePlace.Revit.Addin.runtimeconfig.json')
$skipped = @()

$installedAt = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')

foreach ($revitVersion in $RevitVersions) {
    $hostRuntime = Get-RuntimeMajor (Join-Path $env:ProgramFiles "Autodesk\Revit $revitVersion\RevitAPI.runtimeconfig.json")
    if ($null -ne $buildRuntime -and $null -ne $hostRuntime -and $hostRuntime -lt $buildRuntime) {
        Write-Host "Skipped Revit $revitVersion -- it runs .NET $hostRuntime and this build is .NET $buildRuntime, which it cannot load. Install its latest update, or build against a .NET $hostRuntime API (revit/README.md)." -ForegroundColor Yellow
        $skipped += $revitVersion
        continue
    }

    $destination = Join-Path $addinsRoot $revitVersion
    if (-not (Test-Path -LiteralPath $destination)) {
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
    }

    Copy-Item -LiteralPath $manifest -Destination $destination -Force
    foreach ($file in $payload) {
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }

    $stampPath = Join-Path $destination $stampFileName
    $stamp = [ordered]@{
        schema = 1
        source = $(if ($fromRelease) { 'release' } else { 'source-tree' })
        version = $version
        installedAt = $installedAt
    }
    if (-not $fromRelease) {
        $stamp['sha'] = $sourceSha
        $stamp['dirty'] = [bool]$sourceDirty
        $stamp['branch'] = $sourceBranch
        $stamp['sourceTree'] = $sourceTree
        $stamp['configuration'] = $Configuration
    }
    [System.IO.File]::WriteAllText($stampPath, ($stamp | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))

    $written = (Get-Item -LiteralPath (Join-Path $destination $shimFileName)).LastWriteTime
    Write-Host "Installed to $destination" -ForegroundColor Green
    Write-Host "  Mantle Place $version  $written  ($($payload.Count) files + manifest + stamp)"
}

if (-not $fromRelease) {
    if ($null -eq $sourceSha) {
        $provenance = 'no git: the stamp names no commit'
    }
    else {
        $short = $sourceSha.Substring(0, 7)
        $branchText = if ($null -eq $sourceBranch) { 'a detached head' } else { $sourceBranch }
        $dirtyText = if ($sourceDirty) { ', with uncommitted changes under revit/' } else { '' }
        $provenance = "$branchText@$short$dirtyText, from $sourceTree"
    }
    Write-Host "  Stamped: $provenance"
    if ($null -ne $sourceSha -and $sourceBranch -ne 'main') {
        Write-Host '  This is a preview, not main. The install returns to main when a session deploys after a merge.' -ForegroundColor Yellow
    }
}

if ($skipped.Count -gt 0) {
    Write-Host "  Not installed for Revit $($skipped -join ', '): its runtime is older than this build's." -ForegroundColor Yellow
}

Write-Host ''
if ($Launch) {
    $revitExe = Join-Path (Join-Path $env:ProgramFiles "Autodesk\Revit $LaunchVersion") 'Revit.exe'
    if (-not (Test-Path -LiteralPath $revitExe)) {
        throw "Revit $LaunchVersion is not installed at $revitExe. The plugin was installed; launch a Revit by hand."
    }
    $hotReload = (-not $fromRelease) -and ($Configuration -eq 'Debug')
    if ($hotReload) {
        Write-Host "Starting Revit $LaunchVersion with DOTNET_MODIFIABLE_ASSEMBLIES=debug. Attach a debugger to Revit.exe for Hot Reload of method bodies." -ForegroundColor Cyan
    }
    else {
        Write-Host "Starting Revit $LaunchVersion." -ForegroundColor Cyan
    }
    Write-Host 'If the Security - Unsigned Add-In dialog appears, answer Always Load and let Revit exit normally once, or the answer is not kept.' -ForegroundColor Yellow
    $previous = $env:DOTNET_MODIFIABLE_ASSEMBLIES
    try {
        if ($hotReload) { $env:DOTNET_MODIFIABLE_ASSEMBLIES = 'debug' }
        Start-Process -FilePath $revitExe | Out-Null
    }
    finally {
        if ($null -eq $previous) { Remove-Item Env:\DOTNET_MODIFIABLE_ASSEMBLIES -ErrorAction SilentlyContinue }
        else { $env:DOTNET_MODIFIABLE_ASSEMBLIES = $previous }
    }
}
else {
    Write-Host 'Restart Revit to load the new build. Revit reads the add-ins folder only at startup.' -ForegroundColor Yellow
}
