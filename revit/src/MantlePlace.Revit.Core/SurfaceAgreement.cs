using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// How far one surface's points sit above or below another surface, measured vertically.
/// </summary>
/// <param name="Sampled">How many sample points landed on the reference surface and were measured.</param>
/// <param name="OffFootprint">
/// How many did not — outside its footprint, or over a part of it too degenerate to interpolate
/// across. Counted rather than discarded, because "the subdivision reaches past the ground it was
/// cut from" is a finding and a silently dropped sample is not.
/// </param>
/// <param name="MaxAbsDeltaM">The worst absolute vertical deviation, in metres.</param>
/// <param name="MeanAbsDeltaM">The mean absolute vertical deviation, in metres.</param>
public readonly record struct SurfaceAgreement(
    int Sampled,
    int OffFootprint,
    double MaxAbsDeltaM,
    double MeanAbsDeltaM);

/// <summary>
/// Whether one surface lies on another — the arithmetic behind "do these two disagree, or do they
/// only <em>draw</em> differently".
/// </summary>
/// <remarks>
/// <para>
/// A Revit site-boundary subdivision is itself a <c>Toposolid</c>, cut into the terrain and carrying
/// its own contour lines. On a site with 23 of them those contours visibly fail to line up with the
/// continuous ground beneath, and two very different explanations fit that picture:
/// </para>
/// <list type="bullet">
/// <item><description>
/// the subdivisions lie exactly on the ground and Revit generates contours per element, so the lines
/// disagree while the surfaces do not — a limitation to document, not a defect to fix;
/// </description></item>
/// <item><description>
/// the subdivisions are genuinely different surfaces, in which case something is wrong and the
/// contours are the symptom rather than the fault.
/// </description></item>
/// </list>
/// <para>
/// ⛔ <b>Those call for opposite responses, and nothing short of a number tells them apart.</b> This
/// type exists so the number is produced by pure code a headless test drives (<c>HPS-02</c>), rather
/// than by a paragraph in the shim asserting what Revit "must" be doing. The immediate cause is a
/// comment in this tree that claimed smooth shading was incompatible with the drape, stayed true-
/// sounding for two weeks after it stopped being true, and cost a session.
/// </para>
/// <para>
/// This derives nothing the manifest publishes. It compares two surfaces Revit already built and
/// reports how far apart they are — an observation about the document, not a placement value.
/// </para>
/// </remarks>
public static class SurfaceAgreementCheck
{
    /// <summary>
    /// The deviation below which two surfaces are called the same surface, in metres.
    /// </summary>
    /// <remarks>
    /// One millimetre. The surfaces under test are built by Revit from one point set, so agreement
    /// should be exact and the tolerance is here only to absorb tessellation round-off — it is not a
    /// judgement about how much disagreement is acceptable, and it is deliberately far below the
    /// contour interval anybody would draw.
    /// </remarks>
    public const double AgreementToleranceM = 0.001;

    /// <summary>
    /// A point is treated as inside a triangle when its barycentric coordinates clear this much of
    /// the wrong side of an edge.
    /// </summary>
    /// <remarks>
    /// ⛔ Not zero. The samples come from the same point set the reference surface was built from,
    /// so a large share of them land exactly on a shared edge or a vertex. A strict test drops those
    /// and the probe then reports a surface as absent from ground it sits precisely on — the failure
    /// would look like the defect being hunted.
    /// </remarks>
    private const double InsideToleranceBarycentric = 1e-9;

    /// <summary>Twice the plan area below which a triangle is treated as having no interior, in m².</summary>
    /// <remarks>
    /// Far below any triangle a terrain mesh contains, and well above the round-off that makes an
    /// exactly-collinear triple compute as a tiny non-zero rather than as zero. Testing against
    /// <c>double.Epsilon</c> would catch only the exact case and let a sliver through, where the
    /// division that follows turns round-off into a barycentric coordinate of any size at all.
    /// </remarks>
    private const double DegenerateArea2 = 1e-12;

    /// <summary>
    /// Measures every sample vertically against the surface described by
    /// <paramref name="referenceVertices"/> and <paramref name="referenceTriangles"/>.
    /// </summary>
    /// <remarks>
    /// Triangles that name a vertex outside the list, and triangles with no area in plan, are
    /// skipped rather than thrown on: the shim feeds this from Revit's own tessellation, and a probe
    /// that throws hands its reader nothing at all.
    /// </remarks>
    public static SurfaceAgreement Compare(
        IReadOnlyList<SurfacePoint> referenceVertices,
        IReadOnlyList<SurfaceTriangle> referenceTriangles,
        IReadOnlyList<SurfacePoint> samples)
    {
        ArgumentNullException.ThrowIfNull(referenceVertices);
        ArgumentNullException.ThrowIfNull(referenceTriangles);
        ArgumentNullException.ThrowIfNull(samples);

        int sampled = 0;
        int offFootprint = 0;
        double max = 0.0;
        double total = 0.0;

        foreach (SurfacePoint sample in samples)
        {
            if (TryHeightAt(referenceVertices, referenceTriangles, sample.X, sample.Y, out double ground))
            {
                double delta = Math.Abs(sample.Z - ground);
                sampled++;
                total += delta;
                max = Math.Max(max, delta);
            }
            else
            {
                offFootprint++;
            }
        }

        return new SurfaceAgreement(
            sampled,
            offFootprint,
            max,
            sampled == 0 ? 0.0 : total / sampled);
    }

    /// <summary>
    /// The reading a person needs from <see cref="Compare"/>, as one sentence.
    /// </summary>
    /// <remarks>
    /// The verdict is stated, not left to be inferred from the numbers. A reader looking at a
    /// screenshot of mismatched contours is deciding whether to file a bug, and "0.000 m" only
    /// answers that if somebody says what it means.
    /// </remarks>
    public static string Describe(string subject, SurfaceAgreement agreement)
    {
        if (agreement.Sampled == 0)
        {
            return agreement.OffFootprint == 0
                ? $"{subject}: nothing to measure — no sample points."
                : $"{subject}: nothing to measure — all {agreement.OffFootprint:N0} sample(s) fell "
                    + "outside the reference surface's footprint.";
        }

        string verdict = agreement.MaxAbsDeltaM <= AgreementToleranceM
            ? $"lies on the reference surface (worst deviation "
                + $"{agreement.MaxAbsDeltaM.ToString("0.###", CultureInfo.InvariantCulture)} m over "
                + $"{agreement.Sampled:N0} point(s)), so any contour mismatch is drawing, not geometry"
            : $"does not lie on the reference surface — worst deviation "
                + $"{agreement.MaxAbsDeltaM.ToString("0.###", CultureInfo.InvariantCulture)} m, mean "
                + $"{agreement.MeanAbsDeltaM.ToString("0.###", CultureInfo.InvariantCulture)} m over "
                + $"{agreement.Sampled:N0} point(s)";

        string outside = agreement.OffFootprint == 0
            ? string.Empty
            : $" {agreement.OffFootprint:N0} further point(s) fell outside its footprint entirely.";

        return $"{subject}: {verdict}.{outside}";
    }

    /// <summary>
    /// The reference surface's height under one plan position, or <c>false</c> when nothing covers it.
    /// </summary>
    /// <remarks>
    /// A linear scan. The caller is a probe run by hand on one document, and an index would be the
    /// third thing in this file that has to be right for the number to mean anything. If a site ever
    /// makes this too slow, the fix is to sample fewer points, not to make the arithmetic harder to
    /// check.
    /// </remarks>
    private static bool TryHeightAt(
        IReadOnlyList<SurfacePoint> vertices,
        IReadOnlyList<SurfaceTriangle> triangles,
        double x,
        double y,
        out double height)
    {
        height = 0.0;

        foreach (SurfaceTriangle triangle in triangles)
        {
            if (!TryCorners(vertices, triangle, out SurfacePoint a, out SurfacePoint b, out SurfacePoint c))
            {
                continue;
            }

            double area2 = ((b.X - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (b.Y - a.Y));
            if (Math.Abs(area2) <= DegenerateArea2)
            {
                // No area in plan: a sliver seen edge-on, or three collinear points. There is no
                // interior to interpolate across, and dividing by it would produce an infinity that
                // reads as a catastrophic deviation.
                continue;
            }

            double u = (((b.X - x) * (c.Y - y)) - ((c.X - x) * (b.Y - y))) / area2;
            double v = (((c.X - x) * (a.Y - y)) - ((a.X - x) * (c.Y - y))) / area2;
            double w = 1.0 - u - v;

            if (u < -InsideToleranceBarycentric
                || v < -InsideToleranceBarycentric
                || w < -InsideToleranceBarycentric)
            {
                continue;
            }

            height = (u * a.Z) + (v * b.Z) + (w * c.Z);
            return true;
        }

        return false;
    }

    private static bool TryCorners(
        IReadOnlyList<SurfacePoint> vertices,
        SurfaceTriangle triangle,
        out SurfacePoint a,
        out SurfacePoint b,
        out SurfacePoint c)
    {
        a = default;
        b = default;
        c = default;

        if (triangle.A < 0 || triangle.A >= vertices.Count
            || triangle.B < 0 || triangle.B >= vertices.Count
            || triangle.C < 0 || triangle.C >= vertices.Count)
        {
            return false;
        }

        a = vertices[triangle.A];
        b = vertices[triangle.B];
        c = vertices[triangle.C];
        return true;
    }
}
