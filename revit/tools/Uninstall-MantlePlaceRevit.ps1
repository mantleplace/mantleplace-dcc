<#
.SYNOPSIS
    Removes the Mantle Place Revit plugin from the per-user Revit add-ins folders, and optionally
    the local data it accumulated.

.DESCRIPTION
    The mirror of Deploy-MantlePlaceRevit.ps1. It ships in the release zip verbatim, beside it, and
    is reached through Uninstall.cmd for the same reason Install.cmd exists: a .ps1 in a downloaded
    zip is not something a curator can run.

    What it removes, always: MantlePlace.addin and every other MantlePlace.* file sitting directly in
    a year folder under %APPDATA%\Autodesk\Revit\Addins. That is the set the installer writes -- the
    manifest, the assemblies, their .pdb and .json files, and MantlePlace.install.json. Every year
    folder that exists is searched, not just 2025 to 2027, so a folder for a year this build no
    longer targets is cleaned too. Nothing else in a year folder is touched, and the folder itself is
    left in place: it is Revit's, and other add-ins live in it.

    What it removes only if asked, and asks before doing: the plugin's own data under
    %LOCALAPPDATA%\MantlePlace -- the bundle cache (bundles), the planting family files (families)
    and the two machine records (announced-orders.json, interrupted-prepares.json).

    What it never removes: %LOCALAPPDATA%\MantlePlace\auth, the stored sign-in credential. The
    Unreal plugin reads the same credential, so deleting it here would sign a curator out of a
    product they are not uninstalling. To sign out first, use Mantle Place > Account > Sign Out in
    Revit before uninstalling. config.json is left too: nothing installs it, a person wrote it, and
    it holds overrides they may want back on a reinstall.

    Refuses, before touching anything, while Revit is running: a loaded plugin is file-locked, so a
    delete would remove some files and fail on the rest, leaving a half-removed plugin.

    Windows PowerShell 5.1 compatible, deliberately, for the reasons Deploy-MantlePlaceRevit.ps1
    gives. No ternaries, no null-coalescing, no pipeline chain operators. ASCII only: 5.1 reads a
    BOM-less file as ANSI.

.PARAMETER RemoveLocalData
    Delete the local data without asking. For unattended use.

.PARAMETER KeepLocalData
    Keep the local data without asking. For unattended use.

.PARAMETER AddinsRoot
    The folder holding the per-year add-in folders. Defaults to %APPDATA%\Autodesk\Revit\Addins.
    Exists so the script can be exercised against a scratch tree.

.PARAMETER LocalDataRoot
    The folder holding the plugin's local data. Defaults to %LOCALAPPDATA%\MantlePlace. Exists so
    the script can be exercised against a scratch tree.

.EXAMPLE
    ./Uninstall-MantlePlaceRevit.ps1
    ./Uninstall-MantlePlaceRevit.ps1 -KeepLocalData
#>
[CmdletBinding()]
param(
    [switch]$RemoveLocalData,

    [switch]$KeepLocalData,

    [string]$AddinsRoot,

    [string]$LocalDataRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($RemoveLocalData -and $KeepLocalData) {
    throw '-RemoveLocalData and -KeepLocalData contradict each other. Pass at most one. Nothing was removed.'
}

if ([string]::IsNullOrWhiteSpace($AddinsRoot)) {
    $AddinsRoot = Join-Path $env:APPDATA 'Autodesk/Revit/Addins'
}
if ([string]::IsNullOrWhiteSpace($LocalDataRoot)) {
    $LocalDataRoot = Join-Path $env:LOCALAPPDATA 'MantlePlace'
}

# The names under the local data root that the plugin writes and this script may delete. Kept as an
# explicit list rather than "everything but auth": a folder this list does not name is one the
# script has no business deleting, and a future credential-adjacent file stays safe by default.
# Sources: BundleCacheLayout (bundles), FamilyFileStore (families), AnnouncedOrderStore and
# InterruptedPrepareStore via MachineRecordFile (the two .json records).
$localDataNames = @('bundles', 'families', 'announced-orders.json', 'interrupted-prepares.json')

# ---------------------------------------------------------------------------
# Refuse before removing anything if Revit is open. See the description.
# ---------------------------------------------------------------------------
$running = @(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    throw 'Revit is running, so its plugin files are locked and only some of them would be removed. Close Revit and run this again. Nothing was removed.'
}

$failures = 0
$removedFiles = 0

if (Test-Path -LiteralPath $AddinsRoot) {
    $yearFolders = @(Get-ChildItem -LiteralPath $AddinsRoot -Directory)
}
else {
    $yearFolders = @()
}

foreach ($yearFolder in $yearFolders) {
    $files = @(Get-ChildItem -LiteralPath $yearFolder.FullName -File | Where-Object { $_.Name -like 'MantlePlace.*' })

    foreach ($file in $files) {
        try {
            Remove-Item -LiteralPath $file.FullName -Force
            $removedFiles++
            Write-Host "Removed $($file.FullName)" -ForegroundColor Green
        }
        catch {
            $failures++
            Write-Host "Could not remove $($file.FullName): $($_.Exception.Message)" -ForegroundColor Red
        }
    }
}

if ($removedFiles -eq 0 -and $failures -eq 0) {
    Write-Host "No Mantle Place plugin files were found under $AddinsRoot."
}
else {
    Write-Host "Removed $removedFiles plugin file(s)."
}

# ---------------------------------------------------------------------------
# Local data. Asked about, never assumed: the bundle cache can be gigabytes of downloads a curator
# may want to keep, and re-fetching them costs time and a download each.
# ---------------------------------------------------------------------------
$localData = @()
foreach ($name in $localDataNames) {
    $path = Join-Path $LocalDataRoot $name
    if (Test-Path -LiteralPath $path) {
        $localData += $path
    }
}

Write-Host ''
if ($localData.Count -eq 0) {
    Write-Host "No Mantle Place local data was found under $LocalDataRoot."
}
else {
    Write-Host 'The plugin also keeps data of its own:' -ForegroundColor Cyan
    foreach ($path in $localData) {
        Write-Host "  $path"
    }

    if ($RemoveLocalData) {
        $remove = $true
    }
    elseif ($KeepLocalData) {
        $remove = $false
    }
    else {
        $answer = Read-Host 'Delete it too? Bundles you downloaded will have to be downloaded again. [y/N]'
        # Read-Host returns null, not '', when there is no console to read (Uninstall.cmd < nul).
        $remove = ("$answer".Trim() -match '^(y|yes)$')
    }

    if ($remove) {
        foreach ($path in $localData) {
            try {
                Remove-Item -LiteralPath $path -Recurse -Force
                Write-Host "Removed $path" -ForegroundColor Green
            }
            catch {
                $failures++
                Write-Host "Could not remove ${path}: $($_.Exception.Message)" -ForegroundColor Red
            }
        }
    }
    else {
        Write-Host 'Kept the local data.'
    }
}

Write-Host ''
Write-Host 'Your Mantle Place sign-in was not removed. The Unreal plugin uses the same one, so it stays.'
Write-Host "It lives in $(Join-Path $LocalDataRoot 'auth'); sign out from Mantle Place > Account in Revit"
Write-Host 'before uninstalling if you want it forgotten.'

if ($failures -gt 0) {
    throw "$failures item(s) could not be removed. Close anything holding them open and run this again."
}
