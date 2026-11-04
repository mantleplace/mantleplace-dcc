<#
.SYNOPSIS
    Installs the Mantle Place AutoCAD plugin into the per-user Autodesk bundle folder, from a source
    build or from an extracted release.

.DESCRIPTION
    Two callers, one script, on purpose. Maintainers run it against the source tree, where it builds
    and lays out the bundle first; a curator runs it against an extracted release zip with
    -PayloadDirectory, where there is nothing to build. Keeping them in one file is what gives the
    installer a curator runs any test coverage at all -- CI can never execute it, because CI can never
    build this plugin (no AutoCAD on a hosted runner), so the only proof it works is that maintainers
    run the same file.

    AutoCAD and Civil 3D read Autodesk bundles from %APPDATA%\Autodesk\ApplicationPlugins at startup,
    so installing is copying the bundle folder there: MantlePlaceAutoCad.bundle, holding
    PackageContents.xml and Contents. Hand-copying that folder is a perfectly good way to install a
    RELEASE, and the zip's README leads with it (ADR 0005). This script is for a source build, where
    copying by hand is how a machine ends up running a plugin months older than the source tree, and
    it prints the version and timestamp of what it wrote.

    It writes MantlePlace.install.json beside PackageContents.xml: which commit, which branch, which
    worktree, whether that tree was dirty, and when. The stamp is also how this script knows a bundle
    folder is its own. The stamp records; it never refuses.

    It refuses, before writing anything, while a Mantle Place AutoCAD bundle it did not write is
    present in any of the three Autodesk bundle folders (the per-user one and the two machine-wide
    ones): the storefront's install, or the release zip's bundle placed there by hand. AutoCAD would
    otherwise load the plugin twice, and a machine must never load it twice. The refusal names the
    folder; remove it (or uninstall Mantle Place for AutoCAD from the Autodesk storefront) or keep it
    and do not run this script. A bundle is Mantle Place's by its folder name or by the name its
    PackageContents.xml declares, which survives a rename.

    The Revit plugin's bundle has a different folder and name, so it never trips this refusal, and the
    two plugins can be installed side by side.

    Windows PowerShell 5.1 compatible, deliberately. That is what is on a curator machine by
    default; PowerShell 7 is not. No ternaries, no null-coalescing, no pipeline chain operators.

.PARAMETER PayloadDirectory
    Install the bundle already in this directory, without building. This is the release path: point it
    at the extracted zip's MantlePlaceAutoCad.bundle folder. PackageContents.xml and Contents are
    expected inside it. When this is given, -Configuration, -SkipBuild and -ApiDirNet8 are unused and
    no part of the source tree is touched.

.PARAMETER Configuration
    Debug (default) or Release. Source-tree path only.

.PARAMETER SkipBuild
    Lay out whatever is already in bin/ without rebuilding. Source-tree path only.

.PARAMETER ApiDirNet8
    The AutoCAD API directory the .NET 8 build compiles against; see Package-MantlePlaceAutoCad.ps1.
    Source-tree path only.

.EXAMPLE
    ./Deploy-MantlePlaceAutoCad.ps1 -ApiDirNet8 'C:\ObjectARX\2025\inc'
    ./Deploy-MantlePlaceAutoCad.ps1 -PayloadDirectory .\MantlePlaceAutoCad.bundle
#>
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

# Runs git with stderr discarded, leaving the exit code in $LASTEXITCODE. Windows PowerShell 5.1
# turns a native command's redirected stderr into an error record, and under 'Stop' that record
# terminates the script. Scoping the preference to this function is the fix. Duplicated from the Revit
# deploy script on purpose: this file ships verbatim in the release zip and can import nothing.
function Invoke-Git {
    # A simple function, not an advanced one: with [Parameter()] git's own -e/-c/-C would bind to
    # PowerShell's common parameters instead of reaching git.
    $ErrorActionPreference = 'Continue'
    $output = & git @args 2>$null
    return $output
}

$fromRelease = -not [string]::IsNullOrWhiteSpace($PayloadDirectory)
$userBundleRoot = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'
$target = Join-Path $userBundleRoot $bundleFolderName

# ---------------------------------------------------------------------------
# Refuse before writing anything if AutoCAD or Civil 3D is open.
#
# A loaded plugin DLL is file-locked, so replacing the bundle fails PART WAY THROUGH: some files
# replaced, some not. That leaves a machine running a MIXED build -- a strictly worse version of the
# stale-build failure this script exists to prevent, and silent in the same way. Civil 3D runs as
# acad.exe, and the headless console is accoreconsole.exe.
# ---------------------------------------------------------------------------
$running = @(Get-Process -Name 'acad', 'accoreconsole' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    throw "AutoCAD or Civil 3D is running, so its plugin files are locked and only some of them would be replaced. Close it and run this again. Nothing was installed."
}

# ---------------------------------------------------------------------------
# Refuse before writing anything if a Mantle Place AutoCAD bundle this script did not write is present.
#
# AutoCAD scans three Autodesk bundle folders at startup: the per-user one and the two machine-wide
# ones. A bundle in any of them loads the same plugin this script is about to install. The one bundle
# this script may replace is its own earlier install: the folder it writes, carrying the stamp it
# writes. Anything else is somebody else's install (the storefront's, or a hand copy) and is named.
# ---------------------------------------------------------------------------
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
                        # An unreadable manifest is not ours to judge; the folder name was the other test.
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

    # Resolved here and nowhere else: this branch must never reach for the parent of $PSScriptRoot,
    # because in a release the script sits in an extracted zip and that parent is a Downloads folder.
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

    # The same layout a release has: this script installs what the package script would zip.
    $packageArgs = @{ Configuration = $Configuration; OutputDirectory = $stage; BundleOnly = $true }
    if ($SkipBuild) { $packageArgs['SkipBuild'] = $true }
    if (-not [string]::IsNullOrWhiteSpace($ApiDirNet8)) { $packageArgs['ApiDirNet8'] = $ApiDirNet8 }
    & $packageScript @packageArgs

    $staged = @(Get-ChildItem -LiteralPath $stage -Directory -Filter 'staging-*' -ErrorAction SilentlyContinue)
    if ($staged.Count -ne 1) {
        throw "Packaging left no bundle to install in $stage. Nothing was deployed."
    }
    $source = Join-Path $staged[0].FullName $bundleFolderName

    # What the stamp will say about where this build came from. Read from git rather than from the
    # assembly so the stamp can disagree with the assembly. Each call is allowed to fail: git may be
    # absent, and the script must still install.
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
            # Scoped to autocad/, the same scope the build's own -dirty marker uses.
            $status = @(Invoke-Git -C $autoCadRoot status --porcelain --untracked-files=all -- .)
            $sourceDirty = ($LASTEXITCODE -eq 0) -and ($status.Count -gt 0)
            $sourceTree = Invoke-Git -C $autoCadRoot rev-parse --show-toplevel
            if ($LASTEXITCODE -ne 0) { $sourceTree = $null }
            if ($null -ne $sourceTree) { $sourceTree = $sourceTree -replace '/', '\' }
        }
    }
}

# The version the bundle's own manifest declares: what is printed is what is being copied.
[xml]$manifest = Get-Content -LiteralPath (Join-Path $source $manifestFileName) -Raw
$version = $manifest.ApplicationPackage.AppVersion
if ([string]::IsNullOrWhiteSpace($version)) {
    $version = 'unknown version'
}

$shell = @(Get-ChildItem -LiteralPath (Join-Path $source 'Contents') -Recurse -File -Filter $shellFileName)
if ($shell.Count -eq 0) {
    throw "$shellFileName is missing from $source. Without it AutoCAD has nothing to load."
}

# A clean replace: the previous install is this script's own (the refusal above guarantees it), and
# removing it first means no file of an older build survives beside a newer one.
if (Test-Path -LiteralPath $target) {
    Remove-Item -LiteralPath $target -Recurse -Force
}
New-Item -ItemType Directory -Path $target -Force | Out-Null

# The stamp. Schema 1; installedAt is ISO 8601 with the local offset, as text, because it is read out to
# a human and never computed on. Key order is fixed so two stamps diff cleanly.
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
# Written without a BOM, by hand: Windows PowerShell's -Encoding utf8 adds one.
[System.IO.File]::WriteAllText((Join-Path $target $stampFileName), ($stamp | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))

# Stamped first, so a copy that fails part way leaves a folder this script still knows as its own.
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
