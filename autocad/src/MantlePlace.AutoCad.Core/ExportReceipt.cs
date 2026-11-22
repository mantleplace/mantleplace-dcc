using MantlePlace.Core;

namespace MantlePlace.AutoCad;

public static class ExportReceipt
{
    public static string Describe(HostExport export, string zipPath)
    {
        string where = string.IsNullOrWhiteSpace(zipPath) ? "in the bundle cache" : $"at {zipPath}";

        return $"Your vault sent order {export.OrderId} to this drawing session. "
            + $"Mantle Place downloaded the bundle and checked it; it is {where}. "
            + "It is importing into the active drawing, at the order's own coordinates and units. "
            + $"{WindowLabels.Cancel} in the import window stops it between steps.";
    }
}
