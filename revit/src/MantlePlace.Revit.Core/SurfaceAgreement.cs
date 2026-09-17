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
    /// <para>
    /// One millimetre. The surfaces under test are built by Revit from one point set, so agreement
    /// should be exact — this is not a judgement about how much disagreement is acceptable, and it
    /// is deliberately far below the contour interval anybody would draw.
    /// </para>
    /// <para>
    /// ⛔ <b>It does not absorb chord error, and a caller that tessellates the two surfaces
    /// independently can exceed it without the surfaces differing.</b> Each surface is sampled as
    /// flat triangles; a sample vertex from one is interpolated across the other's chords, and on
    /// sloped ground two different triangulations of the same shape disagree by the chord height
    /// between them. That is a property of the tessellation, not of the geometry, and it is exactly
    /// the false "they differ" verdict this type exists to avoid producing. A caller must tessellate
    /// both surfaces at the finest level of detail available to it, and a reading between a
    /// millimetre and the chord height of its own mesh is inconclusive rather than a defect.
    /// </para>
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
    /// <para>
    /// Triangles that name a vertex outside the list, and triangles with no area in plan, are
    /// skipped rather than thrown on: the shim feeds this from Revit's own tessellation, and a probe
    /// that throws hands its reader nothing at all.
    /// </para>
    /// <para>
    /// ⛔ The reference triangles are indexed in plan before anything is measured. A linear scan
    /// would be samples × triangles, and the caller tessellates at the finest level of detail it can
    /// get precisely so that the triangle count is <em>large</em> — the two pull in opposite
    /// directions, and the version of this that scanned was sized against a triangle count nobody
    /// had measured. An index removes the guess rather than documenting it.
    /// </para>
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

        PlanIndex index = PlanIndex.Over(referenceVertices, referenceTriangles);

        foreach (SurfacePoint sample in samples)
        {
            if (index.TryHeightAt(referenceVertices, referenceTriangles, sample.X, sample.Y, out double ground))
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
    /// At most <paramref name="cap"/> of <paramref name="source"/>, spread evenly across it and
    /// always including its first and last item.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Compare"/> is a linear scan over the reference triangles, so the cost of a run is
    /// samples × triangles and an uncapped comparison against a terrain mesh is minutes. A probe
    /// nobody waits for answers nothing.
    /// </para>
    /// <para>
    /// ⛔ Strided, not the first <paramref name="cap"/>. A surface's vertices arrive in tessellation
    /// order, which is not random: taking a prefix samples one part of it, and on a subdivision that
    /// is the part nearest its boundary — exactly where a boundary artifact would mask a
    /// disagreement in the middle. The endpoints are included for the same reason, because a surface
    /// that reaches past the one it is measured against does so at its extremes.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SurfacePoint> Sample(IReadOnlyList<SurfacePoint> source, int cap)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 2);

        if (source.Count <= cap)
        {
            return source;
        }

        List<SurfacePoint> taken = new(cap);
        for (int i = 0; i < cap; i++)
        {
            taken.Add(source[(int)((long)i * (source.Count - 1) / (cap - 1))]);
        }

        return taken;
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
            ? $"lies on the reference surface at every point measured (worst deviation "
                + $"{agreement.MaxAbsDeltaM.ToString("0.###", CultureInfo.InvariantCulture)} m over "
                + $"{agreement.Sampled:N0} point(s)), so a contour mismatch here is drawing, not geometry"
            : $"does not lie on the reference surface — worst deviation "
                + $"{agreement.MaxAbsDeltaM.ToString("0.###", CultureInfo.InvariantCulture)} m, mean "
                + $"{agreement.MeanAbsDeltaM.ToString("0.###", CultureInfo.InvariantCulture)} m over "
                + $"{agreement.Sampled:N0} point(s). ⚠ Inconclusive below the chord height of the two "
                + "meshes compared: each was tessellated on its own, so a deviation smaller than that "
                + "is the tessellation disagreeing and not the geometry";

        string outside = agreement.OffFootprint == 0
            ? string.Empty
            : $" {agreement.OffFootprint:N0} further point(s) fell outside its footprint entirely.";

        return $"{subject}: {verdict}.{outside}";
    }

    /// <summary>
    /// The reference triangles bucketed by their plan footprint, so a sample only tests the few that
    /// could possibly cover it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A uniform grid, sized so the average cell holds a handful of triangles. A triangle goes in
    /// every cell its plan bounding box touches, which over-counts slightly and never under-counts —
    /// the failure this must not have is a sample reported as off the footprint because the triangle
    /// covering it was filed somewhere else.
    /// </para>
    /// <para>
    /// ⛔ It changes cost, never answers. <see cref="TryHeightAt"/> still tests candidates with the
    /// same arithmetic and the same tolerances a scan used; the grid only decides which candidates
    /// are worth testing. The tests assert agreement results, not the index, and a mesh larger than
    /// one cell is among them for that reason.
    /// </para>
    /// </remarks>
    private sealed class PlanIndex
    {
        private const int MaxCellsPerAxis = 256;
        private const double TargetTrianglesPerCell = 4.0;

        private readonly List<int>[] _cells;
        private readonly int _columns;
        private readonly int _rows;
        private readonly double _minX;
        private readonly double _minY;
        private readonly double _cellWidth;
        private readonly double _cellHeight;

        private PlanIndex(
            List<int>[] cells, int columns, int rows,
            double minX, double minY, double cellWidth, double cellHeight)
        {
            _cells = cells;
            _columns = columns;
            _rows = rows;
            _minX = minX;
            _minY = minY;
            _cellWidth = cellWidth;
            _cellHeight = cellHeight;
        }

        internal static PlanIndex Over(
            IReadOnlyList<SurfacePoint> vertices, IReadOnlyList<SurfaceTriangle> triangles)
        {
            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;
            int usable = 0;

            foreach (SurfaceTriangle triangle in triangles)
            {
                if (!TryCorners(vertices, triangle, out SurfacePoint a, out SurfacePoint b, out SurfacePoint c))
                {
                    continue;
                }

                usable++;
                minX = Math.Min(minX, Math.Min(a.X, Math.Min(b.X, c.X)));
                minY = Math.Min(minY, Math.Min(a.Y, Math.Min(b.Y, c.Y)));
                maxX = Math.Max(maxX, Math.Max(a.X, Math.Max(b.X, c.X)));
                maxY = Math.Max(maxY, Math.Max(a.Y, Math.Max(b.Y, c.Y)));
            }

            if (usable == 0 || maxX <= minX || maxY <= minY)
            {
                // Nothing to index, or a reference surface with no extent in plan. One cell holding
                // everything degrades to the scan this replaced, which is correct and merely slow —
                // and on a degenerate surface there is nothing to be slow about.
                return OneCell(triangles);
            }

            int perAxis = (int)Math.Ceiling(Math.Sqrt(usable / TargetTrianglesPerCell));
            perAxis = Math.Clamp(perAxis, 1, MaxCellsPerAxis);

            List<int>[] cells = new List<int>[perAxis * perAxis];
            double cellWidth = (maxX - minX) / perAxis;
            double cellHeight = (maxY - minY) / perAxis;

            PlanIndex index = new(cells, perAxis, perAxis, minX, minY, cellWidth, cellHeight);

            for (int i = 0; i < triangles.Count; i++)
            {
                if (!TryCorners(vertices, triangles[i], out SurfacePoint a, out SurfacePoint b, out SurfacePoint c))
                {
                    continue;
                }

                int left = index.Column(Math.Min(a.X, Math.Min(b.X, c.X)));
                int right = index.Column(Math.Max(a.X, Math.Max(b.X, c.X)));
                int bottom = index.Row(Math.Min(a.Y, Math.Min(b.Y, c.Y)));
                int top = index.Row(Math.Max(a.Y, Math.Max(b.Y, c.Y)));

                for (int column = left; column <= right; column++)
                {
                    for (int row = bottom; row <= top; row++)
                    {
                        (cells[(row * perAxis) + column] ??= []).Add(i);
                    }
                }
            }

            return index;
        }

        private static PlanIndex OneCell(IReadOnlyList<SurfaceTriangle> triangles)
        {
            List<int> everything = new(triangles.Count);
            for (int i = 0; i < triangles.Count; i++)
            {
                everything.Add(i);
            }

            return new PlanIndex([everything], 1, 1, 0.0, 0.0, 1.0, 1.0);
        }

        private int Column(double x) => _columns == 1
            ? 0
            : Math.Clamp((int)((x - _minX) / _cellWidth), 0, _columns - 1);

        private int Row(double y) => _rows == 1
            ? 0
            : Math.Clamp((int)((y - _minY) / _cellHeight), 0, _rows - 1);

        internal bool TryHeightAt(
            IReadOnlyList<SurfacePoint> vertices,
            IReadOnlyList<SurfaceTriangle> triangles,
            double x,
            double y,
            out double height)
            => SurfaceAgreementCheck.TryHeightAt(
                vertices, triangles, _cells[(Row(y) * _columns) + Column(x)], x, y, out height);
    }

    /// <summary>
    /// The reference surface's height under one plan position, or <c>false</c> when nothing covers it.
    /// </summary>
    /// <remarks>
    /// <paramref name="candidates"/> is the index's shortlist of triangle indices. The arithmetic is
    /// the same one a full scan used; only the set it runs over is smaller.
    /// </remarks>
    private static bool TryHeightAt(
        IReadOnlyList<SurfacePoint> vertices,
        IReadOnlyList<SurfaceTriangle> triangles,
        List<int>? candidates,
        double x,
        double y,
        out double height)
    {
        height = 0.0;

        if (candidates is null)
        {
            return false;
        }

        foreach (int candidate in candidates)
        {
            SurfaceTriangle triangle = triangles[candidate];
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
