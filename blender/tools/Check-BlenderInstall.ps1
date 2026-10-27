<#
.SYNOPSIS
    Says whether the Blender plugin installed on this machine is current with origin/main, a
    preview of a branch, or stale -- one line, one exit code.

.DESCRIPTION
    Reads the stamp Install-BlenderAddon.ps1 wrote into the plugin's folder in each Blender
    version's extension repository, and compares the commit it names against origin/main, counting
    only commits that touch blender/. The verdict contract is tools/local-install/README.md; the
    sentences come from LocalInstall.psm1 and nowhere else.

    Only the Blender versions that hold the plugin are read: a Blender used for something else is
    not a slot. No Blender configuration at all is "not configured", quiet with exit 0, so a
    stranger's clone sees nothing red.

    Windows PowerShell 5.1 compatible, like every script in this repository.

.PARAMETER ConfigRoot
    Blender's per-user configuration root. Defaults to %APPDATA%\Blender Foundation\Blender.

.PARAMETER NoFetch
    Compare against the origin/main this clone already has, without fetching first.

.EXAMPLE
    ./Check-BlenderInstall.ps1
#>
[CmdletBinding()]
param(
    [string]$ConfigRoot = (Join-Path $env:APPDATA 'Blender Foundation/Blender'),
    [switch]$NoFetch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$blenderRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $blenderRoot
Import-Module (Join-Path $repoRoot 'tools/local-install/LocalInstall.psm1') -Force

$remedy = 'blender/tools/Install-BlenderAddon.ps1'

if (-not (Test-Path -LiteralPath $ConfigRoot)) {
    $verdict = Get-LocalInstallVerdict -HostName Blender -Configured $false
    Write-Output $verdict.Line
    exit $verdict.ExitCode
}

$slots = @(Get-ChildItem -LiteralPath $ConfigRoot -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+$' } |
    ForEach-Object {
        [pscustomobject]@{
            Version = $_.Name
            Path    = Join-Path $_.FullName 'extensions/user_default/mantleplace_blender'
        }
    } |
    Where-Object { Test-Path -LiteralPath $_.Path })

if ($slots.Count -eq 0) {
    $verdict = Get-LocalInstallVerdict -HostName Blender -Installed $false
    Write-Output ($verdict.Line + " - no Blender under $ConfigRoot holds the plugin; run $remedy")
    exit $verdict.ExitCode
}

function Get-StampField {
    param($Stamp, [string]$Name)
    if ($Stamp.PSObject.Properties.Name -contains $Name) { return $Stamp.$Name }
    return $null
}

# One verdict per distinct install, keyed by what the stamp says. Normally one.
$buckets = [ordered]@{}
$fetched = $false
foreach ($slot in $slots) {
    $stamp = $null
    $stampPath = Join-Path $slot.Path 'MantlePlace.install.json'
    if (Test-Path -LiteralPath $stampPath) {
        try { $stamp = Get-Content -LiteralPath $stampPath -Raw | ConvertFrom-Json } catch { $stamp = $null }
    }
    if ($null -eq $stamp -or $null -eq (Get-StampField $stamp 'source')) {
        $key = 'no-stamp'
        if (-not $buckets.Contains($key)) {
            $buckets[$key] = [pscustomobject]@{
                Verdict  = (Get-LocalInstallVerdict -HostName Blender -HasStamp $false -Remedy $remedy)
                Versions = @()
            }
        }
        $buckets[$key].Versions += $slot.Version
        continue
    }

    $installedAt = [string](Get-StampField $stamp 'installedAt')
    $sha = [string](Get-StampField $stamp 'sha')
    $branch = Get-StampField $stamp 'branch'
    $dirty = [bool](Get-StampField $stamp 'dirty')
    $sourceTree = Get-StampField $stamp 'sourceTree'
    $key = "source|$sha|$branch|$dirty|$installedAt"
    if (-not $buckets.Contains($key)) {
        $facts = Get-GitInstallFacts -RepoRoot $repoRoot -HostPath 'blender/' -Sha $sha -Branch $branch -NoFetch:($NoFetch -or $fetched)
        $fetched = $true
        $verdict = Get-LocalInstallVerdict -HostName Blender -Sha $sha -ShaKnown $facts.ShaKnown -Branch $branch -Dirty $dirty `
            -OnMain $facts.OnMain -BehindMain $facts.BehindMain `
            -BranchExists $facts.BranchExists -BehindBranch $facts.BehindBranch -BranchMerged $facts.BranchMerged `
            -HostPath 'blender/' -Remedy $remedy -InstalledAt $installedAt -SourceTree $sourceTree
        if ($facts.FetchFailed) {
            $verdict.Line = $verdict.Line + ' (fetch failed; compared against the origin/main this clone last saw)'
        }
        $buckets[$key] = [pscustomobject]@{ Verdict = $verdict; Versions = @() }
    }
    $buckets[$key].Versions += $slot.Version
}

$exitCode = 0
foreach ($bucket in $buckets.Values) {
    $line = $bucket.Verdict.Line
    if ($buckets.Count -gt 1) {
        $line = "$line [Blender $($bucket.Versions -join ', ')]"
    }
    Write-Output $line
    if ($bucket.Verdict.ExitCode -ne 0) { $exitCode = 1 }
}
if ($buckets.Count -gt 1) {
    Write-Output 'Blender: the version slots disagree with each other. Run the install script once to make them one install.'
    $exitCode = 1
}
exit $exitCode
