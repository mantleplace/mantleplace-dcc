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

        run.Case("no samples is a stated fact, not a silent zero", () =>
        {
            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(FlatVertices, FlatTriangles, []);
            string described = SurfaceAgreementCheck.Describe("subdivision 1", agreement);

            run.Equal(agreement.Sampled, 0, "nothing was measured");
            run.Contains(described, "nothing to measure", "said rather than implied");
        });

        run.Case("agreement within tolerance is described as lying on the ground", () =>
        {
            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                FlatVertices, FlatTriangles, [new SurfacePoint(5.0, 5.0, 0.0)]);

            string described = SurfaceAgreementCheck.Describe("subdivision 1", agreement);

            run.Contains(described, "subdivision 1", "which surface this is about");
            run.Contains(described, "lies on", "the reading, not just the number");
        });

        run.Case("a deviating surface is described as a different surface", () =>
        {
            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                FlatVertices, FlatTriangles, [new SurfacePoint(5.0, 5.0, 0.4)]);

            string described = SurfaceAgreementCheck.Describe("subdivision 1", agreement);

            run.Contains(described, "does not lie on", "the opposite reading is said as plainly");
            run.Contains(described, "0.4", "with the number that settles it");
        });

        run.Case("samples off the footprint are named in the sentence", () =>
        {
            SurfaceAgreement agreement = SurfaceAgreementCheck.Compare(
                FlatVertices,
                FlatTriangles,
                [new SurfacePoint(5.0, 5.0, 0.0), new SurfacePoint(50.0, 50.0, 0.0)]);

            run.Contains(SurfaceAgreementCheck.Describe("subdivision 1", agreement), "outside",
                "a subdivision reaching past its ground is worth a clause of its own");
        });

        return run.Report("surface agreement");
    }
}
