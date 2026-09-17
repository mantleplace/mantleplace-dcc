<#
.SYNOPSIS
    Says whether the Unreal plugin the consuming project's editor loads is current with
    origin/main, a preview of a branch, or stale -- one line, one exit code.

.DESCRIPTION
    The plugin a local Unreal editor compiles is not this tree. It is the consuming project's
    submodule checkout of this repository, mounted under that project's Plugins/ directory, and
    that checkout is a single slot: one commit, whatever branch or worktree it was made on. This
    reads the slot's commit, branch and dirty state straight from git and compares against
    origin/main, counting only commits that touch unreal/. The verdict contract is
    tools/local-install/README.md.

    The consuming project is private and lives wherever it lives, so this script is told where:
    -ConsumingProjectRoot, or the MANTLEPLACE_CONSUMING_PROJECT_ROOT environment variable. Neither
    set means there is nothing to check on this machine, which is reported quietly with exit 0 so
    a stranger's clone sees nothing red.

    Windows PowerShell 5.1 compatible, like every script in this repository.

.PARAMETER ConsumingProjectRoot
    The consuming Unreal project's root: the directory that holds Plugins/MantlePlaceDcc/.
    Defaults to $env:MANTLEPLACE_CONSUMING_PROJECT_ROOT.

.PARAMETER NoFetch
    Compare against the origin/main the submodule checkout already has, without fetching first.

.EXAMPLE
    ./Check-UnrealInstall.ps1
    ./Check-UnrealInstall.ps1 -ConsumingProjectRoot <path to the Unreal project root>
#>
[CmdletBinding()]
param(
    [string]$ConsumingProjectRoot = $env:MANTLEPLACE_CONSUMING_PROJECT_ROOT,
    [switch]$NoFetch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$unrealRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $unrealRoot
Import-Module (Join-Path $repoRoot 'tools/local-install/LocalInstall.psm1') -Force

$remedy = 'unreal/tools/Refresh-UnrealInstall.ps1'

if ([string]::IsNullOrWhiteSpace($ConsumingProjectRoot)) {
    $verdict = Get-LocalInstallVerdict -HostName Unreal -Configured $false
    Write-Output ($verdict.Line + '; set MANTLEPLACE_CONSUMING_PROJECT_ROOT or pass -ConsumingProjectRoot to point at the consuming Unreal project')
    exit $verdict.ExitCode
}

if (-not (Test-Path -LiteralPath $ConsumingProjectRoot)) {
    $verdict = Get-LocalInstallVerdict -HostName Unreal -Installed $false
    Write-Output ($verdict.Line + " - no consuming project at $ConsumingProjectRoot; the configured root does not exist")
    exit $verdict.ExitCode
}

$slot = Join-Path $ConsumingProjectRoot 'Plugins/MantlePlaceDcc'
$state = $null
if (Test-Path -LiteralPath (Join-Path $slot '.git')) {
    $state = Get-GitCheckoutState -Path $slot
}
if ($null -eq $state) {
    # No .git file or folder means the submodule was never initialised: the editor has no plugin.
    $verdict = Get-LocalInstallVerdict -HostName Unreal -Installed $false
    Write-Output ($verdict.Line + " - no submodule checkout at $slot; run git submodule update --init there")
    exit $verdict.ExitCode
}

# A detached head AT origin/main is what a fresh pin looks like, and is "main" for this purpose:
# the consuming project pins commits, never branches, so a detached checkout on main is the
# normal, current state rather than a preview.
$branch = $state.Branch
$facts = Get-GitInstallFacts -RepoRoot $slot -HostPath 'unreal/' -Sha $state.Sha -Branch $branch -NoFetch:$NoFetch
if ($null -eq $branch -and $facts.OnMain) { $branch = 'main' }

$verdict = Get-LocalInstallVerdict -HostName Unreal -Sha $state.Sha -ShaKnown $facts.ShaKnown -Branch $branch -Dirty $state.Dirty `
    -OnMain $facts.OnMain -BehindMain $facts.BehindMain `
    -BranchExists $facts.BranchExists -BehindBranch $facts.BehindBranch -BranchMerged $facts.BranchMerged `
    -HostPath 'unreal/' -Remedy $remedy -SourceTree $slot
if ($facts.FetchFailed) {
    $verdict.Line = $verdict.Line + ' (fetch failed; compared against the origin/main the checkout last saw)'
}
Write-Output $verdict.Line
exit $verdict.ExitCode
