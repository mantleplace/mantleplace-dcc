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
/// How far a mesh's own chords fall from the surface it approximates — the deviation a reading has
/// to beat before it says anything about geometry.
/// </summary>
/// <remarks>
/// Two surfaces tessellated independently disagree by the chord height between their triangulations
/// even when the surfaces are identical, and until that is a number the caveat saying so is
/// unusable: a reader holding 0.04 m cannot tell which side of it they are on. It is measured, not
/// assumed — see <see cref="SurfaceAgreementCheck.MeasureNoiseFloor"/> for how.
/// </remarks>
/// <param name="Sampled">How many control samples were measured against the reference mesh.</param>
/// <param name="OffFootprint">
/// How many did not, because nothing on the reference mesh covered them in plan. A floor taken from
/// a control that mostly missed is a floor over the part of the surface that was hit, and a reader
/// deciding what a deviation means needs to know that before it is quoted at them.
/// </param>
/// <param name="Coincident">
/// How many of those landed on a vertex of that mesh, where interpolation is exact by construction
/// and no chord error can show. Counted because a control made entirely of them reads 0.000 m while
/// having measured nothing — identical to a floor that is genuinely zero, and it must not be
/// mistaken for one.
/// </param>
/// <param name="FloorM">The worst chord deviation seen, in metres.</param>
public readonly record struct TessellationNoiseFloor(
    int Sampled,
    int OffFootprint,
    int Coincident,
    double FloorM)
{
    /// <summary>No control was taken. Every non-zero reading measured against this is unclassified.</summary>
    public static TessellationNoiseFloor Unmeasured => default;

    /// <summary>
    /// Whether this floor can classify a deviation: at least one control sample that was measured
    /// and was not itself a vertex of the mesh it was measured against.
    /// </summary>
    public bool CanClassify => Sampled > Coincident;
}

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

    /// <summary>
    /// How close two plan positions have to be to count as the same position, in metres.
    /// </summary>
    /// <remarks>
    /// A micron. The control's coarse vertices come off the same face as the fine mesh's, so the
    /// shared ones are shared exactly, and this only absorbs the round-off of converting out of
    /// Revit's internal units twice.
    /// </remarks>
    private const double CoincidentPlanToleranceM = 1e-6;

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
        double max = 0.0;
        double total = 0.0;

        int offFootprint = MeasureEach(
            referenceVertices,
            referenceTriangles,
            samples,
            (sample, ground) =>
            {
                double delta = Math.Abs(sample.Z - ground);
                sampled++;
                total += delta;
                max = Math.Max(max, delta);
            });

        return new SurfaceAgreement(
            sampled,
            offFootprint,
            max,
            sampled == 0 ? 0.0 : total / sampled);
    }

    /// <summary>
    /// Runs <paramref name="onMeasured"/> for every sample the reference surface covers, and returns
    /// how many it did not cover.
    /// </summary>
    /// <remarks>
    /// ⛔ One traversal for both readings. <see cref="Compare"/> and <see cref="MeasureNoiseFloor"/>
    /// have to agree exactly on which samples count as measured — the floor is the yardstick the
    /// agreement is judged against, and a footprint test that drifted between them would compare a
    /// deviation over one set of points to a floor over another. Sharing the walk makes that
    /// impossible rather than merely unlikely.
    /// </remarks>
    private static int MeasureEach(
        IReadOnlyList<SurfacePoint> referenceVertices,
        IReadOnlyList<SurfaceTriangle> referenceTriangles,
        IReadOnlyList<SurfacePoint> samples,
        Action<SurfacePoint, double> onMeasured)
    {
        PlanIndex index = PlanIndex.Over(referenceVertices, referenceTriangles);
        int offFootprint = 0;

        foreach (SurfacePoint sample in samples)
        {
            if (index.TryHeightAt(referenceVertices, referenceTriangles, sample.X, sample.Y, out double height))
            {
                onMeasured(sample, height);
            }
            else
            {
                offFootprint++;
            }
        }

        return offFootprint;
    }

    /// <summary>
    /// At most <paramref name="cap"/> of <paramref name="source"/>, spread evenly across it and
    /// always including its first and last item.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ The cap bounds what a verdict <em>claims to have checked</em>, not what a run costs:
    /// <see cref="Compare"/> indexes the reference triangles in plan, so a run does not track their
    /// count. A verdict from some of a surface's vertices is a verdict about those vertices and no
    /// others, which is why the caller prints the two counts side by side rather than the verdict
    /// alone. The caller also chooses the number, and says there why it chose it.
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
    /// The chord error of the surface described by <paramref name="referenceVertices"/> and
    /// <paramref name="referenceTriangles"/>, measured at <paramref name="controlSamples"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The control is one mesh against a coarser tessellation of <em>itself</em>. Both sets of points
    /// lie on one true face, so the true deviation between them is zero and whatever comes out is
    /// that mesh's own chords falling away from the surface — the noise floor, measured on the
    /// document being probed rather than guessed at from triangle counts.
    /// </para>
    /// <para>
    /// ⛔ The coarse level only decides <em>where</em> the floor is sampled, never how large it
    /// reads. What it must not do is sample only the fine mesh's own vertices, which interpolate
    /// exactly and would report a floor of zero from a control that measured no chord at all;
    /// <see cref="TessellationNoiseFloor.Coincident"/> is how a reader tells those apart.
    /// </para>
    /// </remarks>
    public static TessellationNoiseFloor MeasureNoiseFloor(
        IReadOnlyList<SurfacePoint> referenceVertices,
        IReadOnlyList<SurfaceTriangle> referenceTriangles,
        IReadOnlyList<SurfacePoint> controlSamples)
    {
        ArgumentNullException.ThrowIfNull(referenceVertices);
        ArgumentNullException.ThrowIfNull(referenceTriangles);
        ArgumentNullException.ThrowIfNull(controlSamples);

        HashSet<(long X, long Y)> vertexCells = PlanCells(referenceVertices);

        int sampled = 0;
        int coincident = 0;
        double floor = 0.0;

        // ⛔ Coincidence is counted over the samples that were MEASURED, never over the samples that
        // were offered. The plan match is a micron wide and the footprint test is not, so a control
        // vertex a hair outside the mesh can be coincident with one of its vertices and still not be
        // measured; counting those would let Coincident reach Sampled, take CanClassify false, and
        // throw away a floor that was there — which prints every subdivision as unclassified and
        // leaves the question this probe exists to settle unanswered.
        int offFootprint = MeasureEach(
            referenceVertices,
            referenceTriangles,
            controlSamples,
            (sample, height) =>
            {
                sampled++;
                floor = Math.Max(floor, Math.Abs(sample.Z - height));
                if (Occupies(vertexCells, Cell(sample)))
                {
                    coincident++;
                }
            });

        return new TessellationNoiseFloor(sampled, offFootprint, coincident, floor);
    }

    /// <summary>The floor, as the one line that belongs above a set of verdicts.</summary>
    public static string DescribeNoiseFloor(TessellationNoiseFloor floor)
    {
        if (floor.Sampled == 0)
        {
            return "tessellation noise floor: not measured — no control sample reached the reference "
                + "surface, so every reading below is unclassified.";
        }

        if (!floor.CanClassify)
        {
            return $"tessellation noise floor: not measured — all {floor.Sampled:N0} control sample(s) "
                + "landed on a vertex of the reference mesh, where interpolation is exact by "
                + "construction and no chord error can show. Every reading below is unclassified.";
        }

        string missed = floor.OffFootprint == 0
            ? string.Empty
            : $" {floor.OffFootprint:N0} further control sample(s) fell outside its footprint, so this "
                + "is the floor over the part of the surface that was hit.";

        return $"tessellation noise floor: {Metres(floor.FloorM)} m, from {floor.Sampled:N0} control "
            + $"sample(s), {floor.Coincident:N0} of them on a vertex of the reference mesh. A "
            + $"deviation at or below this is the two tessellations disagreeing, not the surfaces.{missed}";
    }

    /// <summary>
    /// The reading a person needs from <see cref="Compare"/>, as one sentence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The verdict is stated, not left to be inferred from the numbers. A reader looking at a
    /// screenshot of mismatched contours is deciding whether to file a bug, and "0.000 m" only
    /// answers that if somebody says what it means.
    /// </para>
    /// <para>
    /// ⛔ Three readings, not two, and the precedence matters. <see cref="AgreementToleranceM"/>
    /// is tested first and wins outright: a surface agreeing to a millimetre has agreed however
    /// coarsely its neighbour's chords are drawn, so a large <paramref name="floor"/> never takes an
    /// agreement away. The floor bounds only what a <em>disagreement</em> must clear. A run with no
    /// floor produces neither verdict — it says the reading is unclassified, because "differs by more
    /// than a number nobody measured" is the sentence this type exists to stop being written.
    /// </para>
    /// </remarks>
    public static string Describe(string subject, SurfaceAgreement agreement, TessellationNoiseFloor floor)
    {
        if (agreement.Sampled == 0)
        {
            return agreement.OffFootprint == 0
                ? $"{subject}: nothing to measure — no sample points."
                : $"{subject}: nothing to measure — all {agreement.OffFootprint:N0} sample(s) fell "
                    + "outside the reference surface's footprint.";
        }

        string verdict;
        if (agreement.MaxAbsDeltaM <= AgreementToleranceM)
        {
            verdict = "lies on the reference surface at every point measured (worst deviation "
                + $"{Metres(agreement.MaxAbsDeltaM)} m over {agreement.Sampled:N0} point(s)), so a "
                + "contour mismatch here is drawing, not geometry";
        }
        else if (!floor.CanClassify)
        {
            // ⛔ The reading, then the refusal to read it — and no clause before either that names
            // one of the two verdicts. "Deviates from the reference surface" is the disagreement
            // said in other words, and a reader who stops at the first comma has been told the thing
            // this branch exists to withhold.
            verdict = "⚠ Unclassified — worst deviation "
                + $"{Metres(agreement.MaxAbsDeltaM)} m, mean {Metres(agreement.MeanAbsDeltaM)} m over "
                + $"{agreement.Sampled:N0} point(s), and no tessellation noise floor was measured for "
                + "this reference surface. Whether that is geometry or chord error is not something "
                + "this run can say";
        }
        else if (agreement.MaxAbsDeltaM <= floor.FloorM)
        {
            verdict = "deviates by no more than this mesh's own chords — worst deviation "
                + $"{Metres(agreement.MaxAbsDeltaM)} m against a tessellation noise floor of "
                + $"{Metres(floor.FloorM)} m, over {agreement.Sampled:N0} point(s). ⚠ "
                + "Inconclusive: a deviation this small is the two tessellations disagreeing, not "
                + "the surfaces";
        }
        else
        {
            verdict = "does not lie on the reference surface — worst deviation "
                + $"{Metres(agreement.MaxAbsDeltaM)} m, mean {Metres(agreement.MeanAbsDeltaM)} m over "
                + $"{agreement.Sampled:N0} point(s), against a tessellation noise floor of "
                + $"{Metres(floor.FloorM)} m";
        }

        string outside = agreement.OffFootprint == 0
            ? string.Empty
            : $" {agreement.OffFootprint:N0} further point(s) fell outside its footprint entirely.";

        return $"{subject}: {verdict}.{outside}";
    }

    private static string Metres(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Every vertex's plan position, bucketed at <see cref="CoincidentPlanToleranceM"/>.</summary>
    private static HashSet<(long X, long Y)> PlanCells(IReadOnlyList<SurfacePoint> vertices)
    {
        HashSet<(long X, long Y)> occupied = [];
        foreach (SurfacePoint vertex in vertices)
        {
            occupied.Add(Cell(vertex));
        }

        return occupied;
    }

    /// <summary>
    /// Whether one plan position sits on an occupied one.
    /// </summary>
    /// <remarks>
    /// The nine cells around it, not the one it landed in: a pair either side of a bucket boundary
    /// is a micron apart and the same point.
    /// </remarks>
    private static bool Occupies(HashSet<(long X, long Y)> occupied, (long X, long Y) cell)
        => Occupies(occupied, cell.X, cell.Y);

    private static bool Occupies(HashSet<(long X, long Y)> occupied, long x, long y)
    {
        for (long dx = -1; dx <= 1; dx++)
        {
            for (long dy = -1; dy <= 1; dy++)
            {
                if (occupied.Contains((x + dx, y + dy)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static (long X, long Y) Cell(SurfacePoint point) => (
        (long)Math.Round(point.X / CoincidentPlanToleranceM),
        (long)Math.Round(point.Y / CoincidentPlanToleranceM));

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
