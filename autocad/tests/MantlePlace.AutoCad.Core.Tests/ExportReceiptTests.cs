using MantlePlace.Core;
using MantlePlace.Core.Tests;

namespace MantlePlace.AutoCad.Tests;

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

        run.Case("the receipt says the order is importing, where it lands, and that Cancel stops it", () =>
        {
            string text = ExportReceipt.Describe(Export(), @"C:\cache\bundle.zip");

            run.Contains(text, "importing into the active drawing", "the import begins with no press");
            run.Contains(text, "order's own coordinates", "where it lands");
            run.Contains(text, $"{WindowLabels.Cancel} in the import window stops it between steps", "the shared window's own word (HPS-51)");
            run.False(text.Contains("Press Import", StringComparison.Ordinal), "no press is waited for");
        });

        run.Case("a blank path is still a sentence", () =>
            run.Contains(ExportReceipt.Describe(Export(), " "), "in the bundle cache", "no empty 'at '"));

        return run.Report("Export receipt");
    }
}
