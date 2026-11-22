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
                string.Join(",", batches.SelectMany(batch => batch)),
                string.Join(",", Enumerable.Range(0, 1_023)),
                "every road is in exactly one batch, in the order it was read");
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

        run.Case("nothing to cut is no batch", () =>
        {
            run.Equal(GroundCutBatches.Of([]).Count, 0, "no cuts, no batches");
        });

        return run.Report("ground cut batches");
    }

    private static string Describe(IReadOnlyList<IReadOnlyList<int>> batches)
        => string.Join("|", batches.Select(batch => string.Join(",", batch)));

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
