param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$SourceRoot,
    [string]$SourceCommit = '',
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
if (-not (Test-Path (Join-Path $tree 'MantlePlace.Revit.Addin'))) { throw "$SourceRoot has no revit\src\MantlePlace.Revit.Addin" }
$shared = Join-Path $SourceRoot 'shared\src'
if (Test-Path (Join-Path $shared 'MantlePlace.Core')) {
    $coreDir = Join-Path $shared 'MantlePlace.Core'; $clientDir = Join-Path $shared 'MantlePlace.Client'; $platformApiDir = Join-Path $shared 'MantlePlace.PlatformApi'
} else {
    $coreDir = Join-Path $tree 'MantlePlace.Revit.Core'; $clientDir = Join-Path $tree 'MantlePlace.Revit.Client'; $platformApiDir = Join-Path $tree 'MantlePlace.PlatformApi'
}
foreach ($p in $coreDir, $clientDir) {
    if (-not (Test-Path $p)) { throw "$SourceRoot has no $p" }
}
if (-not (Test-Path $platformApiDir)) { $platformApiDir = '' }
$wpfDir = Join-Path $shared 'MantlePlace.Wpf'
if (-not (Test-Path $wpfDir)) { $wpfDir = '' }
if (-not (Test-Path (Join-Path $RevitApiDir 'RevitAPI.dll'))) { throw "no RevitAPI.dll under $RevitApiDir (the plugin compiles against Revit 2025's API)" }

$commit = $SourceCommit
$dirty = $false
$commitFile = Join-Path $SourceRoot 'COMMIT'
if (-not $commit -and (Test-Path $commitFile)) { $commit = (Get-Content $commitFile -TotalCount 1).Trim() }
if (-not $commit -and (Test-Path (Join-Path $SourceRoot '.git'))) {
    $commit = (& git -C $SourceRoot rev-parse HEAD 2>$null | Select-Object -First 1)
    $status = (& git -C $SourceRoot status --porcelain --untracked-files=all -- revit shared 2>$null)
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
Copy-Item (Join-Path $root 'Harness\TimingHarness.csproj'), (Join-Path $root 'Harness\TimingHarness.cs'), (Join-Path $root 'Harness\Verify.cs') $proj
$verify = if (Test-Path (Join-Path $tree 'MantlePlace.Revit.Addin\PrepareForHandover.cs')) { 'true' } else { 'false' }

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
    "-p:Tree=$tree" "-p:CoreDir=$coreDir" "-p:ClientDir=$clientDir" "-p:PlatformApiDir=$platformApiDir" "-p:WpfDir=$wpfDir" "-p:RevitApiDir=$RevitApiDir" "-p:HarnessVerify=$verify" 2>&1 | Tee-Object -FilePath $log | Select-Object -Last 25
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE); see $log" }
$dll = Join-Path $outDir "$assemblyName.dll"
if (-not (Test-Path $dll)) { throw "build reported success but $dll is missing" }

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
