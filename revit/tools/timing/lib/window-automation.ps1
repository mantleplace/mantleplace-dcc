# Helpers for Invoke-TimingRun.ps1 beyond quiet-win32.ps1 (dot-source that first): the hung-window query, shared-read
# file copies, the journal finder, and the UI Automation readers for the real import window.
# Nothing here moves the mouse, sends a key or activates a window.

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
if (-not ('Q.XTiming' -as [type])) {
Add-Type -Namespace Q -Name XTiming -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsHungAppWindow(System.IntPtr h);
'@
}

function Test-Hung([IntPtr]$h) { if ($h -eq [IntPtr]::Zero) { return $false }; [Q.XTiming]::IsHungAppWindow($h) }

# Copies a file another process may still hold open for writing (Revit's journal, the plugin's log).
function Copy-Shared([string]$Source, [string]$Destination) {
    $in = [IO.File]::Open($Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
    try {
        $out = [IO.File]::Create($Destination)
        try { $in.CopyTo($out) } finally { $out.Dispose() }
    }
    finally { $in.Dispose() }
}

# The first $Count lines of a file another process may hold open.
function Read-HeadShared([string]$Path, [int]$Count = 12) {
    $in = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
    try {
        $reader = New-Object IO.StreamReader($in)
        $lines = @(); for ($i = 0; $i -lt $Count -and -not $reader.EndOfStream; $i++) { $lines += $reader.ReadLine() }
        $lines
    }
    finally { $in.Dispose() }
}

# Revit's journal for the process that started at $ProcessStart: journals are journal.NNNN.txt in the
# year's folder, and the first lines say when recording started. Chosen by that time (the nearest to
# the process start, within 90 s); among journals that started within 5 s of each other (two Revits of
# one year launched together) the one that mentions $RunDir wins.
function Find-Journal([string]$Year, [datetime]$ProcessStart, [string]$RunDir) {
    $dir = Join-Path $env:LOCALAPPDATA "Autodesk\Revit\Autodesk Revit $Year\Journals"
    if (-not (Test-Path $dir)) { return $null }
    $cands = @()
    foreach ($f in (Get-ChildItem $dir -Filter 'journal.*.txt' -ErrorAction SilentlyContinue)) {
        if ($f.Name -notmatch '^journal\.\d+\.txt$') { continue }
        if ($f.CreationTime -lt $ProcessStart.AddSeconds(-90)) { continue }
        try { $head = Read-HeadShared $f.FullName 12 } catch { continue }
        $m = $head | Select-String -Pattern "started recording journal file" | Select-Object -First 1
        if (-not $m -or $m.Line -notmatch "'C (\d{2}-\w{3}-\d{4} \d{2}:\d{2}:\d{2}\.\d{3});") { continue }
        $ts = [datetime]::ParseExact($Matches[1], 'dd-MMM-yyyy HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture)
        $delta = ($ts - $ProcessStart).TotalSeconds
        if ([math]::Abs($delta) -le 90) { $cands += [pscustomobject]@{ Path = $f.FullName; Started = $ts; Delta = $delta } }
    }
    if ($cands.Count -eq 0) { return $null }
    $best = $cands | Sort-Object { [math]::Abs($_.Delta) } | Select-Object -First 1
    $close = @($cands | Where-Object { [math]::Abs($_.Delta - $best.Delta) -le 5 })
    if ($close.Count -gt 1) {
        foreach ($c in $close) {
            $hit = Select-String -Path $c.Path -SimpleMatch $RunDir -Quiet -ErrorAction SilentlyContinue
            if ($hit) { $best = $c; break }
        }
    }
    [pscustomobject]@{ Path = $best.Path; Started = $best.Started; Delta = $best.Delta; Candidates = $cands.Count }
}

# ---- the import window through UI Automation ----------------------------------------------------------

$script:StateWords = @('Waiting', 'Importing', 'Done', 'Failed', 'Cancelled', 'Not Run')
$script:ButtonWords = @('Import', 'Cancel', 'Close')

# One batched cross-process fetch of the window's whole raw tree: name, type, enabled, offscreen.
function Get-Elements($win) {
    $cr = New-Object Windows.Automation.CacheRequest
    $cr.TreeFilter = [Windows.Automation.Automation]::RawViewCondition
    foreach ($p in @([Windows.Automation.AutomationElement]::NameProperty, [Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.AutomationElement]::IsEnabledProperty, [Windows.Automation.AutomationElement]::IsOffscreenProperty)) { $cr.Add($p) }
    $cr.TreeScope = [Windows.Automation.TreeScope]::Element -bor [Windows.Automation.TreeScope]::Descendants
    $scope = $cr.Activate()
    try { $found = $win.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) }
    finally { $scope.Dispose() }
    $list = New-Object System.Collections.ArrayList
    foreach ($e in $found) {
        [void]$list.Add([pscustomobject]@{
            Name      = $e.Cached.Name
            Type      = ($e.Cached.ControlType.ProgrammaticName -replace '^ControlType\.', '')
            Enabled   = $e.Cached.IsEnabled
            Offscreen = $e.Cached.IsOffscreen
            Element   = $e
        })
    }
    , $list
}

# What the running window says: the step rows (a name Text followed by its state Text, in the order the
# window builds them), the status line under the bar, the bar, and the Close button.
function Get-WindowReading($win) {
    $els = Get-Elements $win
    $texts = @($els | Where-Object { $_.Type -eq 'Text' })
    $pairs = New-Object System.Collections.ArrayList
    $lastPair = -1
    for ($i = 1; $i -lt $texts.Count; $i++) {
        if ($script:StateWords -contains $texts[$i].Name -and $script:StateWords -notcontains $texts[$i - 1].Name) {
            [void]$pairs.Add([pscustomobject]@{ Step = $texts[$i - 1].Name; State = $texts[$i].Name })
            $lastPair = $i
        }
    }
    $status = ''
    for ($i = $lastPair + 1; $i -lt $texts.Count; $i++) {
        $n = $texts[$i].Name
        if ($n -and $script:StateWords -notcontains $n -and $script:ButtonWords -notcontains $n) { $status = $n; break }
    }
    $bar = $els | Where-Object { $_.Type -eq 'ProgressBar' } | Select-Object -First 1
    $barText = ''
    if ($bar) {
        $rv = $null
        if ($bar.Element.TryGetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern, [ref]$rv)) {
            $max = $rv.Current.Maximum; $val = $rv.Current.Value
            $barText = if ($max -gt 0) { ([double]($val / $max)).ToString('0.000', [Globalization.CultureInfo]::InvariantCulture) } else { "$val" }
        }
        else { $barText = 'indeterminate' }
        if ($bar.Offscreen) { $barText += ' (offscreen)' }
    }
    $close = $els | Where-Object { $_.Type -eq 'Button' -and $_.Name -eq 'Close' } | Select-Object -First 1
    $closeText = if (-not $close) { 'absent' } elseif ($close.Enabled) { 'enabled' } else { 'disabled' }
    $current = ($pairs | Where-Object { $_.State -eq 'Importing' } | Select-Object -First 1).Step
    [pscustomobject]@{
        Pairs = $pairs; Current = "$current"; Status = $status; Bar = $barText; Close = $closeText
        States = (($pairs | ForEach-Object { "$($_.Step)=$($_.State)" }) -join ';')
        Elements = $els.Count
    }
}

# The checklist's check boxes: Name, ToggleState, Enabled, and the element to toggle.
function Get-CheckBoxes($win) {
    $c = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::CheckBox)
    $out = @()
    foreach ($e in $win.FindAll([Windows.Automation.TreeScope]::Descendants, $c)) {
        $tp = $null; [void]$e.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$tp)
        $out += [pscustomobject]@{ Name = $e.Current.Name; State = "$($tp.Current.ToggleState)"; Enabled = $e.Current.IsEnabled; Toggle = $tp; Element = $e }
    }
    , $out
}

# Every raw-tree element of a window, for discovery (written once per phase of the window).
function Get-TreeDump($win) {
    $els = Get-Elements $win
    $els | ForEach-Object { "{0,-12} enabled={1} offscreen={2} name='{3}'" -f $_.Type, $_.Enabled, $_.Offscreen, $_.Name }
}

function Get-ReportText($win) {
    $c = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Edit)
    $edit = $win.FindFirst([Windows.Automation.TreeScope]::Descendants, $c)
    if (-not $edit) { return '' }
    $vp = $null
    if ($edit.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { return $vp.Current.Value }
    ''
}
