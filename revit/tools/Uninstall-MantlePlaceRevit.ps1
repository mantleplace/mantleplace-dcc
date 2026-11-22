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

$localDataNames = @('bundles', 'families', 'announced-orders.json', 'interrupted-prepares.json')

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
