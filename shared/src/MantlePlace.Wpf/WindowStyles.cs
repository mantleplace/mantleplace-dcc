using System.Runtime.InteropServices;

namespace MantlePlace.Wpf;

/// <summary>
/// A window's extended style bits, read and set: the one home of the <c>GetWindowLong</c> pair, for
/// the notices and every host's own windows alike (Revit's import window, its foreground watch).
/// </summary>
/// <remarks>
/// Windows answers both from its own records, without sending the window a message, so either may be
/// used on a window whose thread is busy: the host's, stuck in a commit.
/// </remarks>
public static class WindowStyles
{
    private const int GwlExStyle = -20;

    /// <summary>Floats over every ordinary window.</summary>
    public const int Topmost = 0x00000008;

    /// <summary>Kept off the Alt+Tab list and the taskbar.</summary>
    public const int ToolWindow = 0x00000080;

    /// <summary>Never activated by a click.</summary>
    public const int NoActivate = 0x08000000;

    /// <summary>Whether <paramref name="window"/>'s extended style has every bit of <paramref name="bits"/>.</summary>
    public static bool Has(IntPtr window, int bits) => (GetWindowLong(window, GwlExStyle) & bits) == bits;

    /// <summary>Adds <paramref name="bits"/> to <paramref name="window"/>'s extended style.</summary>
    public static void Add(IntPtr window, int bits)
        => _ = SetWindowLong(window, GwlExStyle, GetWindowLong(window, GwlExStyle) | bits);

    // DllImport rather than LibraryImport: the generator needs AllowUnsafeBlocks, which this project
    // does not set.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
