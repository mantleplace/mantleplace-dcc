using MantlePlace.Revit.Core;
using MantlePlace.Revit.Core.Tests;

namespace MantlePlace.AutoCad.Tests;

/// <summary>What the user is told when an export arrives: that it arrived, where, and that nothing was imported.</summary>
internal static class ExportReceiptTests
{
    private static HostExport Export() => new(
        "exp-1", "order-77", "autocad", "conn", "Site.dwg", "DESK", "1.0.0", ExportStatus.Received, null, null, null, "t0", "t0", null);

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the receipt names the order and the path the verified bundle is at", () =>
        {
            string text = ExportReceipt.Describe(Export(), @"C:\cache\order-77\bundle.zip");

            run.Contains(text, "order-77", "which order");
            run.Contains(text, @"C:\cache\order-77\bundle.zip", "where it is");
        });

        run.Case("the receipt says plainly that nothing was placed in the drawing", () =>
        {
            string text = ExportReceipt.Describe(Export(), @"C:\cache\bundle.zip");

            run.Contains(text, "nothing was placed in your drawing", "no silent success");
            run.Contains(text, "cancelled", "and what closing the window does to the export");
        });

        run.Case("a blank path is still a sentence", () =>
            run.Contains(ExportReceipt.Describe(Export(), " "), "in the bundle cache", "no empty 'at '"));

        return run.Report("Export receipt");
    }
}
