using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Which layers are cut into the ground — the water bodies and the road surfaces only — and what their
/// rings become: one subdivision per polygon, holes left out of it.
/// </summary>
internal static class GroundCutsTests
{
    private static readonly SiteVertex[] Ring =
        [new(0, 0, null), new(10, 0, null), new(10, 10, null), new(0, 10, null)];

    internal static int Run()
    {
        TestRun run = new();

        run.Case("every step that cuts polygons names its layer, and nothing else does", () =>
        {
            // The shim dispatches through this and SlowStepNotice takes its words from it, so a kind
            // missing here would run no step at all and a kind wrongly here would run the wrong one.
            run.True(GroundCuts.LayerOf(ImportStepKind.Water) == GroundLayer.Water, "water");
            run.True(GroundCuts.LayerOf(ImportStepKind.RoadPolygons) == GroundLayer.RoadSurface, "road surfaces");

            // ADR 0015: the land layers are drawn on land plans, and their GroundLayer members are
            // kept only so an earlier build's cuts are still recognised.
            run.True(GroundCuts.LayerOf(ImportStepKind.LandUse) is null, "land use is drawn, not cut");
            run.True(GroundCuts.LayerOf(ImportStepKind.LandCover) is null, "and so is land cover");

            run.Equal(
                Enum.GetValues<ImportStepKind>().Count(kind => GroundCuts.LayerOf(kind) is not null),
                2,
                "one step kind per surface cut, and no other kind claiming one");
            run.True(GroundCuts.LayerOf(ImportStepKind.RoadCentrelines) is null, "the centrelines cut nothing");
            run.True(GroundCuts.LayerOf(ImportStepKind.ImageryDrape) is null, "nor does the drape");
        });

        run.Case("every step kind agrees between the ground cuts and the vector-layer table", () =>
        {
            // Two maps from a step kind: the ground layer it cuts, and the vector layer it places. A
            // kind in the first and not the second would cut a layer the reader never picked a file
            // for; the words are one spelling, read from the table.
            foreach (ImportStepKind kind in Enum.GetValues<ImportStepKind>())
            {
                SiteVectorLayer? layer = SiteVectorLayers.Of(kind);
                if (GroundCuts.LayerOf(kind) is not { } ground)
                {
                    continue;
                }

                run.True(layer?.DrawnFrom == SiteGeometryKinds.Areas, $"{kind}: a ground cut is drawn from areas");
                run.Equal(GroundLayerWords.For(ground).Label, layer?.Label, $"{kind}: one spelling of its words");
            }
        });

        run.Case("a water polygon is one cut with its islands in it", () =>
        {
            SiteFeature[] rings =
            [
                Feature("Lake", subtype: "reservoir", polygon: 1),
                Feature("Lake", subtype: "reservoir", polygon: 1, hole: true),
                Feature("Lake", subtype: "reservoir", polygon: 1, hole: true),
                Feature(string.Empty, subtype: "pond", polygon: 2),
            ];

            (IReadOnlyList<GroundCut> cuts, int stranded) = GroundCuts.For(rings);

            run.Equal(cuts.Count, 2, "two polygons, not four rings");
            run.Equal(stranded, 0, "every hole found its polygon");
            run.Equal(cuts[0].Holes.Count, 2, "both islands are left out of the lake");
            run.Equal(cuts[1].Holes.Count, 0, "the pond has none");
        });

        run.Case("a cut's material word is its layer's, whatever its subtype or class says", () =>
        {
            // The word rides on the stamp's layer (GroundLayerWords.MaterialWord), so a pond is
            // water as much as a reservoir is, and a footway publishing subtype grass is still asphalt.
            run.Equal(GroundLayerWords.For(GroundLayer.Water).MaterialWord, "water", "water bodies");
            run.Equal(GroundLayerWords.For(GroundLayer.RoadSurface).MaterialWord, "asphalt", "road surfaces");
            run.Equal(GroundLayerWords.For(GroundLayer.LandUse).MaterialWord, null, "an earlier build's land-use cut names none");
            run.Equal(GroundLayerWords.For(GroundLayer.LandCover).MaterialWord, null, "and nor does a land-cover one");
        });

        run.Case("holes whose polygon has no outer ring are dropped and counted", () =>
        {
            // The reader drops a ring with too few vertices to close, so a polygon can reach here as
            // holes alone. A hole with nothing to be cut out of is not a subdivision.
            SiteFeature[] rings =
            [
                Feature("Lake", subtype: "reservoir", polygon: 1, hole: true),
                Feature("Pond", subtype: "pond", polygon: 2),
            ];

            (IReadOnlyList<GroundCut> cuts, int stranded) = GroundCuts.For(rings);

            run.Equal(cuts.Count, 1, "only the polygon that still has its outer ring");
            run.Equal(stranded, 1, "the orphaned hole is counted so the import can say so");
            run.Equal(cuts[0].Outer.Name, "Pond", "and the surviving polygon is not the orphan's");
        });

        run.Case("rings are grouped by their polygon, never by adjacency", () =>
        {
            // Polygon 2's outer ring was dropped by the reader. Its holes must not attach themselves
            // to polygon 1, which would cut two lakes' worth of islands out of one lake.
            SiteFeature[] rings =
            [
                Feature("First", subtype: "reservoir", polygon: 1),
                Feature("Second", subtype: "reservoir", polygon: 2, hole: true),
                Feature("Third", subtype: "reservoir", polygon: 3),
            ];

            (IReadOnlyList<GroundCut> cuts, int stranded) = GroundCuts.For(rings);

            run.Equal(cuts.Count, 2, "the two polygons with an outer ring");
            run.Equal(cuts[0].Holes.Count, 0, "the first lake keeps no island it was never given");
            run.Equal(stranded, 1, "the middle polygon's hole is stranded");
        });

        run.Case("the names are the outer rings', in cut order", () =>
        {
            SiteFeature[] rings =
            [
                Feature("Lake", subtype: "reservoir", polygon: 1),
                Feature("Lake", subtype: "reservoir", polygon: 1, hole: true),
                Feature(string.Empty, subtype: "pond", polygon: 2),
            ];

            (IReadOnlyList<GroundCut> cuts, _) = GroundCuts.For(rings);
            IReadOnlyList<string?> names = GroundCuts.Names(cuts);

            run.Equal(names.Count, 2, "one name per cut, so the stamps line up with the subdivisions");
            run.Equal(names[0], "Lake", "the outer ring's name");
            run.Equal(names[1], string.Empty, "an unnamed polygon is stamped by its position instead");
        });

        run.Case("an empty layer is no cuts rather than a throw", () =>
        {
            (IReadOnlyList<GroundCut> cuts, int stranded) = GroundCuts.For([]);
            run.Equal(cuts.Count, 0, "nothing to cut");
            run.Equal(stranded, 0, "nothing stranded");
        });

        return run.Report("ground cuts");
    }

    private static SiteFeature Feature(
        string name,
        string subtype,
        int polygon,
        bool hole = false,
        string classification = "")
        => new()
        {
            Vertices = Ring,
            IsClosed = true,
            Name = name,
            Subtype = subtype,
            Classification = classification,
            IsHole = hole,
            PolygonOrdinal = polygon,
        };
}
