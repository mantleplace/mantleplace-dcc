using System.Runtime.InteropServices;
using System.Text;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// Keeps the import window floating over Revit while Revit is the application in front, and behind
/// whatever the curator turns to otherwise (<see cref="ImportWindowStacking"/>).
/// </summary>
/// <remarks>
/// <para>
/// This is what used to come free with ownership. The import window was owned by Revit's main window,
/// and an owned window floats over its owner — but an owner and the window it owns share one input
/// queue, so the window froze whenever Revit's thread sat in a commit. Unowned, on a thread of its
/// own, the window has to be told when to float, and a foreground hook is how it hears.
/// </para>
/// <para>
/// ⛔ <b>It never asks Revit's thread anything.</b> Every call here is one Windows answers without
/// sending a message to the window it is about — the foreground window, a window's process id, its
/// class name, its extended style, a z-order move of the import window's own handle — so none of it
/// waits on a Revit that is stuck in a commit. <c>GetWindowText</c> on a Revit window would, because
/// within one process it sends the window a message; it is not used.
/// </para>
/// <para>
/// The hook is out of context: Windows posts each foreground change to the thread that set it, which
/// is the import window's, and its dispatcher delivers it like any other message.
/// </para>
/// </remarks>
internal sealed class ForegroundWatch : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint ReorderOnly = SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoOwnerZOrder;
    private const int GwlExStyle = -20;
    private const int WsExTopmost = 0x00000008;

    /// <summary>The class Windows gives the stand-in it draws over a window that stopped answering.</summary>
    private const string GhostClass = "Ghost";

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);

    private readonly IntPtr _window;

    /// <summary>Held for the hook's lifetime: Windows calls through it, and a collected delegate is a crash.</summary>
    private readonly WinEventProc _callback;

    private IntPtr _hook;
    private bool _floating;

    /// <param name="window">The import window's handle. Its thread must pump messages: the hook is delivered there.</param>
    internal ForegroundWatch(IntPtr window)
    {
        _window = window;
        _callback = OnForegroundChanged;

        // Stacked once for whatever is in front now, then on every change that moves it.
        IntPtr foreground = GetForegroundWindow();
        _floating = ImportWindowStacking.Floats(HolderOf(foreground), floating: false);
        Stack(foreground);

        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _callback, 0, 0, WinEventOutOfContext);
    }

    /// <summary>Whose window is in front now.</summary>
    internal static ForegroundHolder Holder() => HolderOf(GetForegroundWindow());

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private static ForegroundHolder HolderOf(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return ForegroundHolder.Nobody;
        }

        _ = GetWindowThreadProcessId(window, out uint processId);
        if (processId == (uint)Environment.ProcessId)
        {
            return ForegroundHolder.ThisProcess;
        }

        StringBuilder className = new(16);
        return GetClassName(window, className, className.Capacity) > 0
            && string.Equals(className.ToString(), GhostClass, StringComparison.Ordinal)
                ? ForegroundHolder.HungWindowGhost
                : ForegroundHolder.OtherProcess;
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
        => Apply(window);

    private void Apply(IntPtr foreground)
    {
        bool floats = ImportWindowStacking.Floats(HolderOf(foreground), _floating);
        if (floats == _floating)
        {
            return;
        }

        _floating = floats;
        Stack(foreground);
    }

    private void Stack(IntPtr foreground)
    {
        if (_floating)
        {
            SetWindowPos(_window, HwndTopmost, 0, 0, 0, 0, ReorderOnly);
        }
        else if (foreground != IntPtr.Zero && (GetWindowLong(foreground, GwlExStyle) & WsExTopmost) == 0)
        {
            // Directly behind what came forward. HWND_NOTOPMOST alone would put the window at the top
            // of the ordinary band, over the very application the curator just turned to.
            SetWindowPos(_window, foreground, 0, 0, 0, 0, ReorderOnly);
        }
        else
        {
            SetWindowPos(_window, HwndNoTopmost, 0, 0, 0, 0, ReorderOnly);
        }
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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
