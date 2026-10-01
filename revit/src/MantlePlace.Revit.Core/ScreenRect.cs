namespace MantlePlace.Revit.Core;

/// <summary>
/// A rectangle on the screen: in device-independent pixels where WPF places a window, and in device
/// pixels where Windows reports one, as each caller says.
/// </summary>
public readonly record struct ScreenRect(double Left, double Top, double Width, double Height);
