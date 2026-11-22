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

$state = $null
if ($null -ne (Get-Command git -ErrorAction SilentlyContinue)) {
    $state = Get-GitCheckoutState -Path $blenderRoot -DirtyScope '.'
}

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
