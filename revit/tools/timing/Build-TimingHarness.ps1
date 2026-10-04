# Builds the timing harness for one TAG from one SOURCE ROOT.
#
#   ./Build-TimingHarness.ps1 -Tag main-d4f3453 -SourceRoot (./Export-TimingSource.ps1 -Commit d4f3453)
#
# SourceRoot is a folder that contains revit\src\MantlePlace.Revit.{Core,Client,Addin}: a `git archive`
# export from Export-TimingSource.ps1 (preferred: it carries the commit in a file called COMMIT at its root), or a
# git worktree ("-dirty" is appended to the commit when revit/ has changes). The plugin's three source trees and
# the harness compile into ONE assembly named MantlePlace.Scratch.Timing.<tag>, into <work root>\builds\<tag>\out\.
# Nothing is copied to, or read from, the real add-in's install slot.
#
# A tag names one source: building an existing tag again with a different SourceRoot or commit is
# refused (two agents must never share a tag), unless -Force. Invoke-TimingRun.ps1 calls this when the tag has
# not been built yet, so calling it by hand is only needed to build ahead of time.
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$SourceRoot,
    [string]$SourceCommit = '',
    # The plugin compiles against Revit 2025's API whatever year it runs in; the default is Revit's standard install folder.
    [string]$RevitApiDir = (Join-Path $env:ProgramFiles 'Autodesk\Revit 2025'),
    [string]$WorkRoot = '',
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
. (Join-Path $root 'lib\timing-root.ps1')
$work = Resolve-TimingRoot $WorkRoot
if ($Tag -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,30}$') { throw "Tag '$Tag' must match ^[A-Za-z0-9][A-Za-z0-9_-]{0,30}$" }
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path.TrimEnd('\')
$tree = Join-Path $SourceRoot 'revit\src'
foreach ($p in 'MantlePlace.Revit.Core', 'MantlePlace.Revit.Client', 'MantlePlace.Revit.Addin') {
    if (-not (Test-Path (Join-Path $tree $p))) { throw "$SourceRoot has no revit\src\$p" }
}
if (-not (Test-Path (Join-Path $RevitApiDir 'RevitAPI.dll'))) { throw "no RevitAPI.dll under $RevitApiDir (the plugin compiles against Revit 2025's API)" }

# The commit the sources are: a COMMIT file (git archive exports have no .git), else git, else the parameter.
$commit = $SourceCommit
$dirty = $false
$commitFile = Join-Path $SourceRoot 'COMMIT'
if (-not $commit -and (Test-Path $commitFile)) { $commit = (Get-Content $commitFile -TotalCount 1).Trim() }
if (-not $commit -and (Test-Path (Join-Path $SourceRoot '.git'))) {
    $commit = (& git -C $SourceRoot rev-parse HEAD 2>$null | Select-Object -First 1)
    $status = (& git -C $SourceRoot status --porcelain --untracked-files=all -- revit 2>$null)
    if ($status) { $dirty = $true }
}
if (-not $commit) { $commit = 'unknown' }
$commitLabel = if ($dirty) { "$commit-dirty" } else { $commit }

$buildDir = Join-Path $work "builds\$Tag"
$outDir = Join-Path $buildDir 'out'
$jsonPath = Join-Path $buildDir 'build.json'
if (Test-Path $jsonPath) {
    $old = Get-Content $jsonPath -Raw | ConvertFrom-Json
    if (-not $Force) {
        if ($old.sourceRoot -eq $SourceRoot -and $old.sourceCommit -eq $commitLabel) { Write-Host "tag '$Tag' already built from $commitLabel"; return }
        throw "tag '$Tag' was built from $($old.sourceRoot) @ $($old.sourceCommit); this is $SourceRoot @ $commitLabel. Use another tag (or -Force if the tag is yours alone)."
    }
}
New-Item -ItemType Directory -Force $buildDir | Out-Null
$proj = Join-Path $buildDir 'proj'
if (Test-Path $proj) { Remove-Item -Recurse -Force $proj }
New-Item -ItemType Directory -Force $proj | Out-Null
Copy-Item (Join-Path $root 'Harness\TimingHarness.csproj'), (Join-Path $root 'Harness\TimingHarness.cs') $proj

# The tag is compiled into the assembly as a constant. The harness gates on it: it does nothing at all in a
# Revit whose MANTLEPLACE_TIMING_TAG differs (a same-year Revit that picked up this run's temporary manifest), so
# it must be exactly the tag this build is for. Verified below, by reading it back out of the finished DLL.
@"
// Written by Build-TimingHarness.ps1 for tag '$Tag'. Not a source file: do not edit.
namespace MantlePlace.Scratch.Timing;

internal static class BuildInfo
{
    public const string Tag = "$Tag";
}
"@ | Set-Content -Encoding UTF8 (Join-Path $proj 'BuildInfo.g.cs')

$assemblyName = 'MantlePlace.Scratch.Timing.' + ($Tag -replace '-', '_')
$log = Join-Path $buildDir 'build.log'
Write-Host "building tag '$Tag' ($assemblyName) from $SourceRoot @ $commitLabel"
& dotnet build (Join-Path $proj 'TimingHarness.csproj') -c Release -o $outDir --nologo -v minimal `
    "-p:HarnessTag=$Tag" "-p:HarnessAssemblyName=$assemblyName" "-p:HarnessSourceCommit=$commitLabel" `
    "-p:Tree=$tree" "-p:RevitApiDir=$RevitApiDir" 2>&1 | Tee-Object -FilePath $log | Select-Object -Last 25
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE); see $log" }
$dll = Join-Path $outDir "$assemblyName.dll"
if (-not (Test-Path $dll)) { throw "build reported success but $dll is missing" }

# The constant the harness gates on is the tag this build is for. Loaded from bytes into a throwaway load context, so
# nothing stays locked and nothing of it stays loaded.
$compiledTag = ''
if ($PSVersionTable.PSEdition -eq 'Core') {
    $ctx = [Runtime.Loader.AssemblyLoadContext]::new('timing-verify', $true)
    try {
        $built = $ctx.LoadFromStream([IO.MemoryStream]::new([IO.File]::ReadAllBytes($dll)))
        $compiledTag = "$($built.GetType('MantlePlace.Scratch.Timing.BuildInfo', $true).GetField('Tag').GetRawConstantValue())"
    }
    catch { throw "could not read BuildInfo.Tag back out of $dll ($($_.Exception.Message.Split("`n")[0])); the harness's guard is unverified, so this build is not used" }
    finally { $ctx.Unload() }
    if ($compiledTag -ne $Tag) { throw "$dll carries compiled tag '$compiledTag', not '$Tag'; the harness's guard would be wrong, so this build is not used" }
    Write-Host "compiled tag verified: '$compiledTag'"
}
else { Write-Warning 'not PowerShell 7: the compiled tag was not read back out of the DLL (build.json compiledTag is left empty)' }

[ordered]@{
    tag          = $Tag
    assemblyName = $assemblyName
    compiledTag  = $compiledTag
    sourceRoot   = $SourceRoot
    sourceCommit = $commitLabel
    revitApiDir  = $RevitApiDir
    builtAt      = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss')
    dllSha256    = (Get-FileHash $dll -Algorithm SHA256).Hash
    clientId     = ([guid]::new([Security.Cryptography.MD5]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes("MantlePlaceTiming:$Tag")))).ToString()
} | ConvertTo-Json | Set-Content -Encoding UTF8 $jsonPath
Write-Host "built $dll"
