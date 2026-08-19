using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The layering arithmetic: the imagery sliver takes exactly the minimum, the remainder takes the rest,
/// the total never moves, and anything degenerate is refused rather than built.
/// </summary>
internal static class DrapeLayeringTests
{
    private const double Tight = 1e-12;

    internal static int Run()
    {
        TestRun run = new();

        run.Case("a comfortable split preserves the total exactly", () =>
        {
            // 300 mm structure, ~3 mm minimum, in internal feet — the shape of a plausible type.
            double total = 0.984252;
            double minimum = 0.0104166;
            DrapeLayerSplit split = DrapeLayering.Split(total, minimum);
            run.True(split.Ok, "a 300 mm layer takes a 3 mm sliver");
            run.Within(split.ImageryThickness, minimum, Tight, "the sliver is exactly the minimum");
            run.Within(split.ImageryThickness + split.LowerThickness, total, Tight,
                "the total is preserved — the terrain must not drift off its survey elevation");
        });

        run.Case("exactly at the 2×-minimum boundary is allowed", () =>
        {
            // total = 3 × minimum leaves the lower layer at exactly twice the minimum, the last
            // allowed shape — the refusal is strictly-below, not at. Dyadic values, so "exactly"
            // survives binary floating point.
            DrapeLayerSplit split = DrapeLayering.Split(0.375, 0.125);
            run.True(split.Ok, "lower layer of exactly 2× the minimum is not degenerate");
            run.Within(split.LowerThickness, 0.25, Tight, "lower layer");
        });

        run.Case("just below the boundary refuses", () =>
        {
            DrapeLayerSplit split = DrapeLayering.Split(0.375 - 1e-9, 0.125);
            run.False(split.Ok, "a lower layer thinner than twice the minimum is refused");
        });

        run.Case("comfortably above the boundary splits", () =>
        {
            DrapeLayerSplit split = DrapeLayering.Split(0.31, 0.1);
            run.True(split.Ok, "just above the boundary");
            run.Within(split.LowerThickness, 0.21, Tight, "lower layer takes the rest");
        });

        run.Case("NaN, infinity, zero and negative inputs all refuse", () =>
        {
            run.False(DrapeLayering.Split(double.NaN, 0.1).Ok, "NaN total");
            run.False(DrapeLayering.Split(0.3, double.NaN).Ok, "NaN minimum");
            run.False(DrapeLayering.Split(double.PositiveInfinity, 0.1).Ok, "infinite total");
            run.False(DrapeLayering.Split(0.3, double.PositiveInfinity).Ok, "infinite minimum");
            run.False(DrapeLayering.Split(0.0, 0.1).Ok, "zero total");
            run.False(DrapeLayering.Split(0.3, 0.0).Ok, "zero minimum");
            run.False(DrapeLayering.Split(-0.3, 0.1).Ok, "negative total");
            run.False(DrapeLayering.Split(0.3, -0.1).Ok, "negative minimum");
        });

        run.Case("a refusal carries zeroed thicknesses, not garbage", () =>
        {
            DrapeLayerSplit split = DrapeLayering.Split(0.1, 0.1);
            run.False(split.Ok, "refused");
            run.Within(split.ImageryThickness, 0.0, Tight, "imagery thickness on refusal");
            run.Within(split.LowerThickness, 0.0, Tight, "lower thickness on refusal");
        });

        return run.Report("drape layering");
    }
}
