[CmdletBinding()]
param(
    [string]$PayloadDirectory,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$SkipBuild,

    [string]$ApiDirNet8
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$bundleFolderName = 'MantlePlaceAutoCad.bundle'
$bundleAppName = 'Mantle Place for AutoCAD'
$manifestFileName = 'PackageContents.xml'
$shellFileName = 'MantlePlace.AutoCad.Addin.dll'
$stampFileName = 'MantlePlace.install.json'

function Invoke-Git {
    $ErrorActionPreference = 'Continue'
    $output = & git @args 2>$null
    return $output
}

$fromRelease = -not [string]::IsNullOrWhiteSpace($PayloadDirectory)
$userBundleRoot = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'
$target = Join-Path $userBundleRoot $bundleFolderName

$running = @(Get-Process -Name 'acad', 'accoreconsole' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    throw "AutoCAD or Civil 3D is running, so its plugin files are locked and only some of them would be replaced. Close it and run this again. Nothing was installed."
}

function Get-ForeignMantlePlaceBundleFolders {
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
            $isMantlePlace = $bundle.Name -eq $bundleFolderName
            if (-not $isMantlePlace) {
                $packageContents = Join-Path $bundle.FullName $manifestFileName
                if (Test-Path -LiteralPath $packageContents) {
                    try {
                        [xml]$package = Get-Content -LiteralPath $packageContents -Raw
                        $isMantlePlace = $package.ApplicationPackage.Name -eq $bundleAppName
                    }
                    catch {
                        $isMantlePlace = $false
                    }
                }
            }
            if (-not $isMantlePlace) { continue }

            $isOurs = ($bundle.FullName -eq $target) -and (Test-Path -LiteralPath (Join-Path $bundle.FullName $stampFileName))
            if (-not $isOurs) { $found += $bundle.FullName }
        }
    }
    return $found
}

$foreign = @(Get-ForeignMantlePlaceBundleFolders)
if ($foreign.Count -gt 0) {
    throw "A Mantle Place AutoCAD bundle is already installed in $($foreign -join ', '). AutoCAD would load the plugin twice. Remove that folder (or uninstall Mantle Place for AutoCAD from the Autodesk storefront), or keep it and do not run this script. Nothing was installed."
}

if ($fromRelease) {
    if (-not (Test-Path -LiteralPath $PayloadDirectory)) {
        throw "No such directory: $PayloadDirectory. Point -PayloadDirectory at the $bundleFolderName folder inside the extracted release zip."
    }

    $source = (Resolve-Path -LiteralPath $PayloadDirectory).Path
    if (-not (Test-Path -LiteralPath (Join-Path $source $manifestFileName)) -or -not (Test-Path -LiteralPath (Join-Path $source 'Contents'))) {
        throw "No $manifestFileName and Contents in $source. That folder is not a Mantle Place AutoCAD bundle."
    }
}
else {
    $autoCadRoot = Split-Path -Parent $PSScriptRoot
    $packageScript = Join-Path $PSScriptRoot 'Package-MantlePlaceAutoCad.ps1'
    $stage = Join-Path $autoCadRoot 'dist/deploy'

    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }

    $packageArgs = @{ Configuration = $Configuration; OutputDirectory = $stage; BundleOnly = $true }
    if ($SkipBuild) { $packageArgs['SkipBuild'] = $true }
    if (-not [string]::IsNullOrWhiteSpace($ApiDirNet8)) { $packageArgs['ApiDirNet8'] = $ApiDirNet8 }
    & $packageScript @packageArgs

    $staged = @(Get-ChildItem -LiteralPath $stage -Directory -Filter 'staging-*' -ErrorAction SilentlyContinue)
    if ($staged.Count -ne 1) {
        throw "Packaging left no bundle to install in $stage. Nothing was deployed."
    }
    $source = Join-Path $staged[0].FullName $bundleFolderName

    $sourceSha = $null
    $sourceBranch = $null
    $sourceDirty = $false
    $sourceTree = $null
    if ($null -ne (Get-Command git -ErrorAction SilentlyContinue)) {
        $sourceSha = Invoke-Git -C $autoCadRoot rev-parse HEAD
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceSha)) { $sourceSha = $null }
        if ($null -ne $sourceSha) {
            $sourceBranch = Invoke-Git -C $autoCadRoot rev-parse --abbrev-ref HEAD
            if ($LASTEXITCODE -ne 0 -or $sourceBranch -eq 'HEAD') { $sourceBranch = $null }
            $status = @(Invoke-Git -C $autoCadRoot status --porcelain --untracked-files=all -- .)
            $sourceDirty = ($LASTEXITCODE -eq 0) -and ($status.Count -gt 0)
            $sourceTree = Invoke-Git -C $autoCadRoot rev-parse --show-toplevel
            if ($LASTEXITCODE -ne 0) { $sourceTree = $null }
            if ($null -ne $sourceTree) { $sourceTree = $sourceTree -replace '/', '\' }
        }
    }
}

[xml]$manifest = Get-Content -LiteralPath (Join-Path $source $manifestFileName) -Raw
$version = $manifest.ApplicationPackage.AppVersion
if ([string]::IsNullOrWhiteSpace($version)) {
    $version = 'unknown version'
}

$shell = @(Get-ChildItem -LiteralPath (Join-Path $source 'Contents') -Recurse -File -Filter $shellFileName)
if ($shell.Count -eq 0) {
    throw "$shellFileName is missing from $source. Without it AutoCAD has nothing to load."
}

if (Test-Path -LiteralPath $target) {
    Remove-Item -LiteralPath $target -Recurse -Force
}
New-Item -ItemType Directory -Path $target -Force | Out-Null

$stamp = [ordered]@{
    schema = 1
    source = $(if ($fromRelease) { 'release' } else { 'source-tree' })
    version = $version
    installedAt = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
}
if (-not $fromRelease) {
    $stamp['sha'] = $sourceSha
    $stamp['dirty'] = [bool]$sourceDirty
    $stamp['branch'] = $sourceBranch
    $stamp['sourceTree'] = $sourceTree
    $stamp['configuration'] = $Configuration
}
[System.IO.File]::WriteAllText((Join-Path $target $stampFileName), ($stamp | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))

Copy-Item -LiteralPath (Join-Path $source $manifestFileName) -Destination $target -Force
Copy-Item -LiteralPath (Join-Path $source 'Contents') -Destination $target -Recurse -Force

$written = (Get-Item -LiteralPath (Join-Path $target $manifestFileName)).LastWriteTime
Write-Host "Installed to $target" -ForegroundColor Green
Write-Host "  Mantle Place for AutoCAD $version  $written"

if (-not $fromRelease) {
    if ($null -eq $sourceSha) {
        $provenance = 'no git: the stamp names no commit'
    }
    else {
        $short = $sourceSha.Substring(0, 7)
        $branchText = if ($null -eq $sourceBranch) { 'a detached head' } else { $sourceBranch }
        $dirtyText = if ($sourceDirty) { ', with uncommitted changes under autocad/' } else { '' }
        $provenance = "$branchText@$short$dirtyText, from $sourceTree"
    }
    Write-Host "  Stamped: $provenance"
    if ($null -ne $sourceSha -and $sourceBranch -ne 'main') {
        Write-Host '  This is a preview, not main. The install returns to main when a session deploys after a merge.' -ForegroundColor Yellow
    }
}

Write-Host ''
Write-Host 'Restart AutoCAD or Civil 3D to load the new build. AutoCAD reads its bundle folders only at startup.' -ForegroundColor Yellow
Write-Host 'Run MANTLEPLACEABOUT to see which build loaded.'
