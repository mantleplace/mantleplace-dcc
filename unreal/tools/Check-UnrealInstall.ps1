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
    $verdict = Get-LocalInstallVerdict -HostName Unreal -Installed $false
    Write-Output ($verdict.Line + " - no submodule checkout at $slot; run git submodule update --init there")
    exit $verdict.ExitCode
}

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
