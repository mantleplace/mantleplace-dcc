using MantlePlace.Core;

namespace MantlePlace.Core.Tests;

internal static class DrapeAnchorTests
{
    private static readonly DrapePlacement Site = new()
    {
        LeftM = -709.5,
        BottomM = -706.5,
        RightM = 709.5,
        TopM = 706.5,
        PixelSize = new ImageSize(4730, 4710),
        ExtentFromDrapeBlock = true,
    };

    internal static int Run()
    {
        TestRun run = new();

        run.Case("flat shading: the offset is the image's south-west corner from the origin", () =>
        {
            DrapeOffset offset = DrapeAnchor.For(Site, smoothShading: false, -708.5, -704.8);

            run.True(Math.Abs(offset.Xm - -709.5) < 1e-9, $"x {offset.Xm}");
            run.True(Math.Abs(offset.Ym - -706.5) < 1e-9, $"y {offset.Ym}");
        });

        run.Case("smooth shading: the offset is measured from the element's own corner", () =>
        {
            DrapeOffset offset = DrapeAnchor.For(Site, smoothShading: true, -708.5, -704.8);

            run.True(Math.Abs(offset.Xm - -1.0) < 1e-9, $"x {offset.Xm}");
            run.True(Math.Abs(offset.Ym - -1.7) < 1e-9, $"y {offset.Ym}");
        });

        run.Case("a subdivision far from the origin gets a large negative offset", () =>
        {
            DrapeOffset offset = DrapeAnchor.For(Site, smoothShading: true, 326.6, 223.1);

            run.True(Math.Abs(offset.Xm - -1036.1) < 1e-9, $"x {offset.Xm}");
            run.True(Math.Abs(offset.Ym - -929.6) < 1e-9, $"y {offset.Ym}");
        });

        run.Case("two elements under smooth shading never share an offset unless they share a corner", () =>
        {
            DrapeOffset ground = DrapeAnchor.For(Site, smoothShading: true, -708.5, -704.8);
            DrapeOffset patch = DrapeAnchor.For(Site, smoothShading: true, -689.1, -594.7);

            run.True(ground != patch, "one material cannot serve both");
            run.True(Math.Abs((ground.Xm - patch.Xm) - (-708.5 - -689.1) * -1) < 1e-9,
                "the difference between the offsets is the difference between the corners");
        });

        run.Case("the description names the origin it was written for", () =>
        {
            string smooth = DrapeAnchor.Describe("the terrain", Site, true, -708.5, -704.8, new DrapeOffset(-1.0, -1.7));
            string flat = DrapeAnchor.Describe("the terrain", Site, false, -708.5, -704.8, new DrapeOffset(-709.5, -706.5));

            run.Contains(smooth, "smooth shading", "which renderer");
            run.Contains(smooth, "(-708.5, -704.8)", "the element's corner");
            run.Contains(smooth, "(-1.0, -1.7)", "what was written");
            run.Contains(flat, "flat shading", "which renderer");
            run.Contains(flat, "(-709.5, -706.5)", "what was written");
        });

        return run.Report("drape anchor");
    }
}
