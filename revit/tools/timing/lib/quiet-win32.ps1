# Win32 + UIA helpers for the quiet harness driver. Dot-source it.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
if (-not ('Q.W2' -as [type])) {
Add-Type -Namespace Q -Name W2 -MemberDefinition @'
public delegate bool P(System.IntPtr h, System.IntPtr l);
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct POINT { public int X; public int Y; }
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(P f, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsIconic(System.IntPtr h);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int c);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetWindowPos(System.IntPtr h, System.IntPtr after, int x, int y, int cx, int cy, uint flags);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetWindow(System.IntPtr h, uint cmd);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetWindowText(System.IntPtr h, System.Text.StringBuilder s, int n);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetClassName(System.IntPtr h, System.Text.StringBuilder s, int n);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr SendMessage(System.IntPtr h, uint msg, System.IntPtr w, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindow(System.IntPtr h);
[System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct WINDOWPLACEMENT { public int length; public int flags; public int showCmd; public POINT ptMinPosition; public POINT ptMaxPosition; public RECT rcNormalPosition; }
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowPlacement(System.IntPtr h, ref WINDOWPLACEMENT p);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetWindowPlacement(System.IntPtr h, ref WINDOWPLACEMENT p);
// Restores a minimized window straight onto (x, y) at 1280x720 without activating it: the restored
// rectangle is moved first, while the window is still minimized, so it never paints anywhere else.
public static bool RestoreParked(System.IntPtr h, int x, int y) {
    WINDOWPLACEMENT p = new WINDOWPLACEMENT(); p.length = System.Runtime.InteropServices.Marshal.SizeOf(p);
    if (!GetWindowPlacement(h, ref p)) return false;
    p.rcNormalPosition.Left = x; p.rcNormalPosition.Top = y; p.rcNormalPosition.Right = x + 1280; p.rcNormalPosition.Bottom = y + 720;
    p.showCmd = 7; p.flags = 0;
    if (!SetWindowPlacement(h, ref p)) return false;
    return ShowWindow(h, 4);
}
// Hands the foreground back to a window the harness process took it from.
public static bool GiveBack(System.IntPtr to) {
    if (!IsWindow(to)) return false;
    System.IntPtr now = GetForegroundWindow(); uint pid;
    uint nowThread = GetWindowThreadProcessId(now, out pid);
    uint me = GetCurrentThreadId();
    bool attached = nowThread != me && AttachThreadInput(me, nowThread, true);
    bool ok = SetForegroundWindow(to);
    if (attached) AttachThreadInput(me, nowThread, false);
    return ok;
}
'@
}

function Get-PidWindows([int]$ProcessId) {
    $list = New-Object System.Collections.ArrayList
    [void][Q.W2]::EnumWindows({ param($h, $l) $x = 0; [void][Q.W2]::GetWindowThreadProcessId($h, [ref]$x); if ($x -eq $ProcessId -and [Q.W2]::IsWindowVisible($h)) { [void]$list.Add($h) }; $true }, [IntPtr]::Zero)
    , $list
}
function Get-WindowClass([IntPtr]$h) { $c = New-Object Text.StringBuilder 256; [void][Q.W2]::GetClassName($h, $c, 256); "$c" }
function Get-WindowTitle([IntPtr]$h) { $t = New-Object Text.StringBuilder 512; [void][Q.W2]::GetWindowText($h, $t, 512); "$t" }
function Get-Cursor { $p = New-Object Q.W2+POINT; [void][Q.W2]::GetCursorPos([ref]$p); "$($p.X),$($p.Y)" }
function Get-ForegroundOwner {
    $h = [Q.W2]::GetForegroundWindow(); $x = 0; [void][Q.W2]::GetWindowThreadProcessId($h, [ref]$x)
    $name = try { (Get-Process -Id $x -ErrorAction Stop).ProcessName } catch { '?' }
    "pid $x ($name) '$(Get-WindowTitle $h)'"
}

# Where a harness window goes: the top-left of the smallest non-primary screen, else far off-screen.
function Get-ParkingSpot {
    $others = [System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Sort-Object { $_.Bounds.Width * $_.Bounds.Height }
    if ($others) { $b = @($others)[0].Bounds; return @{ X = $b.X; Y = $b.Y; Screen = @($others)[0].DeviceName } }
    @{ X = -32000; Y = -32000; Screen = 'off-screen' }
}

$SWP_NOSIZE = 0x0001; $SWP_NOZORDER = 0x0004; $SWP_NOACTIVATE = 0x0010; $HWND_BOTTOM = [IntPtr]1
# Moves a window to the parking spot and to the bottom of the z-order, never activating it.
function Move-Parked([IntPtr]$h, $spot) {
    [void][Q.W2]::SetWindowPos($h, $HWND_BOTTOM, $spot.X, $spot.Y, 0, 0, $SWP_NOSIZE -bor $SWP_NOACTIVATE)
}

# Every UIA descendant of a task dialog, for discovery.
function Show-Dialog([IntPtr]$h) {
    $el = [Windows.Automation.AutomationElement]::FromHandle($h)
    foreach ($d in $el.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
        $patterns = ($d.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName -replace 'PatternIdentifiers.Pattern', '' }) -join '+'
        "   - $($d.Current.ControlType.ProgrammaticName) name='$($d.Current.Name)' id='$($d.Current.AutomationId)' class='$($d.Current.ClassName)' hwnd=$($d.Current.NativeWindowHandle) patterns=$patterns"
    }
}
