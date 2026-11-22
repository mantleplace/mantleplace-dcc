[CmdletBinding()]
param(
    [switch]$NoFetch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

$hosts = @(
    'revit/tools/Check-RevitInstall.ps1',
    'unreal/tools/Check-UnrealInstall.ps1',
    'blender/tools/Check-BlenderInstall.ps1'
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
