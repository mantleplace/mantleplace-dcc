using MantlePlace.Revit.Core;

namespace MantlePlace.AutoCad;

/// <summary>
/// What an AutoCAD or Civil 3D user is told when the web vault sends them an export: the bundle has
/// arrived and is safe on this machine, and importing it into a drawing is not in this plugin yet.
/// </summary>
/// <remarks>
/// The Revit plugin opens its import window here. Importing needs the plugin's hooks into the drawing,
/// which AutoCAD does not have yet, so the window that opens says what did happen (the bundle was
/// fetched and checked, and sits at a stated path) and what did not. Closing it is the web's
/// <c>cancelled</c>: nothing was imported, and the export says so rather than staying open for an
/// import that cannot start.
/// </remarks>
public static class ExportReceipt
{
    /// <summary>The window's title.</summary>
    public const string WindowTitle = "Mantle Place Export Received";

    /// <summary>The window's heading.</summary>
    public const string Heading = "Export Received";

    /// <summary>The button that shows the bundle's folder.</summary>
    public const string ShowFolder = "Show in Folder";

    /// <summary>The sentence the window opens with, and the plugin's command line repeats.</summary>
    /// <param name="export">The export the web sent.</param>
    /// <param name="zipPath">Where the fetched, verified bundle is.</param>
    public static string Describe(HostExport export, string zipPath)
    {
        string where = string.IsNullOrWhiteSpace(zipPath) ? "in the bundle cache" : $"at {zipPath}";

        return $"Your vault sent order {export.OrderId} to this drawing session. "
            + $"Mantle Place downloaded the bundle and checked it; it is {where}. "
            + "Importing a bundle into a drawing is not in the AutoCAD plugin yet, so nothing was "
            + "placed in your drawing. Closing this window ends the export as cancelled.";
    }
}
