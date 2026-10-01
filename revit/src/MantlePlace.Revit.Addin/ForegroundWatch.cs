using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>The window in front at one moment, and what the core reads off it.</summary>
internal readonly record struct ForegroundReading(IntPtr Window, ForegroundFacts Facts);

/// <summary>
/// Reads the window in front and carries out <see cref="ImportWindowStacking"/>'s decision on the
/// import window's place in the z-order, once before the window is shown and on every change of
/// foreground after.
/// </summary>
/// <remarks>
/// <para>
/// It decides nothing: every rule — float over Revit, never over a Revit modal, go behind another
/// application, leave the desktop alone — is the pure core's, with its tests. This reads the facts and
/// makes the one Windows call the decision names.
/// </para>
/// <para>
/// ⛔ <b>It never asks Revit's thread anything.</b> Every call here is answered by Windows from its own
/// records — the foreground window, a window's process, class, styles and enabled state, a ghost's
/// hung window — and the only window it moves is the import window. <c>GetWindowText</c> on a Revit
/// window would send that window a message within one process, and is not used.
/// </para>
/// <para>
/// The hook is out of context: Windows posts each foreground change to the import window's thread,
/// whose dispatcher delivers it. ⛔ An exception must never unwind from the callback into Windows, so
/// it is caught there and handed to <c>fault</c>, and the watch carries on.
/// </para>
/// </remarks>
internal sealed class ForegroundWatch : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const int WmWindowPosChanging = 0x0046;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint ReorderOnly = SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoOwnerZOrder;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);

    private readonly IntPtr _window;
    private readonly IntPtr _revitWindow;
    private readonly Action<Exception> _fault;

    /// <summary>Held for the hook's lifetime: Windows calls through it, and a collected delegate is a crash.</summary>
    private readonly WinEventProc _callback;

    private IntPtr _hook;
    private bool _floating;

    /// <summary>The window to appear directly behind when the import window is first shown, or zero.</summary>
    private IntPtr _showBehind;

    /// <summary>Stacks the window for what was in front as it opened — before it is shown — and watches from then on.</summary>
    /// <param name="window">The import window's handle. Its thread must pump messages: the hook is delivered there.</param>
    /// <param name="revitWindow">Revit's main window.</param>
    /// <param name="opening">
    /// The reading the window decided its focus from (<see cref="ImportWindowStacking.ShowsActivated"/>):
    /// the same one decides where it goes, so the two cannot disagree over a foreground that changed
    /// between two reads.
    /// </param>
    /// <param name="fault">Told of an exception the hook caught, so it can be logged; the watch carries on.</param>
    internal ForegroundWatch(IntPtr window, IntPtr revitWindow, ForegroundReading opening, Action<Exception> fault)
    {
        _window = window;
        _revitWindow = revitWindow;
        _fault = fault;
        _callback = OnForegroundChanged;

        IntPtr foreground = opening.Window;
        Stacking first = ImportWindowStacking.First(opening.Facts);
        _floating = first.Floating;
        if (first.Move == StackingMove.SinkBehindForeground)
        {
            // ⛔ Not moved now: WPF's Show raises the window to the top as it shows it, which would undo
            // the move and put it over the application in front for a moment. The show's own
            // WM_WINDOWPOSCHANGING is rewritten instead, so it appears behind that application.
            _showBehind = foreground;
            HwndSource.FromHwnd(window)?.AddHook(OnWindowMessage);
        }
        else
        {
            Move(first.Move, foreground);
        }

        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _callback, 0, 0, WinEventOutOfContext);
    }

    /// <summary>The window in front now, and what the core reads off it — usable before the import window exists.</summary>
    internal static ForegroundReading Read(IntPtr importWindow, IntPtr revitWindow)
    {
        IntPtr foreground = GetForegroundWindow();
        return new ForegroundReading(foreground, FactsOf(foreground, importWindow, revitWindow));
    }

    /// <summary>
    /// Stops floating now, whatever is in front: Revit is about to show a dialog, which must not open
    /// under this window. The next change of foreground decides again.
    /// </summary>
    internal void Lower()
    {
        Stacking lowered = ImportWindowStacking.Lowered(_floating);
        _floating = lowered.Floating;
        Move(lowered.Move, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private static ForegroundFacts FactsOf(IntPtr foreground, IntPtr importWindow, IntPtr revitWindow)
    {
        // Read, not judged: what a disabled main window means is the core's.
        bool mainEnabled = revitWindow == IntPtr.Zero || IsWindowEnabled(revitWindow);
        if (foreground == IntPtr.Zero)
        {
            return new ForegroundFacts(false, false, false, false, mainEnabled, false, string.Empty, false);
        }

        _ = GetWindowThreadProcessId(foreground, out uint processId);
        bool inThisProcess = processId == (uint)Environment.ProcessId;

        StringBuilder className = new(64);
        _ = GetClassName(foreground, className, className.Capacity);

        return new ForegroundFacts(
            Exists: true,
            IsImportWindow: foreground == importWindow,
            InThisProcess: inThisProcess,
            IsRevitMainWindow: foreground == revitWindow,
            RevitMainWindowEnabled: mainEnabled,
            ForegroundFloats: WindowStyles.Has(foreground, WindowStyles.Topmost),
            ClassName: className.ToString(),
            HungWindowOfThisProcess: inThisProcess ? false : HungWindowIsOurs(foreground));
    }

    /// <summary>
    /// Whether <paramref name="window"/> is the ghost of one of this process's windows: <c>false</c> for
    /// a window that is no ghost, <c>null</c> when this Windows cannot say.
    /// </summary>
    private static bool? HungWindowIsOurs(IntPtr window)
    {
        try
        {
            IntPtr hung = HungWindowFromGhostWindow(window);
            if (hung == IntPtr.Zero)
            {
                return false;
            }

            _ = GetWindowThreadProcessId(hung, out uint processId);
            return processId == (uint)Environment.ProcessId;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Puts the window's first show directly behind the window that was in front.</summary>
    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmWindowPosChanging || _showBehind == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            WindowPos position = Marshal.PtrToStructure<WindowPos>(lParam);
            if ((position.Flags & SwpShowWindow) != 0)
            {
                position.InsertAfter = _showBehind;
                position.Flags &= ~SwpNoZOrder;
                Marshal.StructureToPtr(position, lParam, false);
                _showBehind = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            // Inside Windows' own message dispatch: a fault costs a window shown on top, not the thread.
            _showBehind = IntPtr.Zero;
            _fault(ex);
        }

        return IntPtr.Zero;
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        try
        {
            Stacking next = ImportWindowStacking.Next(FactsOf(window, _window, _revitWindow), _floating);
            _floating = next.Floating;
            Move(next.Move, window);
        }
        catch (Exception ex)
        {
            // Called by Windows: nothing may unwind into user32. A missed restack leaves the window in
            // the wrong place until the next change of foreground, which costs less than its thread.
            _fault(ex);
        }
    }

    private void Move(StackingMove move, IntPtr foreground)
    {
        switch (move)
        {
            case StackingMove.Float:
                SetWindowPos(_window, HwndTopmost, 0, 0, 0, 0, ReorderOnly);
                break;
            case StackingMove.SinkBehindForeground:
                SetWindowPos(_window, foreground, 0, 0, 0, 0, ReorderOnly);
                break;
            case StackingMove.SinkBelowFloating:
                SetWindowPos(_window, HwndNoTopmost, 0, 0, 0, 0, ReorderOnly);
                break;
            default:
                break;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Window;
        public IntPtr InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);

    // DllImport rather than LibraryImport, for RibbonImagery's reason: the generator needs
    // AllowUnsafeBlocks, which this project does not set.
    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);

    // Exported by user32 since Windows Vista, and resolved at the first call, which is guarded.
    [DllImport("user32.dll")]
    private static extern IntPtr HungWindowFromGhostWindow(IntPtr ghost);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
