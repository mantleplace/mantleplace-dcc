using MantlePlace.Core;

namespace MantlePlace.Core.Tests;

internal static class GroundCutBatchesTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("roads that lie apart are cut together, so a city's roads are tens of subdivisions, not a thousand", () =>
        {
            List<GroundCut> grid = [.. Enumerable.Range(0, 1_023).Select(index => Square(index % 40 * 20.0, index / 40 * 20.0, 10.0))];

            IReadOnlyList<IReadOnlyList<int>> batches = GroundCutBatches.Of(grid);

            run.Equal(batches.Count, 21, "1,023 roads in batches of at most 50");
            run.True(batches.All(batch => batch.Count <= GroundCutBatches.MostCuts), "no batch holds more than the cap");
            run.Equal(
                string.Join(",", batches.SelectMany(batch => batch).Order()),
                string.Join(",", Enumerable.Range(0, 1_023)),
                "every road is in exactly one batch");
        });

        run.Case("a batch stays in one place, so a subdivision does not span the site", () =>
        {
            List<GroundCut> alternating = [];
            for (int step = 0; step < 50; step++)
            {
                alternating.Add(Square(step % 10 * 20.0, step / 10 * 20.0, 10.0));
                alternating.Add(Square(5_000 + (step % 10 * 20.0), 5_000 + (step / 10 * 20.0), 10.0));
            }

            IReadOnlyList<IReadOnlyList<int>> batches = GroundCutBatches.Of(alternating);

            run.Equal(batches.Count, 2, "two clusters of fifty need two batches, as they did in file order");
            run.True(
                batches.All(batch => batch.Select(index => index % 2).Distinct().Count() == 1),
                "no batch mixes the two clusters");
            run.Equal(
                string.Join(",", batches.SelectMany(batch => batch).Order()),
                string.Join(",", Enumerable.Range(0, 100)),
                "every cut is placed exactly once");
            run.Equal(Describe(batches), Describe(GroundCutBatches.Of(alternating)), "the same input gives the same batches");
        });

        run.Case("a cut with no outer ring is still placed once", () =>
        {
            GroundCut empty = new() { Outer = new SiteFeature { Vertices = [], IsClosed = true } };

            IReadOnlyList<IReadOnlyList<int>> batches = GroundCutBatches.Of([Square(0, 0, 10), empty, Square(500, 500, 10)]);

            run.Equal(
                string.Join(",", batches.SelectMany(batch => batch).Order()),
                "0,1,2",
                "each cut is in exactly one batch");
        });

        run.Case("roads whose extents overlap or nearly touch are never cut together", () =>
        {
            GroundCut west = Square(0, 0, 10);
            GroundCut overlapping = Square(5, 5, 10);
            GroundCut touching = Square(10, 0, 10);
            GroundCut withinGap = Square(10 + (GroundCutBatches.GapMetres / 2), 10 + (GroundCutBatches.GapMetres / 2), 10);
            GroundCut apart = Square(100, 100, 10);

            IReadOnlyList<IReadOnlyList<int>> batches = GroundCutBatches.Of([west, overlapping, touching, withinGap, apart]);

            foreach (IReadOnlyList<int> batch in batches)
            {
                run.False(batch.Contains(0) && batch.Contains(1), "an overlapping road is cut apart from the first");
                run.False(batch.Contains(0) && batch.Contains(2), "so is one sharing an edge");
                run.False(batch.Contains(0) && batch.Contains(3), "and one closer than the gap at a corner");
            }

            run.True(batches[0].Contains(4), "a road well clear of the first joins its batch");
        });

        run.Case("a hole lies inside its road's extent, so it never pushes a road out of a batch", () =>
        {
            GroundCut ring = Square(0, 0, 100);
            GroundCut withIsland = new() { Outer = ring.Outer, Holes = [Square(40, 40, 10).Outer] };

            IReadOnlyList<IReadOnlyList<int>> batches = GroundCutBatches.Of([withIsland, Square(200, 0, 10)]);

            run.Equal(batches.Count, 1, "the road with an island and a road far off share one cut");
        });

        run.Case("the batches are the same on every import of the same layer", () =>
        {
            List<GroundCut> cuts = [.. Enumerable.Range(0, 300).Select(index => Square(index * 7 % 113 * 9.0, index * 13 % 97 * 9.0, 12.0))];

            run.Equal(
                Describe(GroundCutBatches.Of(cuts)),
                Describe(GroundCutBatches.Of(cuts)),
                "one answer for one layer");
        });

        run.Case("a batch carries a bounded number of vertices, so one heavy batch cannot hold the import for an hour", () =>
        {
            List<GroundCut> heavy = [.. Enumerable.Range(0, 12).Select(index => Ring(index * 200.0, 0, 300))];

            IReadOnlyList<IReadOnlyList<int>> batches = GroundCutBatches.Of(heavy);

            run.True(
                batches.All(batch => batch.Sum(index => VertexCount(heavy[index])) <= GroundCutBatches.MostVertices),
                "no batch carries more than the vertex budget");
            run.Equal(batches.Count, 4, "twelve roads of 300 vertices go three to a batch");
        });

        run.Case("a road over the vertex budget is cut on its own, and its holes count toward it", () =>
        {
            GroundCut huge = Ring(0, 0, GroundCutBatches.MostVertices + 1);
            GroundCut holed = new()
            {
                Outer = Ring(500, 0, 10).Outer,
                Holes = [Ring(500, 0, GroundCutBatches.MostVertices).Outer],
            };
            GroundCut small = Square(1_000, 0, 10);

            IReadOnlyList<IReadOnlyList<int>> batches = GroundCutBatches.Of([huge, holed, small]);

            run.Equal(Describe(batches), "0|1|2", "each road over the budget is its own cut, and the small one is left its own");
        });

        run.Case("nothing to cut is no batch", () =>
        {
            run.Equal(GroundCutBatches.Of([]).Count, 0, "no cuts, no batches");
        });

        return run.Report("ground cut batches");
    }

    private static string Describe(IReadOnlyList<IReadOnlyList<int>> batches)
        => string.Join("|", batches.Select(batch => string.Join(",", batch)));

    private static int VertexCount(GroundCut cut) => cut.Outer.Vertices.Count + cut.Holes.Sum(hole => hole.Vertices.Count);

    private static GroundCut Ring(double east, double north, int vertices) => new()
    {
        Outer = new SiteFeature
        {
            Vertices =
            [
                .. Enumerable.Range(0, vertices).Select(step => new SiteVertex(
                    east + (50.0 * Math.Cos(2 * Math.PI * step / vertices)),
                    north + (50.0 * Math.Sin(2 * Math.PI * step / vertices)),
                    null)),
            ],
            IsClosed = true,
        },
    };

    private static GroundCut Square(double east, double north, double size) => new()
    {
        Outer = new SiteFeature
        {
            Vertices =
            [
                new(east, north, null),
                new(east + size, north, null),
                new(east + size, north + size, null),
                new(east, north + size, null),
            ],
            IsClosed = true,
        },
    };
}
