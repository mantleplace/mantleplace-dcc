using System.Runtime.InteropServices;

namespace MantlePlace.Wpf;

public static class WindowStyles
{
    private const int GwlExStyle = -20;

    public const int Topmost = 0x00000008;

    public const int ToolWindow = 0x00000080;

    public const int NoActivate = 0x08000000;

    public static bool Has(IntPtr window, int bits) => (GetWindowLong(window, GwlExStyle) & bits) == bits;

    public static void Add(IntPtr window, int bits)
        => _ = SetWindowLong(window, GwlExStyle, GetWindowLong(window, GwlExStyle) | bits);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
