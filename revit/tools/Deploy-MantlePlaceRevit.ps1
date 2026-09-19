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

    It also writes MantlePlace.install.json beside the manifest: which commit, which branch, which
    worktree, whether that tree was dirty, and when. The assembly knows its commit; it cannot know
    the branch or the worktree, and with several worktrees and one add-ins folder those are the
    questions that matter. The About dialog reads the stamp back, and
    revit/tools/Check-RevitInstall.ps1 compares it against origin/main. The stamp records; it never
    refuses. A deploy from a dirty tree or a feature branch is a legitimate preview, and a refusal
    path a curator can never reach would be untested code in the one file that ships verbatim.

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

.PARAMETER Launch
    After installing, start Revit -LaunchVersion (2027 by default: the only host where a .NET 8
    assembly runs under a .NET 10 runtime, so the leg that surfaces a difference first). A Debug
    source-tree deploy launches it with DOTNET_MODIFIABLE_ASSEMBLIES=debug, which is what lets a
    debugger attached to that Revit apply Hot Reload: edit a method body in the Client or Core, and
    the running add-in takes it without a restart. What Hot Reload cannot do is re-run OnStartup,
    so a ribbon change still needs a deploy and a restart -- and the first launch of a new build
    still needs a human to answer Always Load (revit/README.md).

.PARAMETER LaunchVersion
    Which Revit -Launch starts. Must be one of -RevitVersions.

.EXAMPLE
    ./Deploy-MantlePlaceRevit.ps1
    ./Deploy-MantlePlaceRevit.ps1 -Launch
    ./Deploy-MantlePlaceRevit.ps1 -Configuration Release -RevitVersions 2025
    ./Deploy-MantlePlaceRevit.ps1 -PayloadDirectory .\Contents
#>
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

# Runs git with stderr discarded, leaving the exit code in $LASTEXITCODE. Windows PowerShell 5.1
# turns a native command's redirected stderr into an error record, and under 'Stop' that record
# terminates the script -- so a bare `git rev-parse HEAD 2>$null` outside a repository would be a
# crash here, not a $null. Scoping the preference to this function is the fix. Duplicated from
# tools/local-install/LocalInstall.psm1 on purpose: this file ships verbatim in the release zip and
# can import nothing.
function Invoke-Git {
    # A simple function, not an advanced one: with [Parameter()] git's own -e/-c/-C would bind to
    # PowerShell's common parameters instead of reaching git.
    $ErrorActionPreference = 'Continue'
    $output = & git @args 2>$null
    return $output
}
$fromRelease = -not [string]::IsNullOrWhiteSpace($PayloadDirectory)

if ($Launch -and ($RevitVersions -notcontains $LaunchVersion)) {
    throw "-LaunchVersion $LaunchVersion is not one of the versions being installed ($($RevitVersions -join ', ')). Nothing was installed."
}

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

    # What the stamp will say about where this build came from. Read from git rather than from the
    # assembly so the stamp can disagree with the assembly -- that disagreement is the hand-copy
    # case About exists to report. Each call is allowed to fail: git may be absent, and the script
    # must still install.
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
            # Scoped to revit/, the same scope the build's own -dirty marker uses.
            $status = @(Invoke-Git -C $revitRoot status --porcelain --untracked-files=all -- .)
            $sourceDirty = ($LASTEXITCODE -eq 0) -and ($status.Count -gt 0)
            $sourceTree = Invoke-Git -C $revitRoot rev-parse --show-toplevel
            if ($LASTEXITCODE -ne 0) { $sourceTree = $null }
            if ($null -ne $sourceTree) { $sourceTree = $sourceTree -replace '/', '\' }
        }
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

# Read once, before the loop: one run is one install, and Check-RevitInstall.ps1 groups the add-ins
# folders by their stamps. A clock read per folder crossed a second between two of them and made
# the check report one deploy as folders that disagree.
$installedAt = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')

foreach ($revitVersion in $RevitVersions) {
    $destination = Join-Path $addinsRoot $revitVersion
    if (-not (Test-Path -LiteralPath $destination)) {
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
    }

    Copy-Item -LiteralPath $manifest -Destination $destination -Force
    foreach ($file in $payload) {
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }

    # The stamp. Schema 1; InstallStamp in MantlePlace.Revit.Core is its one reader, and
    # Check-RevitInstall.ps1 its one comparer. installedAt is ISO 8601 with the local offset, as
    # text, because it is read out to a human and never computed on. Key order is fixed so two
    # stamps diff cleanly.
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
    # Written without a BOM, by hand: Windows PowerShell's -Encoding utf8 adds one.
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

Write-Host ''
if ($Launch) {
    $revitExe = Join-Path (Join-Path $env:ProgramFiles "Autodesk\Revit $LaunchVersion") 'Revit.exe'
    if (-not (Test-Path -LiteralPath $revitExe)) {
        throw "Revit $LaunchVersion is not installed at $revitExe. The add-in was installed; launch a Revit by hand."
    }
    # Only a Debug build can take Hot Reload, and only if the runtime was told at startup that its
    # assemblies may be modified. A Revit started from the Start menu will not have it, and that
    # is fine -- it just cannot be hot-reloaded.
    $hotReload = (-not $fromRelease) -and ($Configuration -eq 'Debug')
    if ($hotReload) {
        Write-Host "Starting Revit $LaunchVersion with DOTNET_MODIFIABLE_ASSEMBLIES=debug. Attach a debugger to Revit.exe for Hot Reload of method bodies." -ForegroundColor Cyan
    }
    else {
        Write-Host "Starting Revit $LaunchVersion." -ForegroundColor Cyan
    }
    Write-Host 'If the Security - Unsigned Add-In dialog appears, answer Always Load and let Revit exit normally once, or the answer is not kept.' -ForegroundColor Yellow
    # $env: is process-wide, and this script runs in the caller's process, so the variable is set
    # only across the spawn and then restored -- a later `dotnet build` in the same shell must not
    # inherit it.
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
