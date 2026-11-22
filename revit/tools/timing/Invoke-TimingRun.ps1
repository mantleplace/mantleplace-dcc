[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('window', 'layers')][string]$Phase,
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][ValidateSet('2025', '2026', '2027')][string]$Year,
    [string]$SourceRoot = '',
    [string]$Commit = '',
    [string]$SourceCommit = '',
    [string]$Layers = '',
    [string]$Levels = '',
    [string]$StopBefore = '',
    [switch]$Verify,
    [string]$Boxes = 'All',
    [string]$OpenCheckpoint = '',
    [string]$SaveCheckpoint = '',
    [switch]$OverwriteCheckpoint,
    [string]$Bundle = $env:MANTLEPLACE_TIMING_BUNDLE,
    [string]$WorkRoot = '',
    [string]$RevitRoot = (Join-Path $env:ProgramFiles 'Autodesk'),
    [string]$ParkScreen = '',
    [int]$WaitForRevitSeconds = 0,
    [int]$TimeoutSeconds = 3600,
    [int]$SampleSeconds = 5,
    [switch]$Rebuild,
    [switch]$AllowDirty,
    [switch]$Minimize,
    [switch]$KeepFiles,
    [switch]$ColdIfc = $true
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
. (Join-Path $root 'lib\timing-root.ps1')
. (Join-Path $root 'lib\quiet-win32.ps1')
. (Join-Path $root 'lib\window-automation.ps1')

$clock = [Diagnostics.Stopwatch]::StartNew()
$script:driverLog = $null
$inv = [Globalization.CultureInfo]::InvariantCulture
function Stamp($text) {
    $line = "{0,7}s {1}" -f $clock.Elapsed.TotalSeconds.ToString('F0', $inv), $text
    if ($script:driverLog) { Add-Content -LiteralPath $script:driverLog -Value $line -Encoding UTF8 }
    Write-Host $line
}
function Iso([datetime]$d) { $d.ToString('yyyy-MM-ddTHH:mm:ss.fff', $inv) }

if ($Tag -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,30}$') { throw "Tag '$Tag' must match ^[A-Za-z0-9][A-Za-z0-9_-]{0,30}$" }
if ($Phase -eq 'layers' -and -not $Layers) { throw '-Layers is required for the layers phase (ImportLayer names, All, or Default).' }
if ($Phase -eq 'window' -and ($Layers -or $Levels -or $SaveCheckpoint)) { throw '-Layers, -Levels and -SaveCheckpoint belong to the layers phase; the window phase takes -Boxes and imports the levels the window opens on.' }
if ($StopBefore -and $Phase -ne 'layers') { throw '-StopBefore belongs to the layers phase.' }
if ($Verify -and ($Phase -ne 'layers' -or $StopBefore)) { throw '-Verify belongs to the layers phase, on an import that runs to its end (no -StopBefore).' }
$StopBefore = ($StopBefore -replace '\s', '')
if ($SourceRoot -and $Commit) { throw 'pass -SourceRoot or -Commit, not both.' }
if (-not $Bundle) { throw 'no bundle: pass -Bundle <zip> or set MANTLEPLACE_TIMING_BUNDLE.' }
if (-not (Test-Path -LiteralPath $Bundle)) { throw "no bundle at $Bundle" }
Assert-TimingRootReachable $WorkRoot
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

if ($SourceRoot -and (Test-Path -LiteralPath (Join-Path $SourceRoot '.git')) -and -not $AllowDirty) {
    $dirtyFiles = @(& git -C $SourceRoot status --porcelain --untracked-files=all -- revit shared 2>$null)
    if ($dirtyFiles.Count -gt 0) { throw "$SourceRoot has uncommitted changes under revit/ ($($dirtyFiles.Count) paths). Commit them and time the commit (-Commit), or pass -AllowDirty to stamp the record as dirty." }
}

$buildDir = Join-Path $work "builds\$Tag"
$buildJson = Join-Path $buildDir 'build.json'
if ($Commit) { $SourceRoot = @(& (Join-Path $root 'Export-TimingSource.ps1') -Commit $Commit -WorkRoot $work)[-1] }
if ($Rebuild -or -not (Test-Path $buildJson)) {
    if (-not $SourceRoot) { throw "tag '$Tag' has no build yet: pass -Commit <sha> or -SourceRoot <dir containing revit\src>" }
    & (Join-Path $root 'Build-TimingHarness.ps1') -Tag $Tag -SourceRoot $SourceRoot -SourceCommit $SourceCommit -WorkRoot $work -Force:$Rebuild
}
$build = Get-Content $buildJson -Raw | ConvertFrom-Json
$dirtyTree = [string]$build.sourceCommit -like '*-dirty'
if ($dirtyTree -and -not $AllowDirty) { throw "tag '$Tag' was built from a dirty tree ($($build.sourceCommit)). Rebuild from a commit, or pass -AllowDirty to stamp the record as dirty." }
if ($SourceRoot -and (Resolve-Path -LiteralPath $SourceRoot).Path.TrimEnd('\') -ne $build.sourceRoot) {
    throw "tag '$Tag' is built from $($build.sourceRoot), not $SourceRoot. Use another tag, or -Rebuild if the tag is yours alone."
}
$outDir = Join-Path $buildDir 'out'
$dllName = "$($build.assemblyName).dll"
if ((Get-FileHash (Join-Path $outDir $dllName) -Algorithm SHA256).Hash -ne $build.dllSha256) { throw "$outDir\$dllName does not match build.json; rebuild the tag (-Rebuild)" }

if ($Phase -eq 'layers') {
    $enumFile = Join-Path $build.sourceRoot 'shared\src\MantlePlace.Core\ImportLayers.cs'
    if (-not (Test-Path $enumFile)) { $enumFile = Join-Path $build.sourceRoot 'revit\src\MantlePlace.Revit.Core\ImportLayers.cs' }
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
    if ($StopBefore -and $Layers -notin @('All', 'Default') -and (@($Layers -split ',' | ForEach-Object { ($_.Trim() -replace '\s', '') } | Where-Object { $_ }) -notcontains $StopBefore)) {
        throw "-StopBefore $StopBefore is not among -Layers ($Layers), so the plan has no step to stop in front of."
    }
}

$pidDir = Join-Path $work 'pids'
New-Item -ItemType Directory -Force $pidDir | Out-Null
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
function Get-OtherRevits {
    @($script:ours.GetEnumerator() | Sort-Object Key | ForEach-Object { "pid $($_.Key), Revit $($_.Value.year), a run of this harness, tag $($_.Value.tag)" }) +
    @($script:foreign | ForEach-Object {
        $path = ''; try { $path = $_.Path } catch { }
        $year = if ($path -match 'Revit (\d{4})') { "Revit $($Matches[1])" } else { 'Revit' }
        "pid $($_.Id), $year, '$($_.MainWindowTitle)', not launched by this tool"
    })
}
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
function Test-ImportsSiteModel {
    $wanted = if ($Phase -eq 'layers') { $Layers } else { $Boxes }
    if ($wanted -ieq 'All') { return $true }
    if ($wanted -ieq 'Default') { return $false }
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
$script:ifcCacheState = 'none'
$extractedDir = ''
$ifcLink.appliesToThisRun = [bool](Test-ImportsSiteModel)
$orderId = ''
try { $orderId = Get-BundleOrderId $Bundle } catch { $ifcLink.summary = "unknown: could not read the bundle's manifest ($($_.Exception.Message.Split("`n")[0]))" }
if ($orderId) {
    $ifcLink.orderId = $orderId
    if ($orderId -notmatch '^[A-Za-z0-9._-]+$' -or $orderId -in @('.', '..')) {
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
    foreach ($earlier in @(Get-ChildItem (Join-Path $work 'runs') -Directory -ErrorAction SilentlyContinue)) {
        $stranded = Join-Path $earlier.FullName "ifc-cache-original\Site.$Year.rvt"
        if (-not (Test-Path -LiteralPath $stranded -PathType Leaf)) { continue }
        $theirs = ''
        try { $theirs = "$((Get-Content (Join-Path $earlier.FullName 'ifc-cache.json') -Raw | ConvertFrom-Json).orderId)" } catch { }
        if ($theirs -and $theirs -ne $orderId) { continue }
        throw "the user's original Site.$Year.rvt was left in $stranded by an earlier cold run (see ifc-cache.json beside it) and the cache path now holds that run's rebuilt copy. Copy the original back to $($ifcLink.companion), check its sha256 against ifc-cache.json, then delete that ifc-cache-original folder. Or pass -ColdIfc:`$false."
    }
}

function Get-FileSha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Save-IfcRecord { $ifcLink | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runDir 'ifc-cache.json') -Encoding UTF8 }
function Set-ResultFields([hashtable]$Fields) {
    $path = Join-Path $runDir 'result.json'
    $obj = if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    foreach ($k in $Fields.Keys) {
        if ($obj.PSObject.Properties[$k]) { $obj.$k = $Fields[$k] } else { $obj | Add-Member -NotePropertyName $k -NotePropertyValue $Fields[$k] }
    }
    $obj | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $path -Encoding UTF8
}

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
        $script:ifcCacheState = 'armed'
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
                Save-IfcRecord
                Remove-Item -LiteralPath $companion -Force
                $ifcLink.movedAside = $true
                Save-IfcRecord
            }
            catch {
                if (-not $ifcLink.movedAside) { Remove-Item -LiteralPath $kept -Force -ErrorAction SilentlyContinue; $ifcLink.originalKeptAt = '' }
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

function Restore-IfcCache {
    if ($script:ifcCacheState -notin @('armed', 'failed')) { return }
    $companion = $ifcLink.companion
    $kept = Join-Path $runDir "ifc-cache-original\Site.$Year.rvt"
    $runKeepDir = Join-Path $runDir 'ifc-cache-run'
    $lastError = ''
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
                try { [IO.Directory]::Delete((Split-Path $kept)) } catch { }
                $ifcLink.restored = $true
                $ifcLink.restoreStatus = "restored: the original is back at $companion, byte-identical ($($ifcLink.originalBytes) bytes, sha256 $($ifcLink.originalSha256))"
            }
            elseif ($ifcLink.existedBefore) {
                $ifcLink.restored = $true
                $ifcLink.restoreStatus = 'restored: the file never left the cache'
            }
            else {
                if (Test-Path -LiteralPath $companion -PathType Leaf) { Remove-Item -LiteralPath $companion -Force }
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

$runsRoot = Join-Path $work 'runs'
New-Item -ItemType Directory -Force $runsRoot | Out-Null
$runDir = Join-Path $runsRoot "$Tag-$Year"
if ((Test-Path $runDir) -and (Get-ChildItem $runDir -Force | Select-Object -First 1)) {
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
    if ($Verify) { $envMap['MANTLEPLACE_TIMING_VERIFY'] = '1' }
    if ($savePath) { $envMap['MANTLEPLACE_TIMING_SAVE_CHECKPOINT'] = (Join-Path $runDir 'checkpoint-saving.rvt') }
}
$envBlock = [string[]]@($envMap.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })

$script:launchLock = $null
function Enter-LaunchLock {
    $m = New-Object Threading.Mutex($false, "Global\MantlePlaceTimingLaunch")
    $got = $false
    try { $got = $m.WaitOne(300000) } catch [Threading.AbandonedMutexException] { $got = $true }
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

try {
Enter-LaunchLock
Read-LiveRevits
$otherRevits = @(Get-OtherRevits)
if ($otherRevits.Count -gt 0) {
    Remove-Item -LiteralPath $zip, $addinDir -Recurse -Force -ErrorAction SilentlyContinue
    throw "another Revit came up while this run was preparing to launch ($($otherRevits -join '; ')). Not launching beside it; try again when it has exited."
}
Move-IfcAside

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
if ($madeAddins) { New-Item -ItemType Directory -Force $addinsFolder | Out-Null }
$manifestXml | Set-Content -Encoding UTF8 $manifest
$launchedAt = Get-Date
$created = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$exe`" /nosplash"; CurrentDirectory = (Split-Path $exe); ProcessStartupInformation = $startup }
if ($created.ReturnValue -ne 0) {
    Remove-Item $manifest -Force -ErrorAction SilentlyContinue
    throw "WMI would not start Revit (Win32_Process.Create returned $($created.ReturnValue))"
}
$revit = Get-Process -Id $created.ProcessId
$runClock = [Diagnostics.Stopwatch]::StartNew()
$revitStart = $revit.StartTime
[ordered]@{ tag = $Tag; year = $Year; runDir = $runDir; driverPid = $PID } | ConvertTo-Json | Set-Content (Join-Path $pidDir "$($revit.Id).json")
Set-Content (Join-Path $runDir 'revit.pid') $revit.Id
Exit-LaunchLock
Stamp "Revit $Year pid $($revit.Id), minimized; parking at $($spot.X),$($spot.Y) ($($spot.Screen)); cursor $cursor0; foreground $fore0"

$doneFile = Join-Path $runDir 'done.txt'
$meta = [ordered]@{
    tag = $Tag; year = $Year; phase = $Phase
    sourceRoot = $build.sourceRoot; sourceCommit = $build.sourceCommit; dirtyTree = $dirtyTree; dllSha256 = $build.dllSha256
    layers = $Layers; levels = $Levels; boxes = $(if ($Phase -eq 'window') { $Boxes } else { $null }); stopBefore = $StopBefore
    openCheckpoint = $openPath; saveCheckpoint = $savePath
    bundleSource = $Bundle; bundleBytes = $sourceInfo.Length; bundleSha256 = $bundleSha
    timeoutSeconds = $TimeoutSeconds; sampleSeconds = $SampleSeconds
    revitPid = $revit.Id; launchedAt = (Iso $launchedAt); revitProcessStart = (Iso $revitStart)
    driverShell = "PowerShell $($PSVersionTable.PSVersion)"
    compiledTag = $(if ($build.PSObject.Properties['compiledTag']) { "$($build.compiledTag)" } else { '' })
    extractedFolder = $extractedDir
    ifcLink = $ifcLink
    outcome = 'error'
}

$parked = @{}
$script:stolen = $false
$dialogClass = [char]35 + '32770'
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

    Restore-IfcCache
    $meta['ifcCacheRestored'] = $ifcLink.restored

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
    Exit-LaunchLock
    if (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest -Force -ErrorAction SilentlyContinue }
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
