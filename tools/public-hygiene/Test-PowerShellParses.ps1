<#
.SYNOPSIS
  Fails when a tracked PowerShell script does not parse.

.DESCRIPTION
  Runs the PowerShell parser over every `git ls-files '*.ps1'` and prints each error as
  file:line:column and its message. It executes nothing. The deliberately broken case under
  tools/public-hygiene/cases/ is excluded from the sweep and is checked by -Case.

  -Path   parse exactly these files instead of the tracked set.
  -Case   expect the parser to refuse every given file, naming a line; fail if one parses.
#>
[CmdletBinding()]
param(
    [string[]]$Path,
    [switch]$Case
)
$ErrorActionPreference = 'Stop'

if (-not $Path) {
    $root = (& git rev-parse --show-toplevel).Trim()
    $Path = & git -C $root ls-files '*.ps1' |
        Where-Object { $_ -notlike 'tools/public-hygiene/cases/*' } |
        ForEach-Object { Join-Path $root $_ }
}

$bad = 0
foreach ($file in $Path) {
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $file).Path, [ref]$null, [ref]$errors)
    if ($errors -and $errors.Count -gt 0) {
        foreach ($e in $errors) {
            Write-Host ("{0}:{1}:{2}: {3}" -f $file, $e.Extent.StartLineNumber, $e.Extent.StartColumnNumber, $e.Message)
        }
        if (-not $Case) { $bad++ }
    }
    elseif ($Case) {
        Write-Host "${file}: parses, but the case must not"
        $bad++
    }
}
Write-Host ("{0} file(s) checked, {1} failing" -f @($Path).Count, $bad)
if ($bad -gt 0) { exit 1 }
