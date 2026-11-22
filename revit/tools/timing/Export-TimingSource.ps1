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
$tarFile = Join-Path $work "src\$short.tar"
New-Item -ItemType Directory -Force (Split-Path -Parent $tarFile) | Out-Null
$paths = @('revit')
& git -C $Repo cat-file -e "${sha}:shared" 2>$null
if ($LASTEXITCODE -eq 0) { $paths += 'shared' }
& git -C $Repo archive --format=tar -o $tarFile $sha @paths
if ($LASTEXITCODE -ne 0) { Remove-Item -LiteralPath $tarFile -Force -ErrorAction SilentlyContinue; throw "git archive of $sha failed" }
New-Item -ItemType Directory -Force $dest | Out-Null
$tarExe = Join-Path $env:SystemRoot 'System32\tar.exe'
if (-not (Test-Path -LiteralPath $tarExe)) { $tarExe = 'tar' }
& $tarExe -x -f $tarFile -C $dest
$tarExit = $LASTEXITCODE
Remove-Item -LiteralPath $tarFile -Force -ErrorAction SilentlyContinue
if ($tarExit -ne 0 -or -not (Test-Path (Join-Path $dest 'revit\src'))) { throw "extracting the archive of $sha into $dest failed" }
Set-Content -Path (Join-Path $dest 'COMMIT') -Value $sha
Write-Host "exported $sha to $dest"
$dest
