# Runs the Civil 3D probe once in a host of its own and prints its result.md.
#
#   powershell -NoProfile -File ./Invoke-Civil3dProbe.ps1 -Year 2027 -Mode civil3d -WorkRoot <dir outside the repo>
#
# -Mode civil3d starts the host with the Civil 3D launcher's switches (/ld AecBase.dbx, the C3D
# profile, /product C3D); -Mode as-autocad with the "as AutoCAD" launcher's (/product ACAD), over the
# same install. -Shell console (the default) runs accoreconsole.exe, headless; -Shell gui runs acad.exe
# minimised. Either way a script NETLOADs the probe and runs MANTLEPLACEC3DPROBE.
#
# One host of a kind at a time: it refuses while another of its shell runs. AutoCAD loads an
# unsigned assembly only from a trusted folder, and the console answers the secure-load prompt "do not
# load" without saying so ("Unable to load ... assembly"). So the script appends the run's addin folder
# to TRUSTEDPATHS for the NETLOAD alone and puts the profile's own value back on the next line, before
# the probe runs. Only the host this script started is stopped.
param(
    [Parameter(Mandatory)][ValidateSet('2025', '2026', '2027')][string]$Year,
    [Parameter(Mandatory)][ValidateSet('civil3d', 'as-autocad')][string]$Mode,
    [Parameter(Mandatory)][string]$WorkRoot,
    [ValidateSet('console', 'gui')][string]$Shell = 'console',
    [string]$AcadDir = (Join-Path $env:ProgramFiles "Autodesk\AutoCAD $Year"),
    [string]$Zone = 'CA83IIIF',
    [string]$MismatchZone = 'CA83-IVF',
    [string]$DemZone = 'UTM83-10',
    [switch]$Force,
    [int]$TimeoutSeconds = 600
)
$ErrorActionPreference = 'Stop'

$name = $(if ($Shell -eq 'console') { 'accoreconsole' } else { 'acad' })
$running = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
if ($running) { throw "$name is running (pid $($running.Id -join ', ')); one at a time." }
$exe = Join-Path $AcadDir "$name.exe"
if (-not (Test-Path $exe)) { throw "no $exe" }

# The framework the install's own managed tools run on is the one its API assemblies need.
$tfms = @(Get-ChildItem $AcadDir -Filter *.runtimeconfig.json | ForEach-Object {
        try { (Get-Content $_.FullName -Raw | ConvertFrom-Json).runtimeOptions.tfm } catch { }
    } | Where-Object { $_ -match '^net\d+\.\d+$' } | Sort-Object { [version]($_ -replace '^net', '') } -Unique)
if (-not $tfms) { throw "no runtime config under $AcadDir names a framework" }
$framework = "$($tfms[-1])-windows"

$label = "$Year, $Mode, $Shell"
$runDir = Join-Path $WorkRoot ("c3d-$Mode-$Year-$Shell-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $runDir | Out-Null
$runDir = (Resolve-Path $runDir).Path
$built = Join-Path $runDir 'addin'
foreach ($project in 'Probe\Civil3dProbe.csproj', 'Civil\Civil3dPart.csproj') {
    dotnet build (Join-Path $PSScriptRoot $project) -c Release --no-incremental --nologo -v quiet -o $built "-p:AcadYear=$Year" "-p:AcadDir=$AcadDir" "-p:AcadFramework=$framework" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$project did not build" }
}

# A synthetic DEM, never real data: a 40 by 40 grid of 30 m cells in UTM zone 10 north, NAD83 metres.
$cells = 40
$rows = New-Object System.Collections.Generic.List[string]
$rows.Add("ncols $cells"); $rows.Add("nrows $cells"); $rows.Add('xllcorner 550000'); $rows.Add('yllcorner 4180000')
$rows.Add('cellsize 30'); $rows.Add('NODATA_value -9999')
for ($r = 0; $r -lt $cells; $r++) {
    $rows.Add((0..($cells - 1) | ForEach-Object { '{0:F2}' -f (20 + 0.5 * $_ + 0.25 * ($cells - $r) + 3 * [math]::Sin($_ / 5.0)) }) -join ' ')
}
$asc = Join-Path $runDir 'grid.asc'
[IO.File]::WriteAllLines($asc, $rows)
$dems = @($asc)
$translate = Get-Command gdal_translate -ErrorAction SilentlyContinue
if ($translate) {
    # Another installation's proj.db on PROJ_LIB (PostGIS ships one) breaks GDAL's EPSG lookup: point
    # PROJ at GDAL's own projlib for this one call.
    $projlib = Join-Path (Split-Path $translate.Source) 'projlib'
    $saved = $env:PROJ_LIB, $env:PROJ_DATA
    if (Test-Path $projlib) { $env:PROJ_LIB = $projlib; $env:PROJ_DATA = $projlib }
    $tif = Join-Path $runDir 'grid.tif'
    & $translate.Source -q -of GTiff -a_srs EPSG:26910 $asc $tif
    if ($LASTEXITCODE -eq 0) { $dems += $tif }
    $env:PROJ_LIB, $env:PROJ_DATA = $saved
}

$lispDir = $built.Replace('\', '\\')
$script = Join-Path $runDir 'probe.scr'
[IO.File]::WriteAllLines($script, @(
        '(setq mantleplace-trusted (getvar "TRUSTEDPATHS"))',
        "(setvar `"TRUSTEDPATHS`" (strcat mantleplace-trusted `";$lispDir`"))",
        "_.NETLOAD `"$(Join-Path $built 'MantlePlace.Scratch.Civil3dProbe.dll')`"",
        '(setvar "TRUSTEDPATHS" mantleplace-trusted)',
        'MANTLEPLACEC3DPROBE'))

$env:MANTLEPLACE_C3D_PROBE_DIR = $runDir
$env:MANTLEPLACE_C3D_PROBE_LABEL = "$label (built $framework)"
$env:MANTLEPLACE_C3D_PROBE_ZONE = $Zone
$env:MANTLEPLACE_C3D_PROBE_MISMATCH_ZONE = $MismatchZone
$env:MANTLEPLACE_C3D_PROBE_DEM_ZONE = $DemZone
$env:MANTLEPLACE_C3D_PROBE_DEMS = $dems -join ';'
$env:MANTLEPLACE_C3D_PROBE_FORCE = $(if ($Force) { '1' } else { '' })

# The launchers' own switches, as the Start menu shortcuts carry them.
$launch = if ($Mode -eq 'civil3d') {
    @($(if ($Shell -eq 'console') { '/loadmodule' } else { '/ld' }), "`"$(Join-Path $AcadDir 'AecBase.dbx')`"", '/p', '"<<C3D_Metric>>"', '/product', 'C3D')
}
else { @('/product', 'ACAD') }
$arguments = if ($Shell -eq 'console') { @('/s', "`"$script`"") + $launch + @('/l', 'en-US') }
else { @('/nologo', '/b', "`"$script`"") + $launch + @('/language', 'en-US') }

$log = Join-Path $runDir 'host-output.txt'
$started = @{ FilePath = $exe; ArgumentList = $arguments; WorkingDirectory = $runDir; PassThru = $true }
if ($Shell -eq 'console') { $started.NoNewWindow = $true; $started.RedirectStandardOutput = $log }
else { $started.WindowStyle = 'Minimized' }
$hostProcess = Start-Process @started
$done = Join-Path $runDir 'done.txt'
$clock = [Diagnostics.Stopwatch]::StartNew()
try {
    while (-not $hostProcess.HasExited -and $clock.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if (Test-Path $done) { [void]$hostProcess.WaitForExit(15000); break }
        Start-Sleep -Milliseconds 500
    }
}
finally {
    if (-not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force }
    'MANTLEPLACE_C3D_PROBE_DIR', 'MANTLEPLACE_C3D_PROBE_LABEL', 'MANTLEPLACE_C3D_PROBE_ZONE', 'MANTLEPLACE_C3D_PROBE_MISMATCH_ZONE',
    'MANTLEPLACE_C3D_PROBE_DEM_ZONE', 'MANTLEPLACE_C3D_PROBE_DEMS', 'MANTLEPLACE_C3D_PROBE_FORCE' | ForEach-Object { Remove-Item "env:$_" -ErrorAction SilentlyContinue }
}
$result = Join-Path $runDir 'result.md'
if (Test-Path $result) { Get-Content $result } else { Write-Output "no result.md after $([int]$clock.Elapsed.TotalSeconds) s"; if (Test-Path $log) { Get-Content $log -Encoding Unicode | Where-Object { $_.Trim() } | Select-Object -Last 30 } }
Write-Output "run folder: $runDir"
