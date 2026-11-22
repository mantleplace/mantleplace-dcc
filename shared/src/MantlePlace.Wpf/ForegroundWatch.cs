using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using MantlePlace.Core;

namespace MantlePlace.Wpf;

internal readonly record struct ForegroundReading(IntPtr Window, ForegroundFacts Facts);

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
    private readonly IntPtr _hostWindow;
    private readonly Action<Exception> _fault;

    private readonly WinEventProc _callback;

    private IntPtr _hook;
    private bool _floating;

    private IntPtr _showBehind;

    internal ForegroundWatch(IntPtr window, IntPtr hostWindow, ForegroundReading opening, Action<Exception> fault)
    {
        _window = window;
        _hostWindow = hostWindow;
        _fault = fault;
        _callback = OnForegroundChanged;

        IntPtr foreground = opening.Window;
        Stacking first = ImportWindowStacking.First(opening.Facts);
        _floating = first.Floating;
        if (first.Move == StackingMove.SinkBehindForeground)
        {
            _showBehind = foreground;
            HwndSource.FromHwnd(window)?.AddHook(OnWindowMessage);
        }
        else
        {
            Move(first.Move, foreground);
        }

        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _callback, 0, 0, WinEventOutOfContext);
    }

    internal static ForegroundReading Read(IntPtr importWindow, IntPtr hostWindow)
    {
        IntPtr foreground = GetForegroundWindow();
        return new ForegroundReading(foreground, FactsOf(foreground, importWindow, hostWindow));
    }

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

    private static ForegroundFacts FactsOf(IntPtr foreground, IntPtr importWindow, IntPtr hostWindow)
    {
        bool mainEnabled = hostWindow == IntPtr.Zero || IsWindowEnabled(hostWindow);
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
            IsRevitMainWindow: foreground == hostWindow,
            RevitMainWindowEnabled: mainEnabled,
            ForegroundFloats: WindowStyles.Has(foreground, WindowStyles.Topmost),
            ClassName: className.ToString(),
            HungWindowOfThisProcess: inThisProcess ? false : HungWindowIsOurs(foreground));
    }

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
            _showBehind = IntPtr.Zero;
            _fault(ex);
        }

        return IntPtr.Zero;
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        try
        {
            Stacking next = ImportWindowStacking.Next(FactsOf(window, _window, _hostWindow), _floating);
            _floating = next.Floating;
            Move(next.Move, window);
        }
        catch (Exception ex)
        {
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

    [DllImport("user32.dll")]
    private static extern IntPtr HungWindowFromGhostWindow(IntPtr ghost);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
