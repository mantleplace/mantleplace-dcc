using System.Globalization;

namespace MantlePlace.Core;

public readonly record struct SurfaceAgreement(
    int Sampled,
    int OffFootprint,
    double MaxAbsDeltaM,
    double MeanAbsDeltaM);

public readonly record struct TessellationNoiseFloor(
    int Sampled,
    int OffFootprint,
    int Coincident,
    double FloorM)
{
    public static TessellationNoiseFloor Unmeasured => default;

    public bool CanClassify => Sampled > Coincident;
}

public static class SurfaceAgreementCheck
{
    public const double AgreementToleranceM = 0.001;

    private const double InsideToleranceBarycentric = 1e-9;

    private const double CoincidentPlanToleranceM = 1e-6;

    private const double DegenerateArea2 = 1e-12;

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

    private static HashSet<(long X, long Y)> PlanCells(IReadOnlyList<SurfacePoint> vertices)
    {
        HashSet<(long X, long Y)> occupied = [];
        foreach (SurfacePoint vertex in vertices)
        {
            occupied.Add(Cell(vertex));
        }

        return occupied;
    }

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
