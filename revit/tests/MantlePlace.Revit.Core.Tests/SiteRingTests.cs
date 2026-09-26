using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// A published polygon ring thinned to what Revit can close (<see cref="SiteRings"/>): every vertex
/// within the short-curve tolerance of the last one kept is skipped, the closing edge included, so
/// the loop the shim builds is contiguous by construction.
/// </summary>
/// <remarks>
/// The failure this guards against is a platform-built road polygon ring with edges of 0.1 to
/// 0.6 mm. Dropping such an edge, rather than a vertex, left a gap Revit refused as "not contiguous",
/// and that one ring took the whole road layer down.
/// </remarks>
internal static class SiteRingTests
{
    private const double Tolerance = 0.01;

    // Revit's ShortCurveTolerance is 1/384 ft; the shim hands it over in metres.
    private const double RevitToleranceM = 0.3048 / 384.0;

    internal static int Run()
    {
        TestRun run = new();

        run.Case("a ring with no short edge comes back as published", () =>
        {
            SiteVertex[] ring = Ring((0, 0), (10, 0), (10, 10), (0, 10));
            IReadOnlyList<SiteVertex>? thinned = SiteRings.Thin(ring, Tolerance);

            run.True(thinned is not null, "closes");
            run.Equal(thinned!.Count, 4, "every vertex kept");
            run.True(IsSubsequence(thinned, ring), "in order");
        });

        run.Case("a sub-tolerance edge that is not zero loses a vertex, not an edge, and the ring stays contiguous", () =>
        {
            // The shape the road file carried: a 0.3 mm step inside an otherwise ordinary ring.
            SiteVertex[] ring = Ring((0, 0), (5, 0), (5.0003, 0), (10, 0), (10, 10), (0, 10));
            IReadOnlyList<SiteVertex>? thinned = SiteRings.Thin(ring, RevitToleranceM);

            run.True(thinned is not null, "closes");
            run.Equal(thinned!.Count, 5, "the near vertex skipped");
            run.True(IsSubsequence(thinned, ring), "a subsequence of the published vertices: none moved or made");
            run.True(EveryEdgeLongerThan(thinned, RevitToleranceM), "every edge, the closing one included, is past tolerance");
            run.Within(thinned[2].EastM, 10.0, 0.0, "the next edge starts from the kept vertex");
        });

        run.Case("an exact duplicate vertex is skipped", () =>
        {
            SiteVertex[] ring = Ring((0, 0), (10, 0), (10, 0), (10, 10), (0, 10));
            IReadOnlyList<SiteVertex>? thinned = SiteRings.Thin(ring, Tolerance);

            run.Equal(thinned?.Count ?? -1, 4, "the duplicate gone");
            run.True(EveryEdgeLongerThan(thinned!, Tolerance), "contiguous");
        });

        run.Case("a run of short steps is measured from the last kept vertex, not the previous one", () =>
        {
            // Each step is under tolerance, but 0.012 is past it from 0.
            SiteVertex[] ring = Ring((0, 0), (0.006, 0), (0.012, 0), (0.018, 0), (10, 0), (10, 10));
            IReadOnlyList<SiteVertex>? thinned = SiteRings.Thin(ring, Tolerance);

            run.Equal(thinned?.Count ?? -1, 4, "0, 0.012, 10 and the corner");
            run.Within(thinned![1].EastM, 0.012, 1e-12, "the first vertex past tolerance");
            run.True(EveryEdgeLongerThan(thinned, Tolerance), "contiguous");
        });

        run.Case("a last vertex within tolerance of the first is skipped, and the first is kept", () =>
        {
            // A ring whose closing position missed the first by 0.3 mm survives the reader's exact
            // check, and its closing edge is the short one.
            SiteVertex[] ring = Ring((0, 0), (10, 0), (10, 10), (0, 10), (0.0003, 0));
            IReadOnlyList<SiteVertex>? thinned = SiteRings.Thin(ring, RevitToleranceM);

            run.Equal(thinned?.Count ?? -1, 4, "the near-closing vertex skipped");
            run.Within(thinned![0].EastM, 0.0, 0.0, "the first vertex stays first");
            run.True(EveryEdgeLongerThan(thinned, RevitToleranceM), "the closing edge is past tolerance");
        });

        run.Case("several trailing vertices near the first are all skipped", () =>
        {
            SiteVertex[] ring = Ring((0, 0), (10, 0), (10, 10), (0, 10), (0, 0.009), (0, 0.004));
            IReadOnlyList<SiteVertex>? thinned = SiteRings.Thin(ring, Tolerance);

            run.Equal(thinned?.Count ?? -1, 4, "both trailing ones gone");
            run.True(EveryEdgeLongerThan(thinned!, Tolerance), "contiguous");
        });

        run.Case("a ring that thins below three vertices cannot close", () =>
        {
            SiteVertex[] sliver = Ring((0, 0), (10, 0), (10.005, 0), (0.005, 0));
            run.True(SiteRings.Thin(sliver, Tolerance) is null, "two left: no loop");

            SiteVertex[] speck = Ring((0, 0), (0.001, 0), (0.001, 0.001));
            run.True(SiteRings.Thin(speck, Tolerance) is null, "one left: no loop");
        });

        run.Case("fewer than three vertices cannot close, whatever the tolerance", () =>
        {
            run.True(SiteRings.Thin(Ring((0, 0), (10, 0)), 0.0) is null, "two");
            run.True(SiteRings.Thin([], 0.0) is null, "none");
        });

        run.Case("the distance is measured in plan, and a kept vertex keeps its elevation", () =>
        {
            SiteVertex[] ring =
            [
                new(0, 0, 1.0),
                new(10, 0, 2.0),
                new(10, 0.001, 50.0),
                new(10, 10, 3.0),
            ];
            IReadOnlyList<SiteVertex>? thinned = SiteRings.Thin(ring, Tolerance);

            run.Equal(thinned?.Count ?? -1, 3, "a vertex 1 mm away in plan is skipped however far it is in Z");
            run.Within(thinned![1].ElevationM ?? double.NaN, 2.0, 0.0, "elevation carried as published");
        });

        return run.Report("site rings");
    }

    private static SiteVertex[] Ring(params (double East, double North)[] points)
        => [.. points.Select(point => new SiteVertex(point.East, point.North, null))];

    private static bool IsSubsequence(IReadOnlyList<SiteVertex> thinned, IReadOnlyList<SiteVertex> published)
    {
        int next = 0;
        foreach (SiteVertex vertex in thinned)
        {
            while (next < published.Count && published[next] != vertex)
            {
                next++;
            }

            if (next == published.Count)
            {
                return false;
            }

            next++;
        }

        return true;
    }

    private static bool EveryEdgeLongerThan(IReadOnlyList<SiteVertex> ring, double tolerance)
    {
        for (int index = 0; index < ring.Count; index++)
        {
            SiteVertex from = ring[index];
            SiteVertex to = ring[(index + 1) % ring.Count];
            double east = to.EastM - from.EastM;
            double north = to.NorthM - from.NorthM;
            if (Math.Sqrt((east * east) + (north * north)) <= tolerance)
            {
                return false;
            }
        }

        return true;
    }
}
