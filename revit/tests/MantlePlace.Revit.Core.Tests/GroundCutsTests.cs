using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// What each polygon layer's rings become: one subdivision per ring for the two land layers, one
/// subdivision per polygon — holes left out of it — for the water bodies and the road surfaces.
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
            run.True(GroundCuts.LayerOf(ImportStepKind.SiteBoundaries) == GroundLayer.LandUse, "land use");
            run.True(GroundCuts.LayerOf(ImportStepKind.LandCover) == GroundLayer.LandCover, "land cover");
            run.True(GroundCuts.LayerOf(ImportStepKind.Water) == GroundLayer.Water, "water");
            run.True(GroundCuts.LayerOf(ImportStepKind.RoadPolygons) == GroundLayer.RoadSurface, "road surfaces");

            run.Equal(
                Enum.GetValues<ImportStepKind>().Count(kind => GroundCuts.LayerOf(kind) is not null),
                Enum.GetValues<GroundLayer>().Length,
                "one step kind per layer, and no other kind claiming one");
            run.True(GroundCuts.LayerOf(ImportStepKind.RoadCentrelines) is null, "the centrelines cut nothing");
            run.True(GroundCuts.LayerOf(ImportStepKind.ImageryDrape) is null, "nor does the drape");
        });

        run.Case("which layers cut a polygon whole, and which cut every ring", () =>
        {
            run.True(GroundCuts.CutsWholePolygons(GroundLayer.Water), "a water body's holes are islands");
            run.True(GroundCuts.CutsWholePolygons(GroundLayer.RoadSurface), "a road network's holes are city blocks");
            run.False(GroundCuts.CutsWholePolygons(GroundLayer.LandUse), "land use keeps the stamps it has");
            run.False(GroundCuts.CutsWholePolygons(GroundLayer.LandCover), "and so does land cover");
        });

        run.Case("a land layer's holes stay subdivisions of their own, in the order they were read", () =>
        {
            SiteFeature[] rings =
            [
                Feature("Forest", subtype: "forest", polygon: 1),
                Feature("Forest", subtype: "forest", polygon: 1, hole: true),
                Feature("Park", subtype: "park", polygon: 2),
            ];

            (IReadOnlyList<GroundCut> cuts, int stranded) = GroundCuts.For(GroundLayer.LandCover, rings);

            run.Equal(cuts.Count, 3, "one cut per ring, the clearing included");
            run.Equal(stranded, 0, "nothing was left out");
            run.Equal(cuts.Count(cut => cut.Holes.Count > 0), 0, "a land cut never carries holes");
            run.Equal(cuts[0].Keyword, "wild grass", "the forest's own subtype");
            run.Equal(cuts[1].Keyword, null, "the clearing is not forest, so it is not named as forest");
            run.Equal(cuts[2].Keyword, "grass", "the park");
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

            (IReadOnlyList<GroundCut> cuts, int stranded) = GroundCuts.For(GroundLayer.Water, rings);

            run.Equal(cuts.Count, 2, "two polygons, not four rings");
            run.Equal(stranded, 0, "every hole found its polygon");
            run.Equal(cuts[0].Holes.Count, 2, "both islands are left out of the lake");
            run.Equal(cuts[1].Holes.Count, 0, "the pond has none");
            run.Equal(cuts[0].Keyword, "water", "the word comes from the layer");
            run.Equal(cuts[1].Keyword, "water", "a pond is water as much as a reservoir is");
        });

        run.Case("a road surface takes the layer's word whatever its class says", () =>
        {
            SiteFeature[] rings =
            [
                Feature(string.Empty, subtype: string.Empty, polygon: 1, classification: "motorway"),
                Feature(string.Empty, subtype: "grass", polygon: 2, classification: "footway"),
            ];

            (IReadOnlyList<GroundCut> cuts, _) = GroundCuts.For(GroundLayer.RoadSurface, rings);

            run.Equal(cuts.Count, 2, "one cut per published surface");
            run.Equal(cuts[0].Keyword, "asphalt", "the motorway");
            run.Equal(cuts[1].Keyword, "asphalt", "a subtype the land table knows does not make a road grass");
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

            (IReadOnlyList<GroundCut> cuts, int stranded) = GroundCuts.For(GroundLayer.Water, rings);

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

            (IReadOnlyList<GroundCut> cuts, int stranded) = GroundCuts.For(GroundLayer.Water, rings);

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

            (IReadOnlyList<GroundCut> cuts, _) = GroundCuts.For(GroundLayer.Water, rings);
            IReadOnlyList<string?> names = GroundCuts.Names(cuts);

            run.Equal(names.Count, 2, "one name per cut, so the stamps line up with the subdivisions");
            run.Equal(names[0], "Lake", "the outer ring's name");
            run.Equal(names[1], string.Empty, "an unnamed polygon is stamped by its position instead");
        });

        run.Case("KeywordsByStamp pairs each keyworded cut with its phrase and leaves the rest out", () =>
        {
            SiteFeature[] rings =
            [
                Feature("Forest", subtype: "forest", polygon: 1),
                Feature("Forest", subtype: "forest", polygon: 1, hole: true),
                Feature("Yard", subtype: "urban", polygon: 2),
                Feature("Lawn", subtype: "grass", polygon: 3),
            ];

            (IReadOnlyList<GroundCut> cuts, _) = GroundCuts.For(GroundLayer.LandCover, rings);
            IReadOnlyDictionary<string, string> byStamp = GroundCuts.KeywordsByStamp(cuts, ["c/1", "c/2", "c/3", "c/4"]);

            run.Equal(byStamp.Count, 2, "the forest's outer ring and the lawn");
            run.Equal(byStamp.GetValueOrDefault("c/1"), "wild grass", "forest");
            run.Equal(byStamp.GetValueOrDefault("c/2"), null, "the hole");
            run.Equal(byStamp.GetValueOrDefault("c/3"), null, "urban");
            run.Equal(byStamp.GetValueOrDefault("c/4"), "grass", "grass");
        });

        run.Case("KeywordsByStamp refuses stamps that do not line up with the cuts", () =>
        {
            (IReadOnlyList<GroundCut> cuts, _) = GroundCuts.For(GroundLayer.Water, [Feature("Lake", "reservoir", 1)]);
            bool threw = false;
            try
            {
                GroundCuts.KeywordsByStamp(cuts, []);
            }
            catch (ArgumentException)
            {
                threw = true;
            }

            run.True(threw, "a keyword on the wrong subdivision is worse than none");
        });

        run.Case("an empty layer is no cuts rather than a throw", () =>
        {
            foreach (GroundLayer layer in Enum.GetValues<GroundLayer>())
            {
                (IReadOnlyList<GroundCut> cuts, int stranded) = GroundCuts.For(layer, []);
                run.Equal(cuts.Count, 0, $"{layer}: nothing to cut");
                run.Equal(stranded, 0, $"{layer}: nothing stranded");
            }
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
