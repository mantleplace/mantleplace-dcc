using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>The box around a published ring, which a drawn plan's crop and the hazard plan's key read.</summary>
internal static class FootprintExtentTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("the extent of a ring is the box around its vertices", () =>
        {
            FootprintExtent? extent = FootprintExtent.Around(
            [
                new SiteVertex(10.0, -5.0, null),
                new SiteVertex(-20.0, 40.0, null),
                new SiteVertex(5.0, 5.0, 12.0),
            ]);

            run.True(extent is not null, "three vertices make a footprint");
            run.Within(extent!.Value.MinEastM, -20.0, 1e-9, "the west edge");
            run.Within(extent.Value.MinNorthM, -5.0, 1e-9, "the south edge");
            run.Within(extent.Value.MaxEastM, 10.0, 1e-9, "the east edge");
            run.Within(extent.Value.MaxNorthM, 40.0, 1e-9, "the north edge");
            run.Within(extent.Value.WidthM, 30.0, 1e-9, "the width");
            run.Within(extent.Value.DepthM, 45.0, 1e-9, "the depth");
        });

        run.Case("a ring with nothing in it, or with a non-finite vertex, has no extent", () =>
        {
            run.True(FootprintExtent.Around([]) is null, "no vertices, no footprint");
            run.True(
                FootprintExtent.Around([new SiteVertex(double.NaN, 0.0, null), new SiteVertex(1.0, 1.0, null)])
                    is null,
                "a non-finite ordinate poisons the box rather than being quietly dropped");
        });

        return run.Report("footprint extent");
    }
}
