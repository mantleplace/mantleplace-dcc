using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Whether one surface lies on another, measured vertically rather than eyeballed.
/// </summary>
/// <remarks>
/// <para>
/// The question this answers came from a screenshot: 23 site-boundary subdivisions cut into one
/// terrain, each drawing its own contour lines, and the contours do not line up with the continuous
/// ground beneath them. Two explanations fit that picture and they call for opposite responses. If
/// every subdivision vertex sits <em>on</em> the ground to within a millimetre, the surfaces agree
/// and the contour mismatch is Revit generating contours per element — nothing to fix, something to
/// document. If the vertices deviate, the subdivisions are genuinely different surfaces and that is
/// a defect.
/// </para>
/// <para>
/// ⛔ The arithmetic lives here rather than in the shim for <c>HPS-02</c>'s reason, and for a
/// sharper one: this session already watched a plausible-sounding claim about Revit survive two
/// weeks in a comment because nothing asserted it. A number that decides between "fix it" and
/// "document it" has to be produced by code a test can drive without launching Revit.
/// </para>
/// </remarks>
internal static class SurfaceAgreementTests
{
    /// <summary>A unit square at z = 0, as two triangles.</summary>
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
            // ⛔ Signed deltas would cancel across a surface that wanders either side of the ground,
            // and "mean deviation 0.0" on a surface that is wrong everywhere is the exact shape of
            // reassuring number this probe exists to avoid producing.
            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                FlatVertices,
                FlatTriangles,
                [new SurfacePoint(2.0, 2.0, 1.0), new SurfacePoint(8.0, 8.0, -1.0)]);

            run.Within(agreement.MeanAbsDeltaM, 1.0, 1e-9, "one metre out on average, not zero");
            run.Within(agreement.MaxAbsDeltaM, 1.0, 1e-9, "and one metre out at worst");
        });

        run.Case("the ground's height is interpolated across a sloped triangle, not snapped", () =>
        {
            // A single triangle rising to 3 m at one corner. Sampled at its centroid the ground is
            // at exactly 1 m — the average of 0, 0 and 3 — so a sample there deviates by nothing.
            // Snapping to the nearest vertex instead would report 1 m or 2 m and invent a defect.
            SurfacePoint[] vertices = [new(0.0, 0.0, 0.0), new(3.0, 0.0, 0.0), new(0.0, 3.0, 3.0)];
            SurfaceTriangle[] triangles = [new(0, 1, 2)];

            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                vertices, triangles, [new SurfacePoint(1.0, 1.0, 1.0)]);

            run.Equal(agreement.Sampled, 1, "the centroid is inside the triangle");
            run.Within(agreement.MaxAbsDeltaM, 0.0, 1e-9, "the interpolated ground is exactly 1 m");
        });

        run.Case("a sample outside the footprint is counted, not measured", () =>
        {
            // A subdivision that reaches past the ground it was cut from is a different finding
            // from one that floats above it, and folding the two together would hide both. It also
            // must not be scored as a zero deviation, which would make the summary look better the
            // further out the sample lay.
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
            // The diagonal of the flat square is shared by both triangles. A strict inside test
            // drops every vertex that happens to land on an edge — which on a mesh whose samples
            // come from the same point set is most of them — and the probe would then report a
            // surface as missing that it sits exactly on.
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
            // The shim feeds this from Revit's own tessellation. A malformed face there must degrade
            // to a thinner measurement, because a probe that throws leaves the reader with nothing.
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
            // ⛔ The ends are the finding. A subdivision that reaches past the ground it was cut
            // from does so at its extremes, so a sample that stops short of the last vertex is a
            // sample that cannot see the thing most worth seeing. Taking a prefix instead would
            // sample one part of a tessellation-ordered list, which is not a random part.
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
            // ⛔ The failure this guards is silent and reads as the defect being hunted: a triangle
            // filed in the wrong index cell makes a sample that sits exactly on the surface report
            // as off the footprint, or — worse — as a deviation. So the surface is big enough to
            // span many cells, it slopes in both axes so a wrong triangle gives a wrong height
            // rather than the same one, and every vertex of it is offered back as a sample. All of
            // them must land, and all must agree to the millimetre.
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
            // The complement of the case above. An index that clamps a query into the nearest cell
            // would make anything outside the surface match its edge triangles and report a
            // deviation instead of an absence — quietly turning "this subdivision overruns its
            // ground" into "this subdivision is the wrong height".
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
            // ⛔ The claim this whole arm rests on: a control taken off the surface a mesh
            // approximates reads that mesh's chords falling away from it, and nothing else. Testing
            // it needs a surface with chords to fall — a flat mesh has none, and would let a reading
            // of zero pass for a measurement.
            //
            // An arch across x, z = sin(pi x / 10), tessellated coarsely at x = 0, 5, 10: between
            // x = 0 and x = 5 the mesh runs straight from z = 0 to z = 1, so at x = 2.5 its chord
            // sits at 0.5 while the surface is at sin(pi/4) = 0.7071. The sag between them is the
            // floor, and it is arithmetic rather than an eyeball.
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
            // ⛔ A floor measured over a corner of a surface is a floor about that corner. Silently
            // averaging one and calling it the mesh's is how a deviation gets judged against a
            // yardstick taken somewhere else.
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
            // ⛔ Interpolation at a vertex is exact by construction, so this control reads 0.000 m
            // while having measured no chord at all. A floor of zero and a floor that was never
            // taken produce the same number and must not produce the same verdict.
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
            // ⛔ Tolerance wins over the floor. A surface agreeing to a millimetre has agreed
            // however coarsely its neighbour's chords are drawn: the floor bounds what a
            // disagreement has to clear, never what an agreement is allowed to be.
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

    /// <summary>A floor of <paramref name="metres"/>, taken from samples that were not vertices.</summary>
    private static TessellationNoiseFloor FloorOf(double metres) => new(8, 0, 0, metres);
}
