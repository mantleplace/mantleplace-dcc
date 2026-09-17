<#
.SYNOPSIS
    Moves the consuming project's submodule checkout of this repository to a ref -- origin/main by
    default -- so the next editor launch compiles that commit.

.DESCRIPTION
    The Unreal plugin a local editor loads is the consuming project's submodule checkout, a single
    slot. This is the Unreal half of "deploy": fetch, then check out the ref, detached, and say
    what the slot now holds. Nothing in the consuming project's own tree is touched -- its pin is
    a commit in ITS history and moves only by its own pull request, after the change has merged
    here. Between a merge here and that pin bump, the consuming project's git status shows the
    submodule as modified, and that is the expected state, not a mistake.

    Detached on purpose. A branch checked out inside a submodule is where an orphan commit comes
    from: `git submodule update` later checks out the pin and the branch's commits become
    unreachable. Previewing a branch here is reading it, never editing it -- edits happen in a
    worktree of this repository, get pushed, and are previewed from origin.

    Refuses when the slot itself is dirty: a checkout over uncommitted changes is either a lost
    edit or a refused merge, and both are worse than stopping. (This is a refusal about the
    DESTINATION. What the source tree looks like is never a reason to refuse -- HPS-50.)

    Windows PowerShell 5.1 compatible, like every script in this repository.

.PARAMETER ConsumingProjectRoot
    The consuming Unreal project's root: the directory that holds Plugins/MantlePlaceDcc/.
    Defaults to $env:MANTLEPLACE_CONSUMING_PROJECT_ROOT.

.PARAMETER Ref
    What to check out. origin/main by default; origin/<branch> for a preview.

.EXAMPLE
    ./Refresh-UnrealInstall.ps1
    ./Refresh-UnrealInstall.ps1 -Ref origin/feat/my-change
#>
[CmdletBinding()]
param(
    [string]$ConsumingProjectRoot = $env:MANTLEPLACE_CONSUMING_PROJECT_ROOT,
    [string]$Ref = 'origin/main'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$unrealRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $unrealRoot
Import-Module (Join-Path $repoRoot 'tools/local-install/LocalInstall.psm1') -Force

if ([string]::IsNullOrWhiteSpace($ConsumingProjectRoot)) {
    throw 'No consuming project: set MANTLEPLACE_CONSUMING_PROJECT_ROOT or pass -ConsumingProjectRoot. Nothing was changed.'
}
$slot = Join-Path $ConsumingProjectRoot 'Plugins/MantlePlaceDcc'
if (-not (Test-Path -LiteralPath (Join-Path $slot '.git'))) {
    throw "No submodule checkout at $slot. Run git submodule update --init in $ConsumingProjectRoot first. Nothing was changed."
}

$state = Get-GitCheckoutState -Path $slot
if ($null -eq $state) {
    throw "$slot is not a readable git checkout. Nothing was changed."
}
if ($state.Dirty) {
    throw "$slot has uncommitted changes. Edits belong in a worktree of this repository, not in the consuming project's checkout; move or discard them, then run this again. Nothing was changed."
}

Write-Host "Fetching origin into $slot ..." -ForegroundColor Cyan
Invoke-Git -C $slot fetch origin --quiet | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "git fetch failed in $slot. Nothing was changed."
}

Invoke-Git -C $slot rev-parse --verify --quiet "$Ref^{commit}" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "$Ref is not a commit origin has. For a branch, push it first and name it origin/<branch>. Nothing was changed."
}

Invoke-Git -C $slot checkout --detach --quiet $Ref | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "git checkout $Ref failed in $slot."
}

$sha = Invoke-Git -C $slot rev-parse --short HEAD
Write-Host "Checked out $Ref at $sha, detached, in $slot" -ForegroundColor Green
if ($Ref -ne 'origin/main') {
    Write-Host 'This is a preview, not main. The slot returns to main when a session refreshes after a merge.' -ForegroundColor Yellow
}
Write-Host 'Restart the editor to compile it: headers and assets that changed under it are not picked up live, only .cpp bodies are (Live Coding).' -ForegroundColor Yellow
