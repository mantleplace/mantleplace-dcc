using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The coextension report: which site-boundary subdivisions carry no information because their
/// footprint is the ground's own, stated rather than acted on.
/// </summary>
/// <remarks>
/// <para>
/// The observed site: 1,086 x 1,080 m, 23 subdivisions cut into one ground, two of them 707 x 555 m
/// and 667 x 325 m. Those two are large and are <em>not</em> the case this catches — a subdivision
/// covering two thirds of a site still says which two thirds. The case is a ring that reproduces the
/// ground's own outline, which marks the whole site as one region while costing a re-triangulation,
/// a material and a set of contour lines that disagree with the ground's.
/// </para>
/// <para>
/// ⛔ The test decides only whether the two footprints are the same footprint. It ranks nothing,
/// arbitrates nothing between overlapping polygons, and produces no coverage fraction — each of
/// those is a derivation this repository does not do (root <c>CLAUDE.md</c>, "The boundary that
/// keeps this client thin"). The published polygons are drawn either way.
/// </para>
/// </remarks>
internal static class SiteBoundaryCoextensionTests
{
    /// <summary>The observed ground: 1,086 x 1,080 m with its origin at the frame's origin.</summary>
    private static readonly FootprintExtent Ground = new(0.0, 0.0, 1086.0, 1080.0);

    internal static int Run()
    {
        TestRun run = new();

        run.Case("a ring reproducing the ground's outline is coextensive", () =>
        {
            run.True(
                SiteBoundaryCoextension.IsCoextensive(Ground, Ground),
                "the ground's own footprint is coextensive with itself");
        });

        run.Case("the big-but-partial subdivisions of the observed site are not", () =>
        {
            // 707 x 555 m and 667 x 325 m — substantial fractions, and each still says WHERE.
            run.False(
                SiteBoundaryCoextension.IsCoextensive(new FootprintExtent(0.0, 0.0, 707.0, 555.0), Ground),
                "707 x 555 m of a 1,086 x 1,080 m ground is a region, not the whole");
            run.False(
                SiteBoundaryCoextension.IsCoextensive(new FootprintExtent(200.0, 300.0, 867.0, 625.0), Ground),
                "667 x 325 m placed inside the ground is a region, not the whole");
        });

        run.Case("a footprint the same size but somewhere else is not coextensive", () =>
        {
            // Same width and depth, shifted half a site east: the sizes match and the sites do not.
            run.False(
                SiteBoundaryCoextension.IsCoextensive(new FootprintExtent(543.0, 0.0, 1629.0, 1080.0), Ground),
                "coextension is about the four edges, not about width and depth alone");
        });

        run.Case("the tolerance is a fraction of the ground, and it bites on every edge", () =>
        {
            double slackEast = SiteBoundaryCoextension.CoextensionFraction * Ground.WidthM;

            run.True(
                SiteBoundaryCoextension.IsCoextensive(
                    new FootprintExtent(slackEast * 0.5, 0.0, 1086.0, 1080.0), Ground),
                "an edge inside the tolerance is still the same edge");
            run.False(
                SiteBoundaryCoextension.IsCoextensive(
                    new FootprintExtent(slackEast * 2.0, 0.0, 1086.0, 1080.0), Ground),
                "an edge outside the tolerance is not");
        });

        run.Case("a degenerate ground decides nothing", () =>
        {
            // No ground extent means no tolerance to measure against, and "coextensive with a line"
            // is a sentence nobody can act on. Silence beats a made-up verdict.
            run.False(
                SiteBoundaryCoextension.IsCoextensive(Ground, new FootprintExtent(0.0, 0.0, 0.0, 1080.0)),
                "a ground with no width decides nothing");
            run.False(
                SiteBoundaryCoextension.IsCoextensive(
                    new FootprintExtent(0.0, 0.0, double.NaN, 1080.0), Ground),
                "a footprint with a non-finite edge decides nothing");
        });

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

        run.Case("the report names the subdivision and both footprints", () =>
        {
            string? line = SiteBoundaryCoextension.Describe("Riverside", 7, Ground, Ground);

            run.True(line is not null, "a coextensive subdivision is reported");
            run.Contains(line, "Riverside", "it names the subdivision");
            run.Contains(line, "position 7", "it says which feature, because two may share a name");
            run.Contains(line, "1,086 x 1,080 m", "it states the footprints it compared");
            run.Contains(line, "1%", "it states the tolerance it applied");
        });

        run.Case("the report says nothing about a subdivision that is not coextensive", () =>
        {
            run.True(
                SiteBoundaryCoextension.Describe("Riverside", 7, new FootprintExtent(0.0, 0.0, 707.0, 555.0), Ground)
                    is null,
                "nothing to report is reported as nothing");
        });

        run.Case("the report neither refuses nor recommends", () =>
        {
            string line = SiteBoundaryCoextension.Describe("Riverside", 7, Ground, Ground)!;

            foreach (string forbidden in new[] { "skip", "remove", "delete", "should" })
            {
                run.False(
                    line.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"it does not tell the curator to \"{forbidden}\" anything");
            }

            // The only percentage in the sentence is the tolerance. "94% of the site is covered" is
            // the coverage fraction the issue refuses, because it invites the plugin to act on it.
            run.False(line.Contains("% of the", StringComparison.Ordinal), "it publishes no coverage fraction");
            run.True(line.Contains("tolerance", StringComparison.Ordinal), "the one percentage is named as the tolerance");
        });

        return run.Report("site boundary coextension");
    }
}
