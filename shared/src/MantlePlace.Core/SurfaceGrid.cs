using System.Globalization;

namespace MantlePlace.Core;

public readonly record struct SurfaceGridShape(int ColumnCount, int RowCount, double Spacing, double MinX, double MinY);

public static class SurfaceGrid
{
    public const int MaxEdgeLines = 3;

    public const double IdenticalFraction = 0.9;

    public const double SuspiciousOffset = 10.0;

    public const double MaxDroppedFraction = 0.02;

    public static SurfaceGridShape? Detect(IReadOnlyList<SurfacePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count < 9)
        {
            return null;
        }

        SortedSet<double> xs = [];
        SortedSet<double> ys = [];
        foreach (SurfacePoint point in points)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                return null;
            }

            xs.Add(point.X);
            ys.Add(point.Y);
        }

        if (xs.Count < 3 || ys.Count < 3 || (long)xs.Count * ys.Count != points.Count)
        {
            return null;
        }

        if (!TryUniformStep(xs, out double stepX) || !TryUniformStep(ys, out double stepY))
        {
            return null;
        }

        if (Math.Abs(stepX - stepY) > stepX * 1e-6)
        {
            return null;
        }

        return new SurfaceGridShape(xs.Count, ys.Count, stepX, xs.Min, ys.Min);
    }

    public static FilledEdges DetectFilledEdges(IReadOnlyList<SurfacePoint> points, out string? report)
    {
        ArgumentNullException.ThrowIfNull(points);
        report = null;

        if (Detect(points) is not { } shape)
        {
            return default;
        }

        Dictionary<double, List<double>> byColumn = [];
        Dictionary<double, List<double>> byRow = [];
        foreach (SurfacePoint point in points)
        {
            Append(byColumn, point.X, point.Z);
            Append(byRow, point.Y, point.Z);
        }

        double[] columnKeys = [.. byColumn.Keys.Order()];
        double[] rowKeys = [.. byRow.Keys.Order()];

        HashSet<double> columns = ScanEdges(columnKeys, byColumn);
        HashSet<double> rows = ScanEdges(rowKeys, byRow);

        if (columns.Count == 0 && rows.Count == 0)
        {
            return default;
        }

        int dropped = CountDropped(points, columns, rows);
        double fraction = (double)dropped / points.Count;
        if (fraction > MaxDroppedFraction)
        {
            report = string.Format(
                CultureInfo.InvariantCulture,
                "{0} of {1} terrain points ({2:0.#}%) look like a producer's fill value rather than "
                + "ground, which is more than this plugin is willing to remove on its own. They were "
                + "kept — expect a wall or a spike at the edge of the terrain, and report the order.",
                dropped,
                points.Count,
                fraction * 100.0);
            return default;
        }

        return new FilledEdges(columns, rows, dropped);
    }

    public readonly record struct FilledEdges(
        IReadOnlySet<double>? Columns,
        IReadOnlySet<double>? Rows,
        int PointCount)
    {
        public bool Covers(SurfacePoint point)
            => (Columns?.Contains(point.X) ?? false) || (Rows?.Contains(point.Y) ?? false);

        public bool Any => PointCount > 0;
    }

    private static HashSet<double> ScanEdges(double[] keys, Dictionary<double, List<double>> lines)
    {
        HashSet<double> filled = [];
        ScanFromEnd(keys, lines, start: 0, inward: +1, filled);
        ScanFromEnd(keys, lines, start: keys.Length - 1, inward: -1, filled);
        return filled;
    }

    private static void ScanFromEnd(
        double[] keys,
        Dictionary<double, List<double>> lines,
        int start,
        int inward,
        HashSet<double> filled)
    {
        List<(double Key, double Fill)> run = [];
        int index = start;

        while (run.Count < MaxEdgeLines
            && index >= 0
            && index < keys.Length
            && TryFillValue(lines[keys[index]], out double fill))
        {
            run.Add((keys[index], fill));
            index += inward;
        }

        if (run.Count == 0 || index < 0 || index >= keys.Length)
        {
            return;
        }

        List<double> reference = lines[keys[index]];
        foreach ((double key, double fill) in run)
        {
            if (MedianAbsoluteOffset(reference, fill) > SuspiciousOffset)
            {
                filled.Add(key);
            }
        }
    }

    private static bool TryFillValue(List<double> zs, out double fill)
    {
        fill = 0.0;
        if (zs.Count == 0)
        {
            return false;
        }

        Dictionary<double, int> counts = [];
        foreach (double z in zs)
        {
            counts[z] = counts.TryGetValue(z, out int seen) ? seen + 1 : 1;
        }

        int best = 0;
        foreach (KeyValuePair<double, int> pair in counts)
        {
            if (pair.Value > best)
            {
                best = pair.Value;
                fill = pair.Key;
            }
        }

        return best >= zs.Count * IdenticalFraction;
    }

    internal static double MedianAbsoluteOffset(IReadOnlyList<double> zs, double fill)
    {
        if (zs.Count == 0)
        {
            return 0.0;
        }

        double[] offsets = new double[zs.Count];
        for (int i = 0; i < zs.Count; i++)
        {
            offsets[i] = Math.Abs(zs[i] - fill);
        }

        Array.Sort(offsets);
        return offsets[offsets.Length / 2];
    }

    private static int CountDropped(
        IReadOnlyList<SurfacePoint> points,
        HashSet<double> columns,
        HashSet<double> rows)
    {
        int dropped = 0;
        foreach (SurfacePoint point in points)
        {
            if (columns.Contains(point.X) || rows.Contains(point.Y))
            {
                dropped++;
            }
        }

        return dropped;
    }

    private static void Append(Dictionary<double, List<double>> lines, double key, double z)
    {
        if (!lines.TryGetValue(key, out List<double>? bucket))
        {
            bucket = [];
            lines[key] = bucket;
        }

        bucket.Add(z);
    }

    private static bool TryUniformStep(SortedSet<double> values, out double step)
    {
        step = 0.0;
        double? previous = null;
        foreach (double value in values)
        {
            if (previous is { } last)
            {
                double gap = value - last;
                if (step == 0.0)
                {
                    step = gap;
                }
                else if (Math.Abs(gap - step) > step * 1e-6)
                {
                    return false;
                }
            }

            previous = value;
        }

        return step > 0.0;
    }
}
