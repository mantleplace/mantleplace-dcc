# Exports revit\ of one commit into <work root>\src\<short-sha>\ (with a COMMIT file), for -SourceRoot.
# Read-only against the repository: `git archive` never touches a working tree, so a checkout other sessions
# edit is never used as a source.
#
#   ./Export-TimingSource.ps1 -Commit d4f3453
#   ./Export-TimingSource.ps1 -Commit origin/some-branch -Repo <a clone of mantleplace-dcc>
#
# -Repo defaults to the repository this script lives in. The work root is -WorkRoot, else
# MANTLEPLACE_TIMING_ROOT, else %LOCALAPPDATA%\MantlePlaceTiming (lib\timing-root.ps1).
# Writes the export's path as its only pipeline output.
param(
    [Parameter(Mandatory)][string]$Commit,
    [string]$Repo = '',
    [string]$WorkRoot = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\timing-root.ps1')
$work = Resolve-TimingRoot $WorkRoot
if (-not $Repo) { $Repo = "$(& git -C $PSScriptRoot rev-parse --show-toplevel)".Trim() }
$remotes = (& git -C $Repo remote -v) -join "`n"
if ($remotes -notmatch 'mantleplace-dcc') { throw "$Repo does not look like mantleplace-dcc (git remote -v: $remotes)" }
$sha = & git -C $Repo rev-parse --verify --quiet "$Commit^{commit}"
if ($LASTEXITCODE -ne 0 -or -not $sha) { throw "no commit '$Commit' in $Repo" }
$sha = "$sha".Trim()
$short = $sha.Substring(0, 7)
$dest = Join-Path $work "src\$short"
if (Test-Path (Join-Path $dest 'COMMIT')) { Write-Host "$dest already exists"; return $dest }
# Archive to a file first, so a failed `git archive` is its own exit code rather than hidden behind tar's, and the
# COMMIT file (which marks the export complete) is written only after both succeeded.
$tarFile = Join-Path $work "src\$short.tar"
& git -C $Repo archive --format=tar -o $tarFile $sha revit
if ($LASTEXITCODE -ne 0) { Remove-Item -LiteralPath $tarFile -Force -ErrorAction SilentlyContinue; throw "git archive of $sha failed" }
New-Item -ItemType Directory -Force $dest | Out-Null
& tar -x -f $tarFile -C $dest
$tarExit = $LASTEXITCODE
Remove-Item -LiteralPath $tarFile -Force -ErrorAction SilentlyContinue
if ($tarExit -ne 0 -or -not (Test-Path (Join-Path $dest 'revit\src'))) { throw "extracting the archive of $sha into $dest failed" }
Set-Content -Path (Join-Path $dest 'COMMIT') -Value $sha
Write-Host "exported $sha to $dest"
$dest
