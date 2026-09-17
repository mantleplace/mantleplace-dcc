<#
.SYNOPSIS
    Says whether the Revit add-in installed on this machine is current with origin/main, a preview
    of a branch, or stale -- one line, one exit code.

.DESCRIPTION
    Reads the stamp Deploy-MantlePlaceRevit.ps1 wrote beside the manifest in each per-version
    add-ins folder, and compares the commit it names against origin/main, counting only commits
    that touch revit/. The verdict contract is tools/local-install/README.md; the sentences come
    from LocalInstall.psm1 and nowhere else.

    All three add-ins folders are read. The deploy script writes them identically, so one line
    covers them; if they ever disagree, one line per version says which is which.

    Windows PowerShell 5.1 compatible, like the deploy script and for the same reason.

.PARAMETER RevitVersions
    Which per-user add-in folders to read. Defaults to every supported host.

.PARAMETER NoFetch
    Compare against the origin/main this clone already has, without fetching first.

.EXAMPLE
    ./Check-RevitInstall.ps1
#>
[CmdletBinding()]
param(
    [string[]]$RevitVersions = @('2025', '2026', '2027'),
    [switch]$NoFetch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$revitRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $revitRoot
Import-Module (Join-Path $repoRoot 'tools/local-install/LocalInstall.psm1') -Force

$remedy = 'revit/tools/Deploy-MantlePlaceRevit.ps1'
$addinsRoot = Join-Path $env:APPDATA 'Autodesk/Revit/Addins'

# One verdict per distinct install, keyed by what the stamp says. Normally one.
$buckets = [ordered]@{}

function Add-ToBucket {
    param([string]$Key, [string]$RevitVersion, [scriptblock]$Verdict)
    if (-not $buckets.Contains($Key)) {
        $buckets[$Key] = [pscustomobject]@{ Verdict = (& $Verdict); Versions = @() }
    }
    $buckets[$Key].Versions += $RevitVersion
}

function Read-Stamp {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        $stamp = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        return $null
    }
    if ($null -eq $stamp -or -not ($stamp.PSObject.Properties.Name -contains 'source')) { return $null }
    return $stamp
}

function Get-StampField {
    param($Stamp, [string]$Name)
    if ($Stamp.PSObject.Properties.Name -contains $Name) { return $Stamp.$Name }
    return $null
}

$fetched = $false
foreach ($revitVersion in $RevitVersions) {
    $folder = Join-Path $addinsRoot $revitVersion

    if (-not (Test-Path -LiteralPath (Join-Path $folder 'MantlePlace.Revit.Addin.dll'))) {
        Add-ToBucket 'not-installed' $revitVersion { Get-LocalInstallVerdict -HostName Revit -Installed $false }
        continue
    }

    $stamp = Read-Stamp (Join-Path $folder 'MantlePlace.install.json')
    if ($null -eq $stamp) {
        Add-ToBucket 'no-stamp' $revitVersion { Get-LocalInstallVerdict -HostName Revit -HasStamp $false -Remedy $remedy }
        continue
    }

    $installedAt = [string](Get-StampField $stamp 'installedAt')
    if ($stamp.source -ne 'source-tree') {
        Add-ToBucket "release|$installedAt" $revitVersion {
            Get-LocalInstallVerdict -HostName Revit -FromSourceTree $false -InstalledAt $installedAt -Remedy $remedy
        }
        continue
    }

    $sha = [string](Get-StampField $stamp 'sha')
    $branch = Get-StampField $stamp 'branch'
    $dirty = [bool](Get-StampField $stamp 'dirty')
    $sourceTree = Get-StampField $stamp 'sourceTree'
    $key = "source|$sha|$branch|$dirty|$installedAt"
    if ($buckets.Contains($key)) {
        $buckets[$key].Versions += $revitVersion
        continue
    }

    $facts = Get-GitInstallFacts -RepoRoot $repoRoot -HostPath 'revit/' -Sha $sha -Branch $branch -NoFetch:($NoFetch -or $fetched)
    $fetched = $true
    Add-ToBucket $key $revitVersion {
        $verdict = Get-LocalInstallVerdict -HostName Revit -Sha $sha -ShaKnown $facts.ShaKnown -Branch $branch -Dirty $dirty `
            -OnMain $facts.OnMain -BehindMain $facts.BehindMain `
            -BranchExists $facts.BranchExists -BehindBranch $facts.BehindBranch -BranchMerged $facts.BranchMerged `
            -HostPath 'revit/' -Remedy $remedy -InstalledAt $installedAt -SourceTree $sourceTree
        if ($facts.FetchFailed) {
            $verdict.Line = $verdict.Line + ' (fetch failed; compared against the origin/main this clone last saw)'
        }
        $verdict
    }
}

$exitCode = 0
foreach ($bucket in $buckets.Values) {
    $line = $bucket.Verdict.Line
    if ($buckets.Count -gt 1) {
        $line = "$line [Revit $($bucket.Versions -join ', ')]"
    }
    Write-Output $line
    if ($bucket.Verdict.ExitCode -ne 0) { $exitCode = 1 }
}
if ($buckets.Count -gt 1) {
    Write-Output 'Revit: the add-ins folders disagree with each other. Run the deploy script once to make them one install.'
    $exitCode = 1
}
exit $exitCode
