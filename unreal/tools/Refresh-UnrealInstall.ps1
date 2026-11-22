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
