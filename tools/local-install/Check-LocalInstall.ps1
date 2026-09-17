<#
.SYNOPSIS
    Runs every host's Check-<Host>Install.ps1 and prints the table: one line per host, and an
    exit code that is nonzero if any host is stale or unverified.

.DESCRIPTION
    The session-start check. Root CLAUDE.md asks for this to run before work begins, so a session
    knows whether the plugin it is about to see in a host is the tree it is about to edit. Each
    host owns its own script under <host>/tools/ and the contract they share is stated in
    LocalInstall.psm1; this file only enumerates them. A new host adds its script and one line to
    the list below.

    Windows PowerShell 5.1 compatible, like every script in this repository.

.PARAMETER NoFetch
    Passed through: compare against what each checkout already has, without fetching.

.EXAMPLE
    ./tools/local-install/Check-LocalInstall.ps1
#>
[CmdletBinding()]
param(
    [switch]$NoFetch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# One entry per host. Order is the order the lines print.
$hosts = @(
    'revit/tools/Check-RevitInstall.ps1',
    'unreal/tools/Check-UnrealInstall.ps1'
)

$exitCode = 0
foreach ($relative in $hosts) {
    $script = Join-Path $repoRoot $relative
    if (-not (Test-Path -LiteralPath $script)) {
        Write-Output "$relative is listed here and does not exist"
        $exitCode = 1
        continue
    }
    $output = & $script -NoFetch:$NoFetch 2>&1
    foreach ($line in @($output)) { Write-Output ([string]$line) }
    if ($LASTEXITCODE -ne 0) { $exitCode = 1 }
}
exit $exitCode
