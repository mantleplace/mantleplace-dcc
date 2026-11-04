using MantlePlace.Revit.Core;
using MantlePlace.Revit.Core.Tests;

namespace MantlePlace.AutoCad.Tests;

/// <summary>What the user is told when an export arrives: that it arrived, where, and that Import places it.</summary>
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

        run.Case("the receipt says nothing is placed until Import is pressed, and what closing does", () =>
        {
            string text = ExportReceipt.Describe(Export(), @"C:\cache\bundle.zip");

            run.Contains(text, "Press Import", "the one press");
            run.Contains(text, "order's own coordinates", "where it lands");
            run.Contains(text, "without importing", "closing before the press");
            run.Contains(text, "cancelled", "and what closing the window does to the export");
        });

        run.Case("the window's button is the shared Import label", () =>
            run.Equal(ExportReceipt.ImportButton, WindowLabels.Import, "one word for one action (HPS-51)"));

        run.Case("a blank path is still a sentence", () =>
            run.Contains(ExportReceipt.Describe(Export(), " "), "in the bundle cache", "no empty 'at '"));

        return run.Report("Export receipt");
    }
}
