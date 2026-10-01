using System.Runtime.InteropServices;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// Where Revit's main window is, in the device-independent pixels WPF places windows in. The one home
/// of that conversion, for the Prepare notices and the import window alike.
/// </summary>
/// <remarks>
/// Every call here is one Windows answers from its own records, without sending the window a message,
/// so it is safe from a thread that must never wait on Revit's — the import window's — and it answers
/// while Revit's thread is stuck in a commit. Where a window goes inside that rectangle is the pure
/// core's (<see cref="NoticeStack"/>, <see cref="ImportWindowPlacement"/>).
/// </remarks>
internal static class RevitWindowGeometry
{
    private const double BaselineDpi = 96.0;
    private const int RestoreToMaximized = 0x0002;
    private const uint MonitorDefaultToNearest = 0x00000002;

    /// <summary>Whether <paramref name="window"/> is minimised.</summary>
    internal static bool IsMinimised(IntPtr window) => window != IntPtr.Zero && IsIconic(window);

    /// <summary>
    /// The window's rectangle in device-independent pixels, or <c>null</c> when there is none to read.
    /// </summary>
    /// <param name="window">Revit's main window.</param>
    /// <param name="whereRestored">
    /// For a minimised window, where it would be restored to rather than the parking place Windows
    /// moves it to: its restored rectangle, kept by Windows less its monitor's work-area inset, or that
    /// work area when it restores maximised (<see cref="ImportWindowPlacement.Restored"/>).
    /// </param>
    /// <remarks>
    /// GetWindowRect answers in device pixels and WPF places in device-independent ones, so Revit's
    /// window's own DPI is the divisor: what is placed against it lands on the monitor it is on.
    /// </remarks>
    internal static ScreenRect? Bounds(IntPtr window, bool whereRestored = false)
    {
        if (window == IntPtr.Zero)
        {
            return null;
        }

        ScreenRect pixels;
        if (whereRestored && IsIconic(window))
        {
            WindowPlacement placement = new() { Length = Marshal.SizeOf<WindowPlacement>() };
            MonitorInfo monitor = new() { Size = Marshal.SizeOf<MonitorInfo>() };

            // For a minimised window, MonitorFromWindow answers with the monitor it will restore to.
            if (!GetWindowPlacement(window, ref placement)
                || !GetMonitorInfo(MonitorFromWindow(window, MonitorDefaultToNearest), ref monitor))
            {
                return null;
            }

            pixels = ImportWindowPlacement.Restored(
                Of(placement.NormalPosition),
                Of(monitor.WorkArea),
                Of(monitor.Monitor),
                restoresMaximised: (placement.Flags & RestoreToMaximized) != 0);
        }
        else if (GetWindowRect(window, out NativeRect rect))
        {
            pixels = Of(rect);
        }
        else
        {
            return null;
        }

        double scale = GetDpiForWindow(window) is var dpi and > 0 ? dpi / BaselineDpi : 1.0;
        return new ScreenRect(pixels.Left / scale, pixels.Top / scale, pixels.Width / scale, pixels.Height / scale);
    }

    private static ScreenRect Of(NativeRect rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }

    // DllImport rather than LibraryImport, for RibbonImagery's reason: the generator needs
    // AllowUnsafeBlocks, which this project does not set.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement placement);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
