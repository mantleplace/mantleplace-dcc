# Runs the carrier probe once in a real Revit of its own, quietly, and prints its result.md.
#
#   pwsh ./Invoke-CarrierProbe.ps1 -Year 2027 -Mode gpkg -Bundle <extracted bundle> -Carriers <make_carriers.py out> -WorkRoot <dir>
#
# One Revit at a time: it refuses while any Revit runs. It writes a temporary manifest to
# %APPDATA%\Autodesk\Revit\Addins\<year>\MantlePlace.CarrierProbe.addin, removed as soon as the probe
# reports started.txt and again when the run ends; launches Revit minimised through WMI with the
# probe's variables; answers only the unsigned add-in prompt (Load Once), as the timing harness does,
# from whose lib/ it borrows the Win32 helpers; and stops only the Revit it launched.
param(
    [Parameter(Mandatory)][ValidateSet('2025', '2026', '2027')][string]$Year,
    [Parameter(Mandatory)][ValidateSet('geojson', 'gpkg', 'dxf', 'ifc')][string]$Mode,
    [Parameter(Mandatory)][string]$Bundle,
    [Parameter(Mandatory)][string]$Carriers,
    [Parameter(Mandatory)][string]$WorkRoot,
    [int]$TimeoutSeconds = 900
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\timing\lib\quiet-win32.ps1')
. (Join-Path $PSScriptRoot '..\timing\lib\window-automation.ps1')

$running = @(Get-Process -Name Revit -ErrorAction SilentlyContinue)
if ($running) { throw "Revit is running (pid $($running.Id -join ', ')); one Revit at a time." }

$exe = Join-Path $env:ProgramFiles "Autodesk\Revit $Year\Revit.exe"
$project = Join-Path $PSScriptRoot 'Probe\CarrierProbe.csproj'
dotnet build $project -c Release --nologo -v quiet | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'the probe did not build' }
$built = Join-Path $PSScriptRoot 'Probe\bin\Release\net8.0-windows'

$runDir = Join-Path $WorkRoot ("carrier-$Mode-$Year-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force (Join-Path $runDir 'addin') | Out-Null
Copy-Item (Join-Path $built '*') (Join-Path $runDir 'addin') -Recurse

$manifest = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year\MantlePlace.CarrierProbe.addin"
@"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Mantle Place carrier probe (scratch)</Name>
    <Assembly>$runDir\addin\MantlePlace.Scratch.CarrierProbe.dll</Assembly>
    <AddInId>6f1d2c1e-7a35-4f0b-9b8e-3c2a51a0c373</AddInId>
    <FullClassName>MantlePlace.Revit.CarrierSpike.CarrierProbe</FullClassName>
    <VendorId>MNTL</VendorId>
  </AddIn>
</RevitAddIns>
"@ | Set-Content -Encoding UTF8 $manifest

$envMap = [ordered]@{}
foreach ($kv in [Environment]::GetEnvironmentVariables().GetEnumerator()) {
    if ("$($kv.Key)".StartsWith('=') -or $kv.Key -like 'MANTLEPLACE_*') { continue }
    $envMap[$kv.Key] = $kv.Value
}
$envMap['MANTLEPLACE_CARRIER_PROBE_DIR'] = $runDir
$envMap['MANTLEPLACE_CARRIER_PROBE_MODE'] = $Mode
$envMap['MANTLEPLACE_CARRIER_PROBE_BUNDLE'] = (Resolve-Path $Bundle).Path
$envMap['MANTLEPLACE_CARRIER_PROBE_CARRIERS'] = (Resolve-Path $Carriers).Path
$envBlock = [string[]]@($envMap.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })

$spot = Get-ParkingSpot
$startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]7; EnvironmentVariables = $envBlock }
$launched = Get-Date
$created = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$exe`" /nosplash"; CurrentDirectory = (Split-Path $exe); ProcessStartupInformation = $startup }
if ($created.ReturnValue -ne 0) { Remove-Item $manifest -Force; throw "WMI would not start Revit ($($created.ReturnValue))" }
$revit = Get-Process -Id $created.ProcessId
$log = Join-Path $runDir 'driver.txt'
function Stamp([string]$line) { Add-Content $log "$((Get-Date).ToString('HH:mm:ss')) $line" }
Stamp "Revit $Year pid $($revit.Id), mode $Mode"

$dialogClass = [char]35 + '32770'
$parked = @{}
$clock = [Diagnostics.Stopwatch]::StartNew()
try {
    while (-not (Test-Path (Join-Path $runDir 'done.txt')) -and -not $revit.HasExited -and $clock.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if ((Test-Path $manifest) -and (Test-Path (Join-Path $runDir 'started.txt'))) { Remove-Item $manifest -Force; Stamp 'probe started; manifest removed' }
        foreach ($h in (Get-PidWindows $revit.Id)) {
            if ((Get-WindowClass $h) -eq $dialogClass) {
                $el = [Windows.Automation.AutomationElement]::FromHandle($h)
                $cond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, 'CommandButton_1002')
                $button = $null; try { $button = $el.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond) } catch { }
                if ($button -and $button.Current.Name -eq 'Load Once') {
                    [void][Q.W2]::SendMessage($h, 0x0400 + 102, [IntPtr]1002, [IntPtr]::Zero)
                    Stamp "answered Load Once on '$(Get-WindowTitle $h)'"
                    continue
                }
                if (-not $parked.ContainsKey("d$h")) { $parked["d$h"] = 1; Stamp "left alone: dialog '$(Get-WindowTitle $h)'" }
            }
            if (-not [Q.W2]::IsIconic($h) -and -not $parked.ContainsKey("$h") -and -not (Test-Hung $h)) { Move-Parked $h $spot; $parked["$h"] = 1 }
        }
        Start-Sleep -Milliseconds 200
    }
    Stamp "ended after $([int]$clock.Elapsed.TotalSeconds) s: $(if (Test-Path (Join-Path $runDir 'done.txt')) { Get-Content (Join-Path $runDir 'done.txt') } else { 'no done.txt' })"
}
finally {
    Remove-Item $manifest -Force -ErrorAction SilentlyContinue
    if (-not $revit.HasExited) { Stop-Process -Id $revit.Id -Force; Stamp "stopped Revit pid $($revit.Id)" }
}
$result = Join-Path $runDir 'result.md'
if (Test-Path $result) { Get-Content $result } else { Get-Content $log }
Write-Output "run folder: $runDir"
