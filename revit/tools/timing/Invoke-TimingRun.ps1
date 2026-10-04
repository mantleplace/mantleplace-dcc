# The real-Revit timing driver. One run = one Revit process, launched quiet, stopped by PID at the end.
# See README.md beside this script for the two phases and the flags.
#
#   ./Invoke-TimingRun.ps1 -Phase layers -Tag main-d4f3453 -Year 2027 -Commit d4f3453 -Bundle <zip> -Layers All
#   ./Invoke-TimingRun.ps1 -Phase window -Tag main-d4f3453 -Year 2025 -Bundle <zip> -Boxes All
#
# Quiet by construction: Revit is created through WMI with SW_SHOWMINNOACTIVE (a child of the foreground
# terminal would inherit the right to steal focus; a WMI-created process does not), windows are parked on
# a secondary screen without activation, the unsigned add-in prompts are answered with TDM_CLICK_BUTTON to
# the dialog window, and the import window is driven by UI Automation Toggle/Invoke only. No mouse move, no
# key, no click. Only the PID this script launched is ever stopped; the real add-in's install slot is
# never read or written. No run launches while any other Revit is up.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('window', 'layers')][string]$Phase,
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][ValidateSet('2025', '2026', '2027')][string]$Year,
    # The folder that contains revit\src. Needed the first time a tag is built (or with -Rebuild), unless -Commit.
    [string]$SourceRoot = '',
    # A commit of this repository to export (Export-TimingSource.ps1) and build, in place of -SourceRoot.
    [string]$Commit = '',
    [string]$SourceCommit = '',
    # layers phase: ImportLayer names (Terrain, PublishedContours, ...), All, or Default (the checklist's own defaults).
    [string]$Layers = '',
    # layers phase: the fidelity level of each layer - Default (ImportLayers.DefaultLevel, what the window opens on),
    # All=<RAW|MAX|MED|MIN>, or a list such as 'Planting=Min,RoadSubdivisions=Med'. Empty: every layer at MAX, except
    # with -Layers Default, which takes the default levels too. The window phase always imports the window's levels.
    [string]$Levels = '',
    # layers phase: an ImportLayer name. Plans with the full -Layers choice, runs the staged import slice by slice and
    # stops cleanly in front of the first step that builds that layer: every earlier step committed, the named step
    # not started, no transaction open. Combine with -SaveCheckpoint to keep the project as a full import leaves it at
    # that point (e.g. -Layers All -StopBefore ImageryDrape -SaveCheckpoint predrape). Which steps ran, and that the
    # named one did not, is recorded in stop-before.txt, result.json and the checkpoint's sidecar.
    [string]$StopBefore = '',
    # window phase: which boxes end up checked - All, Default (the boxes as the window opens them, untouched), or a
    # list of layer names (Published Contours or PublishedContours).
    [string]$Boxes = 'All',
    # A .rvt (a bare name means checkpoints\<year>\<name>.rvt). The run works on a COPY of it.
    [string]$OpenCheckpoint = '',
    # layers phase: save the project here after the import, every transaction closed.
    [string]$SaveCheckpoint = '',
    [switch]$OverwriteCheckpoint,
    # The bundle zip to import. Only ever read: each run imports a copy of it. Default: MANTLEPLACE_TIMING_BUNDLE.
    [string]$Bundle = $env:MANTLEPLACE_TIMING_BUNDLE,
    # Where builds, run folders, checkpoints and pid records go. Default: MANTLEPLACE_TIMING_ROOT, else
    # %LOCALAPPDATA%\MantlePlaceTiming. Refused inside the repository.
    [string]$WorkRoot = '',
    # The folder holding one "Revit <year>" folder per installed year. Default: Revit's standard install folder.
    [string]$RevitRoot = (Join-Path $env:ProgramFiles 'Autodesk'),
    # The screen to park Revit's windows on (a device name such as \\.\DISPLAY2). Default: the smallest
    # non-primary screen; with only one screen, windows stay minimised off-screen.
    [string]$ParkScreen = '',
    # When another Revit is up, wait up to this many seconds for it to exit before refusing. 0 refuses at once.
    [int]$WaitForRevitSeconds = 0,
    # Wall-clock cutoff counted from launching Revit. A cutoff is recorded as a measurement, not a failure.
    [int]$TimeoutSeconds = 3600,
    # window phase: seconds between readings of the window.
    [int]$SampleSeconds = 5,
    [switch]$Rebuild,
    # window phase: minimise Revit's main window (and with it the import window) once Import is pressed.
    [switch]$Minimize,
    # Keep the bundle copy, the project copy and the harness copy in the run dir afterwards.
    [switch]$KeepFiles,
    # ON by default (turn off with -ColdIfc:$false). Before launch, MOVE this year's converted site model,
    # <extracted folder>\Site\Site.<year>.rvt, and nothing else, aside into the run dir (ifc-cache-original\), so
    # that the Site Model step converts the IFC instead of reusing the companion an earlier run left. However the
    # run ends (success, cutoff, error, Ctrl-C) the original is moved back over whatever the run left at that path,
    # verified byte-identical by sha256, and the run's own file is kept in ifc-cache-run\. The extracted folder is
    # %LOCALAPPDATA%\MantlePlace\bundles\<order id>\extracted: keyed by the manifest's order id, shared by every run
    # of the order (and the real add-in), not the zip copy in the run dir. Only acts when the run imports the
    # Site Model layer. What it did is recorded in meta.json (ifcLink, extractedFolder) and ifc-cache.json.
    [switch]$ColdIfc = $true
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
. (Join-Path $root 'lib\timing-root.ps1')
. (Join-Path $root 'lib\quiet-win32.ps1')
. (Join-Path $root 'lib\window-automation.ps1')

$clock = [Diagnostics.Stopwatch]::StartNew()
$script:driverLog = $null
# Every number this driver writes for a machine to read (driver.txt, window.txt, meta.json) is formatted with the
# invariant culture and no group separators: a session set to en-US used to write 2,754.0, which a parser dropped.
$inv = [Globalization.CultureInfo]::InvariantCulture
function Stamp($text) {
    $line = "{0,7}s {1}" -f $clock.Elapsed.TotalSeconds.ToString('F0', $inv), $text
    if ($script:driverLog) { Add-Content -LiteralPath $script:driverLog -Value $line -Encoding UTF8 }
    Write-Host $line
}
function Iso([datetime]$d) { $d.ToString('yyyy-MM-ddTHH:mm:ss.fff', $inv) }

# ---- arguments ---------------------------------------------------------------------------------------
if ($Tag -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,30}$') { throw "Tag '$Tag' must match ^[A-Za-z0-9][A-Za-z0-9_-]{0,30}$" }
if ($Phase -eq 'layers' -and -not $Layers) { throw '-Layers is required for the layers phase (ImportLayer names, All, or Default).' }
if ($Phase -eq 'window' -and ($Layers -or $Levels -or $SaveCheckpoint)) { throw '-Layers, -Levels and -SaveCheckpoint belong to the layers phase; the window phase takes -Boxes and imports the levels the window opens on.' }
if ($StopBefore -and $Phase -ne 'layers') { throw '-StopBefore belongs to the layers phase.' }
$StopBefore = ($StopBefore -replace '\s', '')
if ($SourceRoot -and $Commit) { throw 'pass -SourceRoot or -Commit, not both.' }
if (-not $Bundle) { throw 'no bundle: pass -Bundle <zip> or set MANTLEPLACE_TIMING_BUNDLE.' }
if (-not (Test-Path -LiteralPath $Bundle)) { throw "no bundle at $Bundle" }
$work = Resolve-TimingRoot $WorkRoot
$exe = Join-Path $RevitRoot "Revit $Year\Revit.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "no Revit $Year at $exe (see -RevitRoot)" }
$Bundle = (Resolve-Path -LiteralPath $Bundle).Path

function Resolve-Checkpoint([string]$Name) {
    $p = if ([IO.Path]::IsPathRooted($Name)) { $Name } else { Join-Path (Join-Path $work "checkpoints\$Year") $Name }
    if ([IO.Path]::GetExtension($p) -eq '') { $p += '.rvt' }
    $p
}
$openPath = ''
if ($OpenCheckpoint) {
    $openPath = Resolve-Checkpoint $OpenCheckpoint
    if (-not (Test-Path -LiteralPath $openPath)) { throw "no checkpoint at $openPath" }
}
$savePath = ''
if ($SaveCheckpoint) {
    $savePath = Resolve-Checkpoint $SaveCheckpoint
    if ((Test-Path -LiteralPath $savePath) -and -not $OverwriteCheckpoint) { throw "checkpoint $savePath already exists (another agent may rely on it); pick another name or pass -OverwriteCheckpoint" }
    New-Item -ItemType Directory -Force (Split-Path $savePath) | Out-Null
}

# ---- the build ---------------------------------------------------------------------------------------
$buildDir = Join-Path $work "builds\$Tag"
$buildJson = Join-Path $buildDir 'build.json'
if ($Commit) { $SourceRoot = @(& (Join-Path $root 'Export-TimingSource.ps1') -Commit $Commit -WorkRoot $work)[-1] }
if ($Rebuild -or -not (Test-Path $buildJson)) {
    if (-not $SourceRoot) { throw "tag '$Tag' has no build yet: pass -Commit <sha> or -SourceRoot <dir containing revit\src>" }
    & (Join-Path $root 'Build-TimingHarness.ps1') -Tag $Tag -SourceRoot $SourceRoot -SourceCommit $SourceCommit -WorkRoot $work -Force:$Rebuild
}
$build = Get-Content $buildJson -Raw | ConvertFrom-Json
if ($SourceRoot -and (Resolve-Path -LiteralPath $SourceRoot).Path.TrimEnd('\') -ne $build.sourceRoot) {
    throw "tag '$Tag' is built from $($build.sourceRoot), not $SourceRoot. Use another tag, or -Rebuild if the tag is yours alone."
}
$outDir = Join-Path $buildDir 'out'
$dllName = "$($build.assemblyName).dll"
if ((Get-FileHash (Join-Path $outDir $dllName) -Algorithm SHA256).Hash -ne $build.dllSha256) { throw "$outDir\$dllName does not match build.json; rebuild the tag (-Rebuild)" }

# layers named on the command line, checked against the enum in the source this tag was built from
if ($Phase -eq 'layers') {
    $enumFile = Join-Path $build.sourceRoot 'revit\src\MantlePlace.Revit.Core\ImportLayers.cs'
    if (Test-Path $enumFile) {
        $body = [regex]::Match((Get-Content $enumFile -Raw), 'public enum ImportLayer\s*\{(.*?)\}', 'Singleline').Groups[1].Value
        $valid = [regex]::Matches(($body -replace '///[^\r\n]*', ''), '\b([A-Z][A-Za-z]+)\s*[,\r\n]') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
        if ($Layers -notin @('All', 'Default')) {
            foreach ($l in ($Layers -split ',' | ForEach-Object { ($_.Trim() -replace '\s', '') } | Where-Object { $_ })) {
                if ($valid -and ($valid -notcontains $l)) { throw "'$l' is not an ImportLayer in this source. Valid: $($valid -join ', '), All, or Default" }
            }
        }
        if ($StopBefore -and $valid -and ($valid -notcontains $StopBefore)) { throw "-StopBefore '$StopBefore' is not an ImportLayer in this source. Valid: $($valid -join ', ')" }
    }
    # The stop layer must be one the run plans, or the run would go to its end without ever stopping.
    if ($StopBefore -and $Layers -notin @('All', 'Default') -and (@($Layers -split ',' | ForEach-Object { ($_.Trim() -replace '\s', '') } | Where-Object { $_ }) -notcontains $StopBefore)) {
        throw "-StopBefore $StopBefore is not among -Layers ($Layers), so the plan has no step to stop in front of."
    }
}

# ---- who else is running ------------------------------------------------------------------------------
# One Revit at a time, whatever its year and whoever launched it. Two reasons. Timings taken beside another Revit
# measure the contention, not the code. And Revit watches %APPDATA%\Autodesk\Revit\Addins\<year>\ while it runs:
# this run's temporary manifest is written there, so a Revit of the same year that is already up picks it up,
# raises an unsigned add-in prompt and loads this run's harness DLL into its own process, whose environment is not
# this run's (README, "Traps"). The harness declines outside its own tag, but that is the second line of defence.
# The first is this: no run launches while any other Revit is up. -WaitForRevitSeconds waits for it instead.
$pidDir = Join-Path $work 'pids'
New-Item -ItemType Directory -Force $pidDir | Out-Null
# Reads who is up: $script:ours (pid -> pid file, this tool's Revits that are alive; dead pid files are removed) and
# $script:foreign (every other Revit). Called again under the launch lock.
function Read-LiveRevits {
    $script:ours = @{}
    foreach ($f in (Get-ChildItem $pidDir -Filter '*.json' -ErrorAction SilentlyContinue)) {
        $info = Get-Content $f.FullName -Raw | ConvertFrom-Json
        if (Get-Process -Id ([int]$f.BaseName) -ErrorAction SilentlyContinue) {
            $script:ours[[int]$f.BaseName] = $info
            if ($info.tag -eq $Tag -and $info.year -eq $Year) { throw "tag '$Tag' is already running in Revit $Year (pid $($f.BaseName), driver pid $($info.driverPid)); use another tag" }
        }
        else { Remove-Item $f.FullName -Force }
    }
    $script:foreign = @(Get-Process Revit -ErrorAction SilentlyContinue | Where-Object { -not $script:ours.ContainsKey($_.Id) })
}
# Every Revit that is up, as text: this tool's (any tag) and the ones it did not launch.
function Get-OtherRevits {
    @($script:ours.GetEnumerator() | Sort-Object Key | ForEach-Object { "pid $($_.Key), Revit $($_.Value.year), a run of this harness, tag $($_.Value.tag)" }) +
    @($script:foreign | ForEach-Object {
        $path = ''; try { $path = $_.Path } catch { }
        $year = if ($path -match 'Revit (\d{4})') { "Revit $($Matches[1])" } else { 'Revit' }
        "pid $($_.Id), $year, '$($_.MainWindowTitle)', not launched by this tool"
    })
}
# Waits up to -WaitForRevitSeconds for every other Revit to exit; returns the ones still up.
function Wait-NoOtherRevit {
    $deadline = (Get-Date).AddSeconds($WaitForRevitSeconds)
    $said = $false
    while ($true) {
        Read-LiveRevits
        $who = @(Get-OtherRevits)
        if ($who.Count -eq 0 -or (Get-Date) -ge $deadline) { return $who }
        if (-not $said) { Write-Host "waiting up to $WaitForRevitSeconds s for another Revit to exit: $($who -join '; ')"; $said = $true }
        Start-Sleep -Seconds 10
    }
}
$otherRevits = @(Wait-NoOtherRevit)
if ($otherRevits.Count -gt 0) {
    throw "another Revit is up ($($otherRevits -join '; ')). Not launching beside it: one Revit at a time. Wait for it to exit, or pass -WaitForRevitSeconds."
}

# ---- the IFC link's companion: cold or warm --------------------------------------------------------------
# The plugin does NOT extract beside the zip copy this run imports. It extracts to
# %LOCALAPPDATA%\MantlePlace\bundles\<order id>\extracted, keyed by the manifest's order_id, so every run of an
# order (and the real add-in) shares one folder. The Site Model step converts Site\Site.ifc into
# Site\Site.<year>.rvt there only when that file is missing and reuses it otherwise, so without moving it away the
# second run of a year is warm. The plan below reads the manifest of the source zip and touches nothing. The move
# itself (Move-IfcAside) happens just before Revit is launched, inside a try/finally whose finally is
# Restore-IfcCache: the user's file goes back over whatever the run left, however the run ends.
function Get-BundleOrderId([string]$ZipPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName.Replace('\', '/').EndsWith('Metadata/manifest.json', [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
        if (-not $entry) { return '' }
        $reader = New-Object IO.StreamReader($entry.Open(), [Text.Encoding]::UTF8)
        try { $manifestJson = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        $id = "$($manifestJson.order_id)"
        if (-not $id -and $manifestJson.attribution) { $id = "$($manifestJson.attribution.order_id)" }
        $id
    }
    finally { $archive.Dispose() }
}
# Whether this run imports the layer whose step links the site IFC (ImportLayer.SiteModel, "Site Model").
function Test-ImportsSiteModel {
    $wanted = if ($Phase -eq 'layers') { $Layers } else { $Boxes }
    if ($wanted -ieq 'All') { return $true }
    if ($wanted -ieq 'Default') { return $false }   # the Site Model starts unticked
    @($wanted -split ',' | ForEach-Object { ($_.Trim() -replace '\s', '') }) -contains 'SiteModel'
}
$ifcLink = [ordered]@{
    coldRequested = [bool]$ColdIfc; year = $Year; appliesToThisRun = $false; orderId = ''; extractedFolder = ''
    companion = ''; existedBefore = $false; bytesBefore = 0; writtenBefore = ''
    movedAside = $false; originalBytes = 0; originalSha256 = ''; originalKeptAt = ''
    cold = $null; summary = ''
    restored = $null; restoreStatus = 'not armed: this run moved nothing, so there is nothing to put back'
    runFileKeptAt = ''; runFileBytes = 0; runFileSha256 = ''
}
# 'none' - this run owns no cache state; 'armed' - a cold run began and owes the cache its state back;
# 'restored' - paid; 'failed' - could not (loud). Restore-IfcCache is idempotent and retries from 'failed'.
$script:ifcCacheState = 'none'
$extractedDir = ''
$ifcLink.appliesToThisRun = [bool](Test-ImportsSiteModel)
$orderId = ''
try { $orderId = Get-BundleOrderId $Bundle } catch { $ifcLink.summary = "unknown: could not read the bundle's manifest ($($_.Exception.Message.Split("`n")[0]))" }
if ($orderId) {
    $ifcLink.orderId = $orderId
    if ($orderId -notmatch '^[A-Za-z0-9._-]+$' -or $orderId -in @('.', '..')) {
        # The plugin would hash-suffix this id (its directory-name sanitiser); this driver does not reimplement that.
        $ifcLink.summary = "unknown: order id '$orderId' needs the plugin's directory-name sanitiser, which this driver does not reproduce; the extracted folder was not located"
    }
    else {
        $extractedDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "MantlePlace\bundles\$orderId\extracted"
        $ifcLink.extractedFolder = $extractedDir
        $ifcLink.companion = Join-Path $extractedDir "Site\Site.$Year.rvt"
    }
}
elseif (-not $ifcLink.summary) { $ifcLink.summary = "unknown: the manifest carries no order id, so the plugin keys the extraction by the zip copy's own path; not touched" }
if ($ifcLink.companion -and $ifcLink.appliesToThisRun -and $ColdIfc) {
    # Moving the file away is only safe when no other Revit can be reading it; the check above already refused one,
    # and it is made again under the launch lock before anything moves.
    # An earlier cold run that could not put the user's original back (or whose driver was killed) leaves it in
    # its own run dir. The file at the cache path is then that run's REBUILT copy, and a new run would take it
    # for the original. So: no cold run until the original is back.
    foreach ($earlier in @(Get-ChildItem (Join-Path $work 'runs') -Directory -ErrorAction SilentlyContinue)) {
        $stranded = Join-Path $earlier.FullName "ifc-cache-original\Site.$Year.rvt"
        if (-not (Test-Path -LiteralPath $stranded -PathType Leaf)) { continue }
        $theirs = ''
        try { $theirs = "$((Get-Content (Join-Path $earlier.FullName 'ifc-cache.json') -Raw | ConvertFrom-Json).orderId)" } catch { }
        if ($theirs -and $theirs -ne $orderId) { continue }
        throw "the user's original Site.$Year.rvt was left in $stranded by an earlier cold run (see ifc-cache.json beside it) and the cache path now holds that run's rebuilt copy. Copy the original back to $($ifcLink.companion), check its sha256 against ifc-cache.json, then delete that ifc-cache-original folder. Or pass -ColdIfc:`$false."
    }
}

# ---- moving the companion aside, and putting it back ------------------------------------------------------
function Get-FileSha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
# The record of what this run did to the cache, in the run dir. Written BEFORE the original leaves the cache and
# again whenever the state changes, so a driver that is killed outright still leaves the way home in writing.
function Save-IfcRecord { $ifcLink | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runDir 'ifc-cache.json') -Encoding UTF8 }
# Adds fields to result.json (creating it if a cutoff meant the harness never wrote one).
function Set-ResultFields([hashtable]$Fields) {
    $path = Join-Path $runDir 'result.json'
    $obj = if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    foreach ($k in $Fields.Keys) {
        if ($obj.PSObject.Properties[$k]) { $obj.$k = $Fields[$k] } else { $obj | Add-Member -NotePropertyName $k -NotePropertyValue $Fields[$k] }
    }
    $obj | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $path -Encoding UTF8
}

# Run once, just before Revit is launched, INSIDE the try whose finally is Restore-IfcCache. Moves exactly one file,
# <extracted>\Site\Site.<year>.rvt, out of the cache after copying it into the run dir and proving the copy has the
# same sha256; never another year's companion, the IFC, or anything else in the folder.
function Move-IfcAside {
    if (-not $ifcLink.companion) { Stamp "IFC link: $($ifcLink.summary)"; return }
    if (-not $ifcLink.appliesToThisRun) {
        $ifcLink.summary = 'not applicable: this run does not import the Site Model layer, so the IFC link is not touched'
        Stamp "IFC link: $($ifcLink.summary)"
        return
    }
    $companion = $ifcLink.companion
    $item = $null
    if (Test-Path -LiteralPath $companion -PathType Leaf) {
        $item = Get-Item -LiteralPath $companion -Force
        $ifcLink.existedBefore = $true
        $ifcLink.bytesBefore = $item.Length
        $ifcLink.writtenBefore = Iso $item.LastWriteTime
    }
    if ($ColdIfc) {
        $script:ifcCacheState = 'armed'   # from here on the cache's state is owed back, whatever happens next
        if ($item) {
            $expected = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetFullPath($extractedDir)) "Site\Site.$Year.rvt"))
            if ($item.FullName -ine $expected -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "refusing to move $($item.FullName): it is not the plain file $expected"
            }
            $keepDir = Join-Path $runDir 'ifc-cache-original'
            $kept = Join-Path $keepDir "Site.$Year.rvt"
            try {
                New-Item -ItemType Directory -Force $keepDir | Out-Null
                $ifcLink.originalBytes = $item.Length
                $ifcLink.originalSha256 = Get-FileSha256 $companion
                Copy-Item -LiteralPath $companion -Destination $kept
                if ((Get-Item -LiteralPath $kept).Length -ne $ifcLink.originalBytes -or (Get-FileSha256 $kept) -ne $ifcLink.originalSha256) {
                    throw "the copy of $companion in $keepDir does not match it (size or sha256); nothing was moved"
                }
                $ifcLink.originalKeptAt = $kept
                Save-IfcRecord                                    # the copy is proven and recorded; only now does the original leave
                Remove-Item -LiteralPath $companion -Force
                $ifcLink.movedAside = $true
                Save-IfcRecord
            }
            catch {
                if (-not $ifcLink.movedAside) { Remove-Item -LiteralPath $kept -Force -ErrorAction SilentlyContinue; $ifcLink.originalKeptAt = '' }   # the file never left the cache
                throw
            }
        }
        else { Save-IfcRecord }
    }
    $ifcLink.cold = -not (Test-Path -LiteralPath $companion)
    $ifcLink.summary =
        if ($ifcLink.movedAside) { "cold: moved Site.$Year.rvt aside to $($ifcLink.originalKeptAt) ($($ifcLink.originalBytes) bytes, sha256 $($ifcLink.originalSha256.Substring(0, 16)), last written $($ifcLink.writtenBefore)); it goes back byte-identical when the run ends; the Site Model step converts the IFC" }
        elseif ($ColdIfc) { "cold: Site.$Year.rvt was not in the cache; whatever the run builds there is moved to ifc-cache-run when it ends, so the cache ends as it began; the Site Model step converts the IFC" }
        elseif ($item) { "WARM: Site.$Year.rvt kept in the cache ($($ifcLink.bytesBefore) bytes, last written $($ifcLink.writtenBefore)); the Site Model step reuses it instead of converting the IFC" }
        else { "cold: Site.$Year.rvt was not in the cache (-ColdIfc was off); the Site Model step converts the IFC and the file stays afterwards" }
    Stamp "IFC link: $($ifcLink.summary) [$extractedDir]"
}

# Run when the run is over, from the inner finally (Revit already stopped) and again from the outer finally as a
# safety net (a no-op once done). Puts the cache back exactly as it was: the run's own file, if any, is copied to
# ifc-cache-run; the original is copied beside the target, proven equal by sha256, renamed over whatever is at the
# target, proven again, and only then is the copy in the run dir removed. Retries three times (a file handle may
# outlive Revit by a moment). A failure is loud: driver.txt, result.json, ifc-cache.json and the record in meta.json.
function Restore-IfcCache {
    if ($script:ifcCacheState -notin @('armed', 'failed')) { return }
    $companion = $ifcLink.companion
    $kept = Join-Path $runDir "ifc-cache-original\Site.$Year.rvt"
    $runKeepDir = Join-Path $runDir 'ifc-cache-run'
    $lastError = ''
    # Whatever sits at the target is the RUN's file only if the original was moved away, or there never was one.
    # If a move that began did not complete, the original is still in the cache and nothing here may touch it.
    $runOwnsPath = $ifcLink.movedAside -or -not $ifcLink.existedBefore
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            if ($runOwnsPath -and (Test-Path -LiteralPath $companion -PathType Leaf) -and -not $ifcLink.runFileKeptAt) {
                $left = Get-Item -LiteralPath $companion -Force
                New-Item -ItemType Directory -Force $runKeepDir | Out-Null
                $copy = Join-Path $runKeepDir "Site.$Year.rvt"
                Copy-Item -LiteralPath $companion -Destination $copy -Force
                if ((Get-Item -LiteralPath $copy).Length -ne $left.Length) { throw "the copy of the run's own $companion in $runKeepDir is the wrong size" }
                $ifcLink.runFileKeptAt = $copy
                $ifcLink.runFileBytes = $left.Length
                $ifcLink.runFileSha256 = Get-FileSha256 $copy
            }
            if ($ifcLink.movedAside) {
                $temp = "$companion.timing-restore"
                Copy-Item -LiteralPath $kept -Destination $temp -Force
                if ((Get-FileSha256 $temp) -ne $ifcLink.originalSha256) { throw "the copy of the original made beside $companion does not match its sha256" }
                Move-Item -LiteralPath $temp -Destination $companion -Force
                $back = Get-Item -LiteralPath $companion -Force
                if ($back.Length -ne $ifcLink.originalBytes -or (Get-FileSha256 $companion) -ne $ifcLink.originalSha256) { throw "$companion does not match the original's size or sha256 after the restore" }
                Remove-Item -LiteralPath $kept -Force
                try { [IO.Directory]::Delete((Split-Path $kept)) } catch { }   # throws, harmlessly, unless it is now empty
                $ifcLink.restored = $true
                $ifcLink.restoreStatus = "restored: the original is back at $companion, byte-identical ($($ifcLink.originalBytes) bytes, sha256 $($ifcLink.originalSha256))"
            }
            elseif ($ifcLink.existedBefore) {
                # armed, but the move never completed: the original is still at the target, untouched
                $ifcLink.restored = $true
                $ifcLink.restoreStatus = 'restored: the file never left the cache'
            }
            else {
                if (Test-Path -LiteralPath $companion -PathType Leaf) { Remove-Item -LiteralPath $companion -Force }   # the run's copy is kept above
                $ifcLink.restored = $true
                $ifcLink.restoreStatus = "restored: the cache had no Site.$Year.rvt before the run and has none now (the run's own, if it made one, is in ifc-cache-run)"
            }
            $script:ifcCacheState = 'restored'
            Stamp "IFC cache: $($ifcLink.restoreStatus)"
            Save-IfcRecord
            return
        }
        catch {
            $lastError = $_.Exception.Message.Split("`n")[0]
            Remove-Item -LiteralPath "$companion.timing-restore" -Force -ErrorAction SilentlyContinue
            if ($attempt -lt 3) { Start-Sleep -Seconds 3 }
        }
    }
    $script:ifcCacheState = 'failed'
    $ifcLink.restored = $false
    $ifcLink.restoreStatus = "FAILED: $lastError. THE USER'S CACHE IS NOT AS IT WAS. The original is in $kept (sha256 $($ifcLink.originalSha256), $($ifcLink.originalBytes) bytes): copy it to $companion by hand."
    Stamp '*********************************************************************************************'
    Stamp "*** IFC CACHE RESTORE FAILED: $lastError"
    Stamp "*** the user's original Site.$Year.rvt is at $kept"
    Stamp "*** put it back at $companion (sha256 $($ifcLink.originalSha256)); no cold run will start until it is"
    Stamp '*********************************************************************************************'
    try { Save-IfcRecord } catch { }
    try { Set-ResultFields @{ ifc_cache_restore_failed = $true; ifc_cache_restore_message = $ifcLink.restoreStatus; ifc_cache_original_at = $kept } } catch { Stamp "could not write the restore failure into result.json: $($_.Exception.Message.Split("`n")[0])" }
}

# ---- the run dir ------------------------------------------------------------------------------------
$runsRoot = Join-Path $work 'runs'
New-Item -ItemType Directory -Force $runsRoot | Out-Null
$runDir = Join-Path $runsRoot "$Tag-$Year"
if ((Test-Path $runDir) -and (Get-ChildItem $runDir -Force | Select-Object -First 1)) {
    # An earlier run of this same tag and year. Kept, never wiped.
    $kept = "$runDir.prev-" + (Get-Item $runDir).LastWriteTime.ToString('yyyyMMdd-HHmmss')
    Move-Item -LiteralPath $runDir -Destination $kept
}
New-Item -ItemType Directory -Force $runDir | Out-Null
$script:driverLog = Join-Path $runDir 'driver.txt'
Stamp "timing harness: phase=$Phase tag=$Tag year=$Year; source $($build.sourceRoot) @ $($build.sourceCommit); dll $($build.dllSha256.Substring(0, 12))"
if ($build.PSObject.Properties['compiledTag'] -and "$($build.compiledTag)" -eq $Tag) {
    Stamp "guard: the tag '$Tag' is compiled into the DLL (checked at build time); the harness does nothing in a Revit whose MANTLEPLACE_TIMING_TAG differs, and logs the refusal to %TEMP%\mantleplace-timing-declined.log"
}
else {
    Stamp "guard: this build has no compiled-tag record (built without PowerShell 7). It still gates on MANTLEPLACE_TIMING_TAG through assembly metadata but logs no refusal; -Rebuild (on your own tag) under pwsh gives it the compiled tag"
}

$bundleDir = Join-Path $runDir 'bundle'
New-Item -ItemType Directory -Force $bundleDir | Out-Null
$zip = Join-Path $bundleDir 'bundle.zip'
Copy-Item -LiteralPath $Bundle -Destination $zip
$bundleSha = (Get-FileHash $zip -Algorithm SHA256).Hash
$sourceInfo = Get-Item -LiteralPath $Bundle
Stamp "bundle: copied $Bundle ($($sourceInfo.Length) bytes, modified $($sourceInfo.LastWriteTime.ToString('s')), sha256 $($bundleSha.Substring(0, 16))) to $zip"
$pluginLog = "$zip.mantleplace-import.log"
if ($openPath) { Copy-Item -LiteralPath $openPath -Destination (Join-Path $runDir 'project.rvt'); Stamp "checkpoint: working on a copy of $openPath" }

$addinDir = Join-Path $runDir 'addin'
New-Item -ItemType Directory -Force $addinDir | Out-Null
Copy-Item -Path (Join-Path $outDir '*') -Destination $addinDir -Recurse

$addinsFolder = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year"
$madeAddins = -not (Test-Path $addinsFolder)
$manifest = Join-Path $addinsFolder "MantlePlace.Timing.$Tag.addin"
# Written just before Revit is launched, and only then (as short a time in the watched folder as possible), and
# removed as soon as the harness reports started.txt, or when the run ends however it ends.
$manifestXml = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Mantle Place timing harness $Tag (temporary)</Name>
    <Assembly>$addinDir\$dllName</Assembly>
    <FullClassName>MantlePlace.Scratch.Timing.Harness</FullClassName>
    <ClientId>$($build.clientId)</ClientId>
    <VendorId>MNTL</VendorId>
  </AddIn>
</RevitAddIns>
"@

# The child's environment: ours, minus every other harness's variables, plus this run's.
$envMap = [ordered]@{}
foreach ($kv in [Environment]::GetEnvironmentVariables().GetEnumerator()) {
    if ($kv.Key -like 'MANTLEPLACE_TIMING_*' -or $kv.Key -like 'MANTLEPLACE_SCRATCH*' -or "$($kv.Key)".StartsWith('=')) { continue }
    $envMap[$kv.Key] = $kv.Value
}
$envMap['MANTLEPLACE_TIMING_TAG'] = $Tag
$envMap['MANTLEPLACE_TIMING_DIR'] = $runDir
$envMap['MANTLEPLACE_TIMING_PHASE'] = $Phase
$envMap['MANTLEPLACE_TIMING_BUNDLE'] = $zip
if ($Phase -eq 'layers') {
    $envMap['MANTLEPLACE_TIMING_LAYERS'] = $Layers
    if ($Levels) { $envMap['MANTLEPLACE_TIMING_LEVELS'] = $Levels }
    if ($StopBefore) { $envMap['MANTLEPLACE_TIMING_STOP_BEFORE'] = $StopBefore }
    if ($savePath) { $envMap['MANTLEPLACE_TIMING_SAVE_CHECKPOINT'] = (Join-Path $runDir 'checkpoint-saving.rvt') }
}
$envBlock = [string[]]@($envMap.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })

# One launch at a time. The check near the top cannot see a run that has decided to launch but has not launched
# yet: a run's pid file exists only once its Revit does. So a run takes this lock (a named mutex, one for every
# year, released by the OS if the driver dies), checks again under it, and holds it until its pid file is written.
$script:launchLock = $null
function Enter-LaunchLock {
    $m = New-Object Threading.Mutex($false, "Global\MantlePlaceTimingLaunch")
    $got = $false
    try { $got = $m.WaitOne(300000) } catch [Threading.AbandonedMutexException] { $got = $true }   # a driver died holding it; the lock is ours
    if (-not $got) { $m.Dispose(); throw "another driver has held the Revit launch lock for 5 minutes; try again" }
    $script:launchLock = $m
}
function Exit-LaunchLock {
    if ($script:launchLock) {
        try { $script:launchLock.ReleaseMutex() } catch { }
        try { $script:launchLock.Dispose() } catch { }
        $script:launchLock = $null
    }
}

# ---- from here to the end of the script, the user's IFC cache is on loan -----------------------------------
# Everything below, launch included, is inside one try whose finally (at the very end) puts the cache back
# whatever happens: a throw before Revit exists, a cutoff, a crash, Ctrl-C. The body is not re-indented, so the
# launch reads top to bottom at the script's own level. A driver killed outright (Stop-Process,
# power) cannot run a finally; ifc-cache.json and ifc-cache-original\ in the run dir then say where the original
# is, and the next cold run refuses to start until it is back.
try {
# From here until the pid file exists, this is the only driver launching a Revit, and the check for any other
# Revit is made again under that lock, before anything is moved or written.
Enter-LaunchLock
Read-LiveRevits
$otherRevits = @(Get-OtherRevits)
if ($otherRevits.Count -gt 0) {
    Remove-Item -LiteralPath $zip, $addinDir -Recurse -Force -ErrorAction SilentlyContinue   # the copies this run made in its run dir
    throw "another Revit came up while this run was preparing to launch ($($otherRevits -join '; ')). Not launching beside it; try again when it has exited."
}
Move-IfcAside

# ---- launch ------------------------------------------------------------------------------------------
function Get-Spot {
    if ($ParkScreen) {
        $named = @([System.Windows.Forms.Screen]::AllScreens | Where-Object { $_.DeviceName -eq $ParkScreen }) | Select-Object -First 1
        if (-not $named) { throw "no screen called '$ParkScreen'. Screens: $(([System.Windows.Forms.Screen]::AllScreens | ForEach-Object { $_.DeviceName }) -join ', ')" }
        $b = $named.Bounds; return @{ X = $b.X; Y = $b.Y; Screen = $named.DeviceName }
    }
    Get-ParkingSpot
}
$spot = Get-Spot
$userWindow = [Q.W2]::GetForegroundWindow()
$cursor0 = Get-Cursor; $fore0 = Get-ForegroundOwner
$startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]7; EnvironmentVariables = $envBlock }
# The temporary manifest goes into the watched folder now, and not a moment earlier (see the manifest's comment above).
if ($madeAddins) { New-Item -ItemType Directory -Force $addinsFolder | Out-Null }
$manifestXml | Set-Content -Encoding UTF8 $manifest
$launchedAt = Get-Date
$created = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$exe`" /nosplash"; CurrentDirectory = (Split-Path $exe); ProcessStartupInformation = $startup }
if ($created.ReturnValue -ne 0) {
    Remove-Item $manifest -Force -ErrorAction SilentlyContinue
    throw "WMI would not start Revit (Win32_Process.Create returned $($created.ReturnValue))"
}
$revit = Get-Process -Id $created.ProcessId
$runClock = [Diagnostics.Stopwatch]::StartNew()   # the cutoff counts from here, the moment Revit was launched
$revitStart = $revit.StartTime
[ordered]@{ tag = $Tag; year = $Year; runDir = $runDir; driverPid = $PID } | ConvertTo-Json | Set-Content (Join-Path $pidDir "$($revit.Id).json")
Set-Content (Join-Path $runDir 'revit.pid') $revit.Id
Exit-LaunchLock   # the pid file exists: the next driver's check sees this Revit
Stamp "Revit $Year pid $($revit.Id), minimized; parking at $($spot.X),$($spot.Y) ($($spot.Screen)); cursor $cursor0; foreground $fore0"

$doneFile = Join-Path $runDir 'done.txt'
$meta = [ordered]@{
    tag = $Tag; year = $Year; phase = $Phase
    sourceRoot = $build.sourceRoot; sourceCommit = $build.sourceCommit; dllSha256 = $build.dllSha256
    layers = $Layers; levels = $Levels; boxes = $(if ($Phase -eq 'window') { $Boxes } else { $null }); stopBefore = $StopBefore
    openCheckpoint = $openPath; saveCheckpoint = $savePath
    bundleSource = $Bundle; bundleBytes = $sourceInfo.Length; bundleSha256 = $bundleSha
    timeoutSeconds = $TimeoutSeconds; sampleSeconds = $SampleSeconds
    revitPid = $revit.Id; launchedAt = (Iso $launchedAt); revitProcessStart = (Iso $revitStart)
    driverShell = "PowerShell $($PSVersionTable.PSVersion)"
    compiledTag = $(if ($build.PSObject.Properties['compiledTag']) { "$($build.compiledTag)" } else { '' })   # the tag compiled into the DLL, verified at build time; '' for a build made without PowerShell 7
    extractedFolder = $extractedDir   # where the plugin extracts this order (shared by every run of it); '' when it could not be located
    ifcLink = $ifcLink                # cold or warm at launch, and how the restore went; existsAfter/bytesAfter/writtenAfter (the cache path after the restore) are added when the run ends
    outcome = 'error'
}

# ---- keeping Revit quiet -------------------------------------------------------------------------------
$parked = @{}
$script:stolen = $false
$dialogClass = [char]35 + '32770'   # the Win32 dialog window class, spelled so it never reads as an issue reference
function Tend {
    foreach ($h in (Get-PidWindows $revit.Id)) {
        $class = Get-WindowClass $h
        if ($class -eq $dialogClass) {
            $el = [Windows.Automation.AutomationElement]::FromHandle($h)
            $cond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, 'CommandButton_1002')
            $button = $null; try { $button = $el.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond) } catch { }
            if ($button -and $button.Current.Name -eq 'Load Once') {
                [void][Q.W2]::SendMessage($h, 0x0400 + 102, [IntPtr]1002, [IntPtr]::Zero)
                [void](Stamp "answered Load Once (TDM_CLICK_BUTTON 1002) on '$(Get-WindowTitle $h)'")
                continue
            }
            elseif (-not $parked.ContainsKey("d$h")) {
                $parked["d$h"] = 1
                [void](Stamp "left alone: dialog '$(Get-WindowTitle $h)'")
                try { Show-Dialog $h | ForEach-Object { [void](Stamp $_) } } catch { }
            }
        }
        if (-not [Q.W2]::IsIconic($h) -and -not $parked.ContainsKey("$h") -and -not (Test-Hung $h)) {
            Move-Parked $h $spot; $parked["$h"] = 1; [void](Stamp "parked $class '$(Get-WindowTitle $h)'")
        }
    }
    $fh = [Q.W2]::GetForegroundWindow(); $fp = 0; [void][Q.W2]::GetWindowThreadProcessId($fh, [ref]$fp)
    if ($fp -eq $revit.Id -and $fh -ne $userWindow) {
        if (-not $script:stolen) { $script:stolen = $true; [void](Stamp "Revit took the foreground ($(Get-ForegroundOwner)); handed back: $([Q.W2]::GiveBack($userWindow))") }
    }
    elseif ($script:stolen) { $script:stolen = $false; [void](Stamp "foreground is $(Get-ForegroundOwner) again") }
}
function Wait-Tending([scriptblock]$Until, [double]$Seconds) {
    $end = $clock.Elapsed.TotalSeconds + $Seconds
    while (-not (& $Until) -and $clock.Elapsed.TotalSeconds -lt $end -and -not $revit.HasExited) { Tend; Start-Sleep -Milliseconds 100 }
}
function Get-MainWindow { $p = Get-Process -Id $revit.Id -ErrorAction SilentlyContinue; if (-not $p) { return [IntPtr]::Zero }; $p.Refresh(); $p.MainWindowHandle }

# The last non-blank line of the plugin's log, for the heartbeat and for a cutoff.
function Get-LogTail([int]$Lines = 3) {
    if (-not (Test-Path -LiteralPath $pluginLog)) { return @() }
    try {
        $in = [IO.File]::Open($pluginLog, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
        try {
            $len = $in.Length; $take = [math]::Min($len, 16384); [void]$in.Seek($len - $take, [IO.SeekOrigin]::Begin)
            $buf = New-Object byte[] $take; [void]$in.Read($buf, 0, $take)
        }
        finally { $in.Dispose() }
        $text = [Text.Encoding]::UTF8.GetString($buf)
        @($text -split "\r?\n" | Where-Object { $_.Trim() } | Select-Object -Last $Lines)
    }
    catch { @() }
}

# The step the plugin's log says is in flight: the last "[Kind] started." with no "(Kind took" after it.
function Get-LogStep {
    if (-not (Test-Path -LiteralPath $pluginLog)) { return '' }
    try {
        $in = [IO.File]::Open($pluginLog, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
        try { $sr = New-Object IO.StreamReader($in, [Text.Encoding]::UTF8); $text = $sr.ReadToEnd() } finally { $in.Dispose() }
        $starts = [regex]::Matches($text, '(?m)^(\d\d:\d\d:\d\d)  \[(\w+)\] started\.')
        $ends = [regex]::Matches($text, '(?m)^(\d\d:\d\d:\d\d)  \((\w+) took ')
        if ($starts.Count -eq 0) { return '(no step started yet)' }
        $ls = $starts[$starts.Count - 1]
        $le = if ($ends.Count -gt 0) { $ends[$ends.Count - 1] } else { $null }
        if ($le -and $le.Index -gt $ls.Index) { return "(between steps; last finished $($le.Groups[2].Value))" }
        "$($ls.Groups[2].Value) since $($ls.Groups[1].Value)"
    }
    catch { '' }
}

# ---- the window phase's steps -------------------------------------------------------------------------
function Get-ImportWindow {
    foreach ($h in (Get-PidWindows $revit.Id)) {
        $el = [Windows.Automation.AutomationElement]::FromHandle($h)
        if ($el.Current.Name -eq 'Mantle Place Bundle Import') { return $el }
    }
    $null
}
function Show-Boxes([string]$label, $boxes) {
    [void](Stamp "READBACK $label :: $(($boxes | ForEach-Object { "$($_.Name)=$($_.State)/$(if ($_.Enabled) { 'enabled' } else { 'DISABLED' })" }) -join ' | ')")
}
function Set-Boxes($win, [string]$Want) {
    $boxes = Get-CheckBoxes $win
    Show-Boxes 'initial' $boxes
    if ($Want -ieq 'Default') { [void](Stamp 'boxes left as the window opened them (-Boxes Default)'); return $boxes }
    $squash = { param($s) ($s -replace '\s', '') }
    $wantAll = $Want -ieq 'All'
    $wantList = @($Want -split ',' | ForEach-Object { & $squash $_.Trim() } | Where-Object { $_ })
    foreach ($w in $wantList) { if (-not $wantAll -and -not ($boxes | Where-Object { (& $squash $_.Name) -ieq $w })) { throw "no checklist box called '$w'. The window offers: $(($boxes | ForEach-Object { $_.Name }) -join ', ')" } }
    $isWanted = { param($name) $wantAll -or ($wantList -contains (& $squash $name)) }
    # Each box is read live from its own element. Wanted boxes go on first (a prerequisite is listed
    # before what needs it); unwanted ones go off after, dependents first.
    foreach ($b in $boxes) {
        if ((& $isWanted $b.Name) -and "$($b.Toggle.Current.ToggleState)" -eq 'Off') {
            if (-not $b.Element.Current.IsEnabled) { throw "box '$($b.Name)' is wanted but disabled" }
            $b.Toggle.Toggle(); Start-Sleep -Milliseconds 300
            [void](Stamp "toggled '$($b.Name)' through UIA TogglePattern: Off -> $($b.Toggle.Current.ToggleState)")
        }
    }
    $reverse = @($boxes); [array]::Reverse($reverse)
    foreach ($b in $reverse) {
        if (-not (& $isWanted $b.Name) -and "$($b.Toggle.Current.ToggleState)" -eq 'On') {
            if (-not $b.Element.Current.IsEnabled) { throw "box '$($b.Name)' is on but disabled, so it cannot be turned off" }
            $b.Toggle.Toggle(); Start-Sleep -Milliseconds 300
            [void](Stamp "toggled '$($b.Name)' through UIA TogglePattern: On -> $($b.Toggle.Current.ToggleState)")
        }
    }
    $final = Get-CheckBoxes $win
    Show-Boxes 'final' $final
    foreach ($b in $final) {
        $want = if (& $isWanted $b.Name) { 'On' } else { 'Off' }
        if ($b.State -ne $want) { throw "box '$($b.Name)' reads $($b.State), wanted $want" }
    }
    $final
}

$outcome = 'error'
$failure = $null
$pressedAt = $null
$window = $null
$lastGood = $null
$lastGoodAt = $null
try {
    Wait-Tending { Test-Path (Join-Path $runDir 'started.txt') } 300
    Remove-Item $manifest -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path (Join-Path $runDir 'started.txt'))) { throw 'the harness never started (a dialog the driver left alone? see driver.txt)' }
    $meta['startedAt'] = (Get-Content (Join-Path $runDir 'started.txt') -Raw).Trim()
    [void](Stamp 'harness started; temporary manifest removed')
    $journal = Find-Journal $Year $revitStart $runDir
    if ($journal) { [void](Stamp "journal: $($journal.Path) (recording started $($journal.Started.ToString('HH:mm:ss.fff')), $([math]::Round($journal.Delta, 1)) s after the process)") }

    if ($Phase -eq 'layers') {
        $nextBeat = 60
        while (-not (Test-Path $doneFile) -and $runClock.Elapsed.TotalSeconds -lt $TimeoutSeconds -and -not $revit.HasExited) {
            Tend; Start-Sleep -Milliseconds 250
            if ($clock.Elapsed.TotalSeconds -ge $nextBeat) { $nextBeat += 60; [void](Stamp ("log: " + ((Get-LogTail 1) -join ' ').Trim())) }
        }
    }
    else {
        Wait-Tending { (Test-Path (Join-Path $runDir 'window-open.txt')) -or (Test-Path $doneFile) } 600
        if (-not (Test-Path (Join-Path $runDir 'window-open.txt'))) { throw 'the import window never opened (see harness.txt)' }
        $main = Get-MainWindow
        [void][Q.W2]::RestoreParked($main, $spot.X, $spot.Y)
        [void](Stamp 'restored the main window parked (not activated)')
        for ($i = 0; $i -lt 150 -and -not ($window -and (Get-CheckBoxes $window).Count -gt 0); $i++) { Tend; $window = Get-ImportWindow; if (-not $window) { Start-Sleep -Milliseconds 200 } }
        if (-not $window) { throw 'the import window was not found by UI Automation' }
        Tend
        $winHwnd = [IntPtr]$window.Current.NativeWindowHandle
        Get-TreeDump $window | Set-Content (Join-Path $runDir 'window-tree-checklist.txt') -Encoding UTF8
        [void](Set-Boxes $window $Boxes)
        $cond = New-Object Windows.Automation.AndCondition(
            (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'Import')),
            (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)))
        $button = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
        $pattern = $null
        if (-not ($button -and $button.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern))) { throw 'no invokable Import button found (no mouse fallback, by design)' }
        [void](Stamp "Import button enabled: $($button.Current.IsEnabled)")
        if (-not $button.Current.IsEnabled) { throw 'the Import button is disabled' }

        $windowLog = Join-Path $runDir 'window.txt'
        "# elapsed_s`tclock`tcurrent_step`tstatus_line`tstates`tprogress_bar`tclose_button`thung_main`thung_window`tuia_ms`tstale_s`tlog_step`tnote" | Set-Content $windowLog -Encoding UTF8

        # The window is read on a thread of its own: a UI Automation call to a window whose thread is inside a
        # long commit blocks until the commit ends (or times out after about a minute), and the 5 s lines
        # must not wait for that. The driver asks for a read each tick when the window is not hung, writes
        # the line with the newest completed reading, and says how old it is (stale_s).
        $shared = [hashtable]::Synchronized(@{ Request = $false; Busy = $false; Reading = $null; ReadAt = $null; UiaMs = 0; Error = ''; Stop = $false; Hwnd = [int64]$winHwnd; BusySince = $null })
        $readerRunspace = [runspacefactory]::CreateRunspace()
        $readerRunspace.ApartmentState = 'MTA'
        $readerRunspace.Open()
        $reader = [powershell]::Create()
        $reader.Runspace = $readerRunspace
        [void]$reader.AddScript({
            param($shared, $lib)
            . $lib
            while (-not $shared.Stop) {
                if (-not $shared.Request) { Start-Sleep -Milliseconds 40; continue }
                $shared.Request = $false; $shared.Busy = $true; $shared.BusySince = Get-Date
                try {
                    $w = [Windows.Automation.AutomationElement]::FromHandle([IntPtr][int64]$shared.Hwnd)
                    $sw = [Diagnostics.Stopwatch]::StartNew()
                    $r = Get-WindowReading $w
                    $shared.UiaMs = [int]$sw.ElapsedMilliseconds; $shared.Reading = $r; $shared.ReadAt = Get-Date; $shared.Error = ''
                }
                catch { $shared.Error = $_.Exception.Message.Split("`n")[0] }
                $shared.Busy = $false
            }
        }).AddArgument($shared).AddArgument((Join-Path $root 'lib\window-automation.ps1'))
        $readerHandle = $reader.BeginInvoke()

        $press = [Diagnostics.Stopwatch]::StartNew()
        $pressedAt = Get-Date
        $pattern.Invoke()
        [void](Stamp 'pressed Import through UIA InvokePattern')
        $meta['pressedAt'] = (Iso $pressedAt)
        Tend
        if ($Minimize) { [void][Q.W2]::ShowWindow($main, 7); [void](Stamp 'minimized the main window (SW_SHOWMINNOACTIVE)') }

        $nextSample = 0.0
        $wroteTree = $false
        while ($true) {
            if (Test-Path $doneFile) { $outcome = 'done'; break }
            if ($revit.HasExited) { $outcome = 'revit-exited'; break }
            if ($runClock.Elapsed.TotalSeconds -ge $TimeoutSeconds) { $outcome = 'timeout'; break }
            Tend
            $t = $press.Elapsed.TotalSeconds
            if ($t -ge $nextSample) {
                $nextSample = [math]::Max($nextSample + $SampleSeconds, $t + 1)
                $sampleAt = Get-Date
                $hm = Test-Hung (Get-MainWindow); $hw = Test-Hung $winHwnd
                if (-not $shared.Busy -and -not $shared.Request -and -not $hm -and -not $hw) {
                    $shared.Request = $true
                    $waitEnd = $press.Elapsed.TotalSeconds + 2.0
                    while (($shared.Request -or $shared.Busy) -and $press.Elapsed.TotalSeconds -lt $waitEnd) { Start-Sleep -Milliseconds 25 }
                }
                $g = $shared.Reading
                $note = ''
                if ($shared.Busy) { $note = "a read has been waiting on the window for $([int]($sampleAt - $shared.BusySince).TotalSeconds) s; newest completed reading shown" }
                elseif ($hm -or $hw) { $note = 'window not responding: not read, newest completed reading shown' }
                elseif ($shared.Error) { $note = 'UIA: ' + $shared.Error }
                $stale = if ($shared.ReadAt) { ([double][math]::Max(0, ($sampleAt - $shared.ReadAt).TotalSeconds)).ToString('F0', $inv) } else { '' }
                $logStep = Get-LogStep
                (@(([double]$t).ToString('F1', $inv), $sampleAt.ToString('HH:mm:ss', $inv), $g.Current, $g.Status, $g.States, $g.Bar, $g.Close, [int]$hm, [int]$hw, $shared.UiaMs, $stale, $logStep, $note) -join "`t") | Add-Content $windowLog -Encoding UTF8
                if (-not $wroteTree -and $g) {
                    $wroteTree = $true
                    [void](Stamp 'first window reading taken')
                }
            }
            Start-Sleep -Milliseconds 250
        }
        $shared.Stop = $true
        [void]$reader.BeginStop($null, $null)
        if ($outcome -eq 'done') {
            Start-Sleep -Milliseconds 1500
            try {
                $final = Get-WindowReading $window
                $report = Get-ReportText $window
                @("final reading at $(Get-Date -Format 'HH:mm:ss')", "current: $($final.Current)", "status: $($final.Status)", "states: $($final.States)", "close button: $($final.Close)", "bar: $($final.Bar)", '', '--- report as the window shows it ---', $report) | Set-Content (Join-Path $runDir 'window-final.txt') -Encoding UTF8
                ("{0}`t{1}`t{2}`t{3}`t{4}`t{5}`t{6}`t0`t0`t0`t0`t{7}`tfinal reading after the import ended" -f $press.Elapsed.TotalSeconds.ToString('F1', $inv), (Get-Date).ToString('HH:mm:ss', $inv), $final.Current, $final.Status, $final.States, $final.Bar, $final.Close, (Get-LogStep)) | Add-Content $windowLog -Encoding UTF8
            }
            catch { [void](Stamp "final window reading failed: $($_.Exception.Message.Split("`n")[0])") }
        }
    }
    if ($outcome -eq 'error') {
        $outcome = if (Test-Path $doneFile) { 'done' } elseif ($revit.HasExited) { 'revit-exited' } else { 'timeout' }
    }
    [void](Stamp "loop ended: $outcome; done.txt=$(Test-Path $doneFile); exited=$($revit.HasExited); cursor $cursor0 -> $(Get-Cursor); foreground $fore0 -> $(Get-ForegroundOwner)")
}
catch {
    $failure = $_
    $outcome = 'error'
    [void](Stamp "ERROR: $($_.Exception.Message)")
}
finally {
    $meta['outcome'] = $outcome
    $meta['endedAt'] = (Iso (Get-Date))
    $meta['elapsedSeconds'] = [math]::Round($runClock.Elapsed.TotalSeconds, 1)
    try {
        if (-not (Test-Path $doneFile) -and -not $revit.HasExited) {
            foreach ($h in (Get-PidWindows $revit.Id)) { [void](Stamp "window: '$(Get-WindowTitle $h)' ($(Get-WindowClass $h))") }
        }
    }
    catch { }
    if (Test-Path $manifest) { Remove-Item $manifest -Force -ErrorAction SilentlyContinue }
    if ($madeAddins -and (Test-Path $addinsFolder) -and -not (Get-ChildItem $addinsFolder -Force)) { Remove-Item $addinsFolder -ErrorAction SilentlyContinue }
    if ($outcome -eq 'done') { Start-Sleep -Seconds 2 }
    $exitCode = $null
    if ($revit.HasExited) { $exitCode = $revit.ExitCode }
    else { Stop-Process -Id $revit.Id -Force; [void](Stamp "killed launched Revit pid $($revit.Id)") }
    [void]$revit.WaitForExit(30000)
    if ($null -ne $exitCode) { $meta['revitExitCodeBeforeKill'] = $exitCode }
    Remove-Item (Join-Path $pidDir "$($revit.Id).json") -Force -ErrorAction SilentlyContinue

    # Revit is gone: put the user's IFC cache back as it was, before meta.json records how that went
    Restore-IfcCache
    $meta['ifcCacheRestored'] = $ifcLink.restored

    # what the run left behind: the harness's result, the plugin's log, Revit's journal
    if (Test-Path (Join-Path $runDir 'result.json')) { $meta['result'] = Get-Content (Join-Path $runDir 'result.json') -Raw | ConvertFrom-Json }
    try { if (Test-Path -LiteralPath $pluginLog) { Copy-Shared $pluginLog (Join-Path $runDir 'import.log') } else { [void](Stamp 'no plugin log beside the zip copy') } }
    catch { [void](Stamp "could not copy the plugin log: $($_.Exception.Message)") }
    try {
        $j = Find-Journal $Year $revitStart $runDir
        if ($j) {
            Copy-Shared $j.Path (Join-Path $runDir 'journal.txt')
            "$($j.Path)`nrecording started $($j.Started.ToString('yyyy-MM-dd HH:mm:ss.fff')); process started $(Iso $revitStart); delta $([math]::Round($j.Delta, 2)) s; candidates $($j.Candidates)" | Set-Content (Join-Path $runDir 'journal-source.txt')
            $meta['journal'] = $j.Path
        }
        else { [void](Stamp 'no journal found for this process') }
    }
    catch { [void](Stamp "could not copy the journal: $($_.Exception.Message)") }

    # -StopBefore: the harness wrote which steps ran (stop-before.txt, result.json). Here the plugin's own log is asked
    # whether the named step ever announced itself: a step that never started writes no "[Kind] started." marker.
    if ($StopBefore) {
        try {
            $sr = $meta['result']
            $stopKind = if ($sr) { "$($sr.stop_before_step)" } else { '' }
            $logPath = Join-Path $runDir 'import.log'
            $line = ''
            if (-not $stopKind) { $line = "stop-before: no boundary was reached (see stop-before.txt and result.json); the named layer was $StopBefore" }
            elseif (-not (Test-Path -LiteralPath $logPath)) { $line = "stop-before: no plugin log to check '$stopKind' against" }
            else {
                $logText = Get-Content -LiteralPath $logPath -Raw -Encoding UTF8
                $markers = [regex]::Matches($logText, '\[' + [regex]::Escape($stopKind) + '\] started\.').Count
                $ranKinds = @($sr.steps_ran | ForEach-Object { ("$_" -split '=')[0] } | Where-Object { $_ })
                $unmarked = @($ranKinds | Where-Object { $logText -notmatch ('\[' + [regex]::Escape($_) + '\] started\.') })
                $line = "stop-before: '$stopKind' has $markers started-marker(s) in import.log (must be 0); $($ranKinds.Count) earlier steps ran, $($unmarked.Count) of them without a started-marker (must be 0)"
            }
            [void](Stamp $line)
            Add-Content -LiteralPath (Join-Path $runDir 'stop-before.txt') -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue
        }
        catch { [void](Stamp "stop-before log check failed: $($_.Exception.Message.Split("`n")[0])") }
    }

    # the checkpoint moves into place only when the import finished and the harness saved it
    $saved = Join-Path $runDir 'checkpoint-saving.rvt'
    if ($savePath -and (Test-Path $saved) -and $meta['result'] -and $meta['result'].checkpoint_saved) {
        Move-Item -LiteralPath $saved -Destination $savePath -Force
        $meta['checkpointWritten'] = $savePath
        $sidecar = [ordered]@{
            checkpoint = $savePath; year = $Year; tag = $Tag; sourceCommit = $build.sourceCommit; layers = $Layers
            openedFrom = $openPath; bundleSha256 = $bundleSha; bundleSource = $Bundle; createdAt = (Iso (Get-Date)); runDir = $runDir
            revitBuild = $meta['result'].revit_build; importWallSeconds = $meta['result'].wall_seconds
            extractedFolder = $extractedDir
            note = 'The project links (an IFC site model) point into extractedFolder (%LOCALAPPDATA%\MantlePlace\bundles\<order id>\extracted), which every run of this order shares. A run with -ColdIfc builds its own Site.<year>.rvt there and then puts the earlier file back, so this link resolves to the file that was there before that run.'
        }
        if ($StopBefore) {
            $sidecar['stopBefore'] = $StopBefore
            $sidecar['stopBeforeStep'] = "$($meta['result'].stop_before_step)"
            $sidecar['stepsRan'] = @($meta['result'].steps_ran)
            $sidecar['stepsNotRun'] = @($meta['result'].steps_not_run)
            $sidecar['stopNote'] = 'Saved by stepping the staged import to the boundary in front of stopBeforeStep: every step in stepsRan committed, none of stepsNotRun started, no transaction open, and the end-of-import Finish() (which commits terrain smooth shading; a full import commits it inside the drape step) was never called.'
        }
        $sidecar | ConvertTo-Json | Set-Content -Encoding UTF8 ([IO.Path]::ChangeExtension($savePath, '.json'))
        [void](Stamp "checkpoint written: $savePath")
    }
    elseif ($savePath) { [void](Stamp 'no checkpoint was written') }

    # a cutoff is a measurement: say where everything stood
    if ($outcome -eq 'timeout' -or $outcome -eq 'revit-exited') {
        $t = @("outcome: $outcome after $([math]::Round($runClock.Elapsed.TotalSeconds)) s of a $TimeoutSeconds s limit (counted from launching Revit)")
        if ($null -ne $pressedAt) { $t += "Import was pressed at $(Iso $pressedAt); $([math]::Round(((Get-Date) - $pressedAt).TotalSeconds)) s later" }
        if ($null -ne $exitCode) { $t += "Revit exited by itself with code $exitCode (0xC0000005/0xe0434352 style codes are crashes; see the journal)" }
        $t += '', 'plugin log, last lines:'; $t += (Get-LogTail 6) | ForEach-Object { "  $_" }
        $t += '', "step the plugin log was in: $(Get-LogStep)"
        if ($shared -and $shared.Reading) {
            $seen = $shared.Reading
            $t += '', "window, newest completed reading ($([math]::Round(((Get-Date) - $shared.ReadAt).TotalSeconds)) s before the cutoff): current step '$($seen.Current)', status '$($seen.Status)'", "window states: $($seen.States)"
        }
        if (Test-Path (Join-Path $runDir 'window.txt')) { $t += '', 'window.txt, last 3 lines (hung_main / hung_window are IsHungAppWindow):'; $t += (Get-Content (Join-Path $runDir 'window.txt') -Tail 3) | ForEach-Object { "  $_" } }
        if (Test-Path (Join-Path $runDir 'journal.txt')) { $t += '', 'journal, last 30 lines:'; $t += (Get-Content (Join-Path $runDir 'journal.txt') -Tail 30) | ForEach-Object { "  $_" } }
        $t | Set-Content (Join-Path $runDir 'timeout.txt') -Encoding UTF8
        [void](Stamp 'wrote timeout.txt')
    }

    # the cache path after the restore (a cold run has put the original back, so this should match originalBytes/writtenBefore)
    try {
        if ($ifcLink.companion) {
            $after = Get-Item -LiteralPath $ifcLink.companion -Force -ErrorAction SilentlyContinue
            $ifcLink['existsAfter'] = [bool]$after
            if ($after) { $ifcLink['bytesAfter'] = $after.Length; $ifcLink['writtenAfter'] = Iso $after.LastWriteTime }
        }
    }
    catch { }
    $meta | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $runDir 'meta.json') -Encoding UTF8
    try {
        $py = (Get-Command python -ErrorAction Stop).Source
        & $py (Join-Path $root 'timings.py') $runDir 2>&1 | ForEach-Object { [void](Stamp "timings.py: $_") }
    }
    catch { [void](Stamp "timings.py failed: $($_.Exception.Message)") }

    if (-not $KeepFiles) {
        Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
        if (-not $savePath) { Remove-Item -LiteralPath (Join-Path $bundleDir 'extracted') -Recurse -Force -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath (Join-Path $runDir 'project.rvt') -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $addinDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
}
finally {
    # The safety net for the try that opened before Move-IfcAside: any way out of the launch-and-run body that did
    # not pass through the inner finally (a throw before Revit existed, a stop between the launch and the inner
    # try) ends here. A no-op when Restore-IfcCache already ran.
    Exit-LaunchLock                                                                     # never held past a throw
    if (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest -Force -ErrorAction SilentlyContinue }   # nor the manifest, whatever state the IFC cache is in
    if ($script:ifcCacheState -in @('armed', 'failed')) {
        $orphan = $null
        try { if ($revit -and -not $revit.HasExited) { $orphan = $revit } } catch { }
        if ($orphan) {
            Stamp "the inner cleanup did not run and Revit pid $($orphan.Id) (launched by this run) is still up: stopping it so the IFC cache can be put back"
            Stop-Process -Id $orphan.Id -Force -ErrorAction SilentlyContinue
            [void]$orphan.WaitForExit(30000)
        }
        if (Test-Path -LiteralPath $manifest) { Remove-Item $manifest -Force -ErrorAction SilentlyContinue }
        Restore-IfcCache
    }
}
[void](Stamp "run dir: $runDir")
Get-ChildItem $runDir | Select-Object Name, Length | Format-Table -AutoSize | Out-String -Width 200
if ($failure) { throw $failure }
