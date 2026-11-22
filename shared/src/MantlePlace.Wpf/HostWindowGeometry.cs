using System.Runtime.InteropServices;
using MantlePlace.Core;

namespace MantlePlace.Wpf;

public static class HostWindowGeometry
{
    private const double BaselineDpi = 96.0;
    private const int RestoreToMaximized = 0x0002;
    private const uint MonitorDefaultToNearest = 0x00000002;

    public static bool IsMinimised(IntPtr window) => window != IntPtr.Zero && IsIconic(window);

    public static ScreenRect? Bounds(IntPtr window, bool whereRestored = false)
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
