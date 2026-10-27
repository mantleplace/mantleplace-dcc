<#
.SYNOPSIS
    Copies the Blender add-on from this tree into Blender's own extension folder, for every
    installed Blender from 4.2, and stamps what it copied.

.DESCRIPTION
    The add-on Blender loads is not this tree: it is a copy in the per-user extension repository,
    one slot per Blender version (HPS-50). This script replaces that copy with blender/mantleplace_blender/
    and writes MantlePlace.install.json into it: the commit, the branch, the worktree, whether the
    tree had uncommitted changes under blender/, and when. Check-BlenderInstall.ps1 reads the stamp
    back. A deploy from a dirty tree or a branch is recorded as one and never refused; the check
    script is where "unverified" and "preview" are said.

    After the first install, enable the add-on once in Blender: Edit, Preferences, Add-ons, search
    "Mantle Place". Later installs replace the files; restart Blender, or run Reload Scripts, to
    load them.

    Windows PowerShell 5.1 compatible, like every script in this repository.

.PARAMETER BlenderVersions
    Which Blender versions to install into, as their configuration folder names ("4.5", "5.2").
    Defaults to every folder from 4.2 up under -ConfigRoot.

.PARAMETER ConfigRoot
    Blender's per-user configuration root. Defaults to %APPDATA%\Blender Foundation\Blender.

.EXAMPLE
    ./Install-BlenderAddon.ps1
    ./Install-BlenderAddon.ps1 -BlenderVersions 5.2
#>
[CmdletBinding()]
param(
    [string[]]$BlenderVersions,
    [string]$ConfigRoot = (Join-Path $env:APPDATA 'Blender Foundation/Blender')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$blenderRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $blenderRoot
Import-Module (Join-Path $repoRoot 'tools/local-install/LocalInstall.psm1') -Force

$source = Join-Path $blenderRoot 'mantleplace_blender'
$slotName = 'mantleplace_blender'
$stampFileName = 'MantlePlace.install.json'

$manifestText = Get-Content -LiteralPath (Join-Path $source 'blender_manifest.toml') -Raw
$versionMatch = [regex]::Match($manifestText, '(?m)^version = "([^"]+)"')
$version = if ($versionMatch.Success) { $versionMatch.Groups[1].Value } else { 'unknown version' }

if (-not $BlenderVersions) {
    if (-not (Test-Path -LiteralPath $ConfigRoot)) {
        throw "No Blender configuration at $ConfigRoot. Start Blender once, or pass -ConfigRoot. Nothing was installed."
    }
    $BlenderVersions = @(Get-ChildItem -LiteralPath $ConfigRoot -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+$' -and ([version]($_.Name + '.0')) -ge [version]'4.2.0' } |
        ForEach-Object { $_.Name })
}
if (@($BlenderVersions).Count -eq 0) {
    throw "No Blender 4.2 or later under $ConfigRoot. Nothing was installed."
}

# What the stamp says about where this copy came from, read from git. git may be absent, and the
# script must still install.
$state = $null
if ($null -ne (Get-Command git -ErrorAction SilentlyContinue)) {
    $state = Get-GitCheckoutState -Path $blenderRoot -DirtyScope '.'
}

# Read once: one run is one install, and the check groups the version slots by their stamps.
$installedAt = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')

foreach ($blenderVersion in $BlenderVersions) {
    $repository = Join-Path $ConfigRoot "$blenderVersion/extensions/user_default"
    $destination = Join-Path $repository $slotName
    if (Test-Path -LiteralPath $destination) {
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath $source -Force | Where-Object { $_.Name -ne '__pycache__' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse -Force }
    Get-ChildItem -LiteralPath $destination -Directory -Recurse -Filter '__pycache__' |
        Remove-Item -Recurse -Force

    # The stamp. Schema 1, the shape the Revit stamp has, so one reader's habits fit both. Key
    # order is fixed so two stamps diff cleanly.
    $stamp = [ordered]@{
        schema      = 1
        source      = 'source-tree'
        version     = $version
        installedAt = $installedAt
        sha         = $(if ($null -ne $state) { $state.Sha } else { $null })
        dirty       = $(if ($null -ne $state) { [bool]$state.Dirty } else { $false })
        branch      = $(if ($null -ne $state) { $state.Branch } else { $null })
        sourceTree  = $(if ($null -ne $state) { $state.TopLevel } else { $null })
    }
    # Written without a BOM, by hand: Windows PowerShell's -Encoding utf8 adds one.
    [System.IO.File]::WriteAllText((Join-Path $destination $stampFileName), ($stamp | ConvertTo-Json),
        (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Installed Mantle Place $version for Blender $blenderVersion to $destination" -ForegroundColor Green
}

$provenance = if ($null -eq $state) { 'no git: the stamp names no commit' }
else {
    $branchText = if ($null -ne $state.Branch) { $state.Branch } else { 'a detached head' }
    $dirtyText = if ($state.Dirty) { ', with uncommitted changes under blender/' } else { '' }
    "$($state.Sha.Substring(0, [Math]::Min(7, $state.Sha.Length))) on $branchText$dirtyText"
}
Write-Host "  Stamped: $provenance"
Write-Host '  Enable it once in Edit > Preferences > Add-ons; after that, restart Blender or run Reload Scripts to load a new copy.'
