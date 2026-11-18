using MantlePlace.Core;

namespace MantlePlace.AutoCad;

/// <summary>
/// What an AutoCAD or Civil 3D user is told on the command line when the web vault sends them an
/// export: the bundle has arrived and is safe on this machine, and it is importing into the active
/// drawing.
/// </summary>
/// <remarks>
/// The export opens the shared import window (<c>HPS-60</c>), as Revit's does. AutoCAD offers no
/// per-layer choice, so the window opens on its steps and the import begins at once, placing the whole
/// order as <see cref="AutoCadImportPlanner"/> plans it; Cancel stops it between steps.
/// </remarks>
public static class ExportReceipt
{
    /// <summary>The sentence the command line says as the import window opens.</summary>
    /// <param name="export">The export the web sent.</param>
    /// <param name="zipPath">Where the fetched, verified bundle is.</param>
    public static string Describe(HostExport export, string zipPath)
    {
        string where = string.IsNullOrWhiteSpace(zipPath) ? "in the bundle cache" : $"at {zipPath}";

        return $"Your vault sent order {export.OrderId} to this drawing session. "
            + $"Mantle Place downloaded the bundle and checked it; it is {where}. "
            + "It is importing into the active drawing, at the order's own coordinates and units. "
            + $"{WindowLabels.Cancel} in the import window stops it between steps.";
    }
}
