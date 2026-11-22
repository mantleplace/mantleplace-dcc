using MantlePlace.Core;

namespace MantlePlace.Core.Tests;

internal static class SurfaceAgreementTests
{
    private static readonly SurfacePoint[] FlatVertices =
    [
        new(0.0, 0.0, 0.0),
        new(10.0, 0.0, 0.0),
        new(10.0, 10.0, 0.0),
        new(0.0, 10.0, 0.0),
    ];

    private static readonly SurfaceTriangle[] FlatTriangles =
    [
        new(0, 1, 2),
        new(0, 2, 3),
    ];

    internal static int Run()
    {
        TestRun run = new();

        run.Case("a sample lying on the surface deviates by nothing", () =>
        {
            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                FlatVertices, FlatTriangles, [new SurfacePoint(5.0, 5.0, 0.0)]);

            run.Equal(agreement.Sampled, 1, "the sample was inside the footprint and measured");
            run.Equal(agreement.OffFootprint, 0, "nothing fell outside");
            run.Within(agreement.MaxAbsDeltaM, 0.0, 1e-9, "no vertical deviation");
        });

        run.Case("a sample above the surface reports its height", () =>
        {
            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                FlatVertices, FlatTriangles, [new SurfacePoint(5.0, 5.0, 2.0)]);

            run.Within(agreement.MaxAbsDeltaM, 2.0, 1e-9, "two metres clear of the ground");
        });

        run.Case("the deviation is absolute, so below counts as much as above", () =>
        {
            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                FlatVertices,
                FlatTriangles,
                [new SurfacePoint(2.0, 2.0, 1.0), new SurfacePoint(8.0, 8.0, -1.0)]);

            run.Within(agreement.MeanAbsDeltaM, 1.0, 1e-9, "one metre out on average, not zero");
            run.Within(agreement.MaxAbsDeltaM, 1.0, 1e-9, "and one metre out at worst");
        });

        run.Case("the ground's height is interpolated across a sloped triangle, not snapped", () =>
        {
            SurfacePoint[] vertices = [new(0.0, 0.0, 0.0), new(3.0, 0.0, 0.0), new(0.0, 3.0, 3.0)];
            SurfaceTriangle[] triangles = [new(0, 1, 2)];

            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                vertices, triangles, [new SurfacePoint(1.0, 1.0, 1.0)]);

            run.Equal(agreement.Sampled, 1, "the centroid is inside the triangle");
            run.Within(agreement.MaxAbsDeltaM, 0.0, 1e-9, "the interpolated ground is exactly 1 m");
        });

        run.Case("a sample outside the footprint is counted, not measured", () =>
        {
            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                FlatVertices,
                FlatTriangles,
                [new SurfacePoint(5.0, 5.0, 0.0), new SurfacePoint(50.0, 50.0, 0.0)]);

            run.Equal(agreement.Sampled, 1, "only the inside sample was measured");
            run.Equal(agreement.OffFootprint, 1, "the outside one is reported separately");
            run.Within(agreement.MaxAbsDeltaM, 0.0, 1e-9, "the outside sample did not inflate the deviation");
        });

        run.Case("a point on a shared edge belongs to the surface, not to neither triangle", () =>
        {
            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                FlatVertices, FlatTriangles, [new SurfacePoint(5.0, 5.0, 0.0), new SurfacePoint(0.0, 0.0, 0.0)]);

            run.Equal(agreement.OffFootprint, 0, "a corner and a point on the diagonal are both on the surface");
        });

        run.Case("a degenerate triangle is skipped rather than dividing by its zero area", () =>
        {
            SurfacePoint[] vertices = [new(0.0, 0.0, 0.0), new(1.0, 0.0, 0.0), new(2.0, 0.0, 0.0)];
            SurfaceTriangle[] triangles = [new(0, 1, 2)];

            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                vertices, triangles, [new SurfacePoint(1.0, 0.0, 0.0)]);

            run.Equal(agreement.Sampled, 0, "a line has no interior to interpolate across");
            run.Equal(agreement.OffFootprint, 1, "and the sample is reported as unmeasurable, not as agreeing");
        });

        run.Case("a triangle index outside the vertex list is skipped rather than thrown on", () =>
        {
            SurfaceTriangle[] triangles = [new(0, 1, 99)];

            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                FlatVertices, triangles, [new SurfacePoint(5.0, 5.0, 0.0)]);

            run.Equal(agreement.Sampled, 0, "nothing measurable was left");
        });

        run.Case("a short list is sampled whole", () =>
        {
            IReadOnlyList<SurfacePoint> source = [new(0.0, 0.0, 0.0), new(1.0, 0.0, 0.0)];

            run.Equal(SurfaceAgreementCheck.Sample(source, 10).Count, 2, "nothing was dropped");
        });

        run.Case("a long list is strided, and keeps both of its ends", () =>
        {
            List<SurfacePoint> source = [];
            for (int i = 0; i < 1000; i++)
            {
                source.Add(new SurfacePoint(i, 0.0, 0.0));
            }

            IReadOnlyList<SurfacePoint> taken = SurfaceAgreementCheck.Sample(source, 10);

            run.Equal(taken.Count, 10, "capped");
            run.Within(taken[0].X, 0.0, 1e-9, "the first vertex is in the sample");
            run.Within(taken[^1].X, 999.0, 1e-9, "and so is the last");
            run.True(taken[1].X > taken[0].X, "the sample advances rather than repeating");
        });

        run.Case("a mesh far larger than one index cell measures every one of its own vertices", () =>
        {
            const int side = 60;
            List<SurfacePoint> vertices = [];
            for (int row = 0; row <= side; row++)
            {
                for (int column = 0; column <= side; column++)
                {
                    vertices.Add(new SurfacePoint(column * 3.0, row * 3.0, (column * 0.7) + (row * 0.3)));
                }
            }

            List<SurfaceTriangle> triangles = [];
            for (int row = 0; row < side; row++)
            {
                for (int column = 0; column < side; column++)
                {
                    int corner = (row * (side + 1)) + column;
                    triangles.Add(new SurfaceTriangle(corner, corner + 1, corner + side + 2));
                    triangles.Add(new SurfaceTriangle(corner, corner + side + 2, corner + side + 1));
                }
            }

            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(vertices, triangles, vertices);

            run.Equal(agreement.Sampled, vertices.Count, "every vertex found the surface it belongs to");
            run.Equal(agreement.OffFootprint, 0, "none was filed into a cell that does not cover it");
            run.Within(agreement.MaxAbsDeltaM, 0.0, 1e-9, "and each one interpolated to its own height");
        });

        run.Case("a sample beyond a many-celled mesh is still outside it", () =>
        {
            const int side = 40;
            List<SurfacePoint> vertices = [];
            for (int row = 0; row <= side; row++)
            {
                for (int column = 0; column <= side; column++)
                {
                    vertices.Add(new SurfacePoint(column * 2.0, row * 2.0, 0.0));
                }
            }

            List<SurfaceTriangle> triangles = [];
            for (int row = 0; row < side; row++)
            {
                for (int column = 0; column < side; column++)
                {
                    int corner = (row * (side + 1)) + column;
                    triangles.Add(new SurfaceTriangle(corner, corner + 1, corner + side + 2));
                    triangles.Add(new SurfaceTriangle(corner, corner + side + 2, corner + side + 1));
                }
            }

            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                vertices, triangles, [new SurfacePoint(500.0, 500.0, 0.0), new SurfacePoint(-500.0, 10.0, 0.0)]);

            run.Equal(agreement.OffFootprint, 2, "both fell outside, neither was clamped onto an edge");
            run.Equal(agreement.Sampled, 0, "and neither was measured");
        });

        run.Case("no samples is a stated fact, not a silent zero", () =>
        {
            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(FlatVertices, FlatTriangles, []);
            string described = SurfaceAgreementCheck.Describe(
                "subdivision 1", agreement, TessellationNoiseFloor.Unmeasured);

            run.Equal(agreement.Sampled, 0, "nothing was measured");
            run.Contains(described, "nothing to measure", "said rather than implied");
        });

        run.Case("agreement within tolerance is described as lying on the ground", () =>
        {
            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                FlatVertices, FlatTriangles, [new SurfacePoint(5.0, 5.0, 0.0)]);

            string described = SurfaceAgreementCheck.Describe(
                "subdivision 1", agreement, TessellationNoiseFloor.Unmeasured);

            run.Contains(described, "subdivision 1", "which surface this is about");
            run.Contains(described, "lies on", "the reading, not just the number");
        });

        run.Case("a deviating surface is described as a different surface", () =>
        {
            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                FlatVertices, FlatTriangles, [new SurfacePoint(5.0, 5.0, 0.4)]);

            string described = SurfaceAgreementCheck.Describe("subdivision 1", agreement, FloorOf(0.01));

            run.Contains(described, "does not lie on", "the opposite reading is said as plainly");
            run.Contains(described, "0.4", "with the number that settles it");
        });

        run.Case("samples off the footprint are named in the sentence", () =>
        {
            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                FlatVertices,
                FlatTriangles,
                [new SurfacePoint(5.0, 5.0, 0.0), new SurfacePoint(50.0, 50.0, 0.0)]);

            run.Contains(
                SurfaceAgreementCheck.Describe("subdivision 1", agreement, FloorOf(0.01)), "outside",
                "a subdivision reaching past its ground is worth a clause of its own");
        });

        run.Case("the noise floor is the reference mesh's own chord error, measured", () =>
        {
            SurfacePoint[] arch =
            [
                new(0.0, 0.0, 0.0), new(5.0, 0.0, 1.0), new(10.0, 0.0, 0.0),
                new(0.0, 10.0, 0.0), new(5.0, 10.0, 1.0), new(10.0, 10.0, 0.0),
            ];

            SurfaceTriangle[] chords = [new(0, 1, 4), new(0, 4, 3), new(1, 2, 5), new(1, 5, 4)];

            double crest = Math.Sin(Math.PI * 0.25);
            TessellationNoiseFloor floor = SurfaceAgreementCheck.MeasureNoiseFloor(
                arch, chords, [new SurfacePoint(2.5, 2.0, crest), new SurfacePoint(7.5, 2.0, crest)]);

            run.Equal(floor.Sampled, 2, "both control samples sat over the mesh");
            run.Equal(floor.Coincident, 0, "neither was one of its vertices");
            run.True(floor.CanClassify, "so this floor can judge a deviation");
            run.Within(floor.FloorM, crest - 0.5, 1e-9, "and it is the chord sag, to the arithmetic");
        });

        run.Case("a control sample the reference surface does not cover is reported, not dropped", () =>
        {
            TessellationNoiseFloor floor = SurfaceAgreementCheck.MeasureNoiseFloor(
                FlatVertices, FlatTriangles,
                [new SurfacePoint(5.0, 5.0, 0.02), new SurfacePoint(500.0, 500.0, 0.0)]);

            run.Equal(floor.Sampled, 1, "one control sample landed on the surface");
            run.Equal(floor.OffFootprint, 1, "and one missed it entirely");
            run.Contains(
                SurfaceAgreementCheck.DescribeNoiseFloor(floor), "outside its footprint",
                "which the floor's own line says out loud");
        });

        run.Case("a control that only landed on the mesh's own vertices establishes nothing", () =>
        {
            TessellationNoiseFloor floor = SurfaceAgreementCheck.MeasureNoiseFloor(
                FlatVertices, FlatTriangles, FlatVertices);

            run.Equal(floor.Sampled, FlatVertices.Length, "every control sample was measured");
            run.Equal(floor.Coincident, FlatVertices.Length, "and every one of them was a vertex");
            run.Within(floor.FloorM, 0.0, 1e-9, "which reads as no deviation");
            run.False(floor.CanClassify, "so there is no floor to judge anything against");
            run.Contains(
                SurfaceAgreementCheck.DescribeNoiseFloor(floor), "not measured",
                "and the report says so rather than printing 0.000 m");
        });

        run.Case("a deviation inside the noise floor is inconclusive, not a disagreement", () =>
        {
            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                FlatVertices, FlatTriangles, [new SurfacePoint(5.0, 5.0, 0.01)]);

            string described = SurfaceAgreementCheck.Describe("subdivision 1", agreement, FloorOf(0.02));

            run.Contains(described, "Inconclusive", "the verdict is stated, not left to arithmetic");
            run.False(described.Contains("does not lie on", StringComparison.Ordinal),
                "and it is not reported as a surface that differs");
        });

        run.Case("a deviation equal to the noise floor is inconclusive", () =>
        {
            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                FlatVertices, FlatTriangles, [new SurfacePoint(5.0, 5.0, 0.02)]);

            run.Contains(
                SurfaceAgreementCheck.Describe("subdivision 1", agreement, FloorOf(0.02)), "Inconclusive",
                "the boundary belongs to the side that claims less");
        });

        run.Case("agreement within tolerance survives a floor far larger than it", () =>
        {
            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                FlatVertices, FlatTriangles, [new SurfacePoint(5.0, 5.0, 0.0004)]);

            string described = SurfaceAgreementCheck.Describe("subdivision 1", agreement, FloorOf(0.05));

            run.Contains(described, "lies on", "the agreement is reported as an agreement");
            run.False(described.Contains("Inconclusive", StringComparison.Ordinal),
                "and a large floor does not take it away");
        });

        run.Case("a deviation above the floor is a disagreement, and names the floor it beat", () =>
        {
            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                FlatVertices, FlatTriangles, [new SurfacePoint(5.0, 5.0, 0.4)]);

            string described = SurfaceAgreementCheck.Describe("subdivision 1", agreement, FloorOf(0.02));

            run.Contains(described, "does not lie on", "the reading is said plainly");
            run.Contains(described, "0.02", "against the floor it had to clear");
        });

        run.Case("without a floor a deviation is unclassified rather than a disagreement", () =>
        {
            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                FlatVertices, FlatTriangles, [new SurfacePoint(5.0, 5.0, 0.4)]);

            string described = SurfaceAgreementCheck.Describe(
                "subdivision 1", agreement, TessellationNoiseFloor.Unmeasured);

            run.Contains(described, "Unclassified", "a missing control is a fact about the run");
            run.False(described.Contains("does not lie on", StringComparison.Ordinal),
                "and it does not license the verdict the control was there to support");
            run.False(described.Contains("deviates from the reference surface", StringComparison.Ordinal),
                "nor that verdict in other words — a reader who stops at the first comma has "
                + "been told the thing this branch withholds");
        });

        return run.Report("surface agreement");
    }

    private static TessellationNoiseFloor FloorOf(double metres) => new(8, 0, 0, metres);
}
