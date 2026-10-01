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

    /// <summary>Whether <paramref name="window"/> is minimised.</summary>
    internal static bool IsMinimised(IntPtr window) => window != IntPtr.Zero && IsIconic(window);

    /// <summary>
    /// The window's rectangle in device-independent pixels, or <c>null</c> when there is none to read.
    /// </summary>
    /// <param name="window">Revit's main window.</param>
    /// <param name="whereRestored">
    /// For a minimised window, where it would be restored to rather than the parking place Windows
    /// moves it to.
    /// </param>
    /// <remarks>
    /// GetWindowRect answers in device pixels and WPF places in device-independent ones, so Revit's
    /// window's own DPI is the divisor: what is placed against it lands on the monitor it is on.
    /// </remarks>
    internal static NoticeRect? Bounds(IntPtr window, bool whereRestored = false)
    {
        if (window == IntPtr.Zero)
        {
            return null;
        }

        NativeRect rect;
        if (whereRestored && IsIconic(window))
        {
            WindowPlacement placement = new() { Length = Marshal.SizeOf<WindowPlacement>() };
            if (!GetWindowPlacement(window, ref placement))
            {
                return null;
            }

            rect = placement.NormalPosition;
        }
        else if (!GetWindowRect(window, out rect))
        {
            return null;
        }

        double scale = GetDpiForWindow(window) is var dpi and > 0 ? dpi / BaselineDpi : 1.0;
        return new NoticeRect(
            rect.Left / scale,
            rect.Top / scale,
            (rect.Right - rect.Left) / scale,
            (rect.Bottom - rect.Top) / scale);
    }

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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
