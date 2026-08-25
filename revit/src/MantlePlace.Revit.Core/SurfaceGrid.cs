using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>The regular lattice a points file turned out to be, when it is one.</summary>
public readonly record struct SurfaceGridShape(int ColumnCount, int RowCount, double Spacing, double MinX, double MinY);

/// <summary>
/// Recognises the regular grid a toposurface points file is sampled on, and the signature of an edge
/// line the producer filled with a constant. Pure.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>This detects a fill, it does not judge terrain.</b> The distinction is the whole design. The
/// bundle that surfaced this defect has 223 m of genuine relief and 1 m neighbour steps up to 13.5 m
/// — a rule of the form "reject a Z that differs too much from its neighbours" would eat the Marin
/// Headlands along with the artefact. What is actually diagnostic is that the producer wrote the
/// <em>same bits</em> to every point in the affected lines: 400 points, two whole grid columns, all
/// carrying exactly 9.372 m, against neighbours 5 m away reaching 221 m. A DEM does not produce 400
/// bit-identical float32 values in an edge column whose neighbours span 200 m.
/// </para>
/// <para>
/// It is the second of two guards and the one that needs nothing from the manifest.
/// <see cref="SurfaceCrop"/> is better when it is available — it removes the cause rather than the
/// symptom — but it needs a bbox and a projectable frame, and this needs neither.
/// </para>
/// </remarks>
public static class SurfaceGrid
{
    /// <summary>How many outer lines per side are examined. Beyond this it stops being an edge artefact.</summary>
    public const int MaxEdgeLines = 3;

    /// <summary>How much of a line must share one value before it reads as filled rather than flat.</summary>
    public const double IdenticalFraction = 0.9;

    /// <summary>How far a filled line must sit from its neighbour before it is worth dropping.</summary>
    /// <remarks>
    /// In the points file's own unit. A genuinely flat edge — a lake, a runway, the Bay — is common
    /// and harmless; what makes a fill a defect is that it does not join the terrain beside it.
    /// </remarks>
    public const double SuspiciousOffset = 10.0;

    /// <summary>The most of a surface this guard may ever remove.</summary>
    /// <remarks>
    /// ⛔ A guard that can eat the terrain is worse than the artefact it removes. Above the cap it
    /// drops nothing and reports instead, so the failure mode is a visible fin plus a log line rather
    /// than a silently truncated site.
    /// </remarks>
    public const double MaxDroppedFraction = 0.02;

    /// <summary>
    /// The lattice <paramref name="points"/> lie on, or <c>null</c> when they are not a regular grid.
    /// </summary>
    /// <remarks>
    /// Deliberately strict: it recognises the grid only when the distinct X and Y values are evenly
    /// spaced by the same step and their product is the point count exactly. Anything less regular is
    /// not something this guard should be reasoning about line-by-line.
    /// </remarks>
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

    /// <summary>
    /// The X values of the outer columns, and Y values of the outer rows, that carry a producer's
    /// fill constant rather than terrain.
    /// </summary>
    /// <returns>
    /// Empty when nothing looks filled, when the points are not a grid, or when the drop would exceed
    /// <see cref="MaxDroppedFraction"/>. <paramref name="report"/> says which.
    /// </returns>
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

    /// <summary>Outer grid lines carrying a fill constant, keyed by their own X or Y value.</summary>
    public readonly record struct FilledEdges(
        IReadOnlySet<double>? Columns,
        IReadOnlySet<double>? Rows,
        int PointCount)
    {
        /// <summary>Whether this point sits on one of the filled lines.</summary>
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

    /// <summary>
    /// Walks inward from one end, collecting the run of lines that each carry one dominant value, and
    /// marks the whole run when it does not join the first line of real terrain beyond it.
    /// </summary>
    /// <remarks>
    /// ⛔ It has to consider the run as a whole rather than one line at a time. The bundle that
    /// surfaced this has <em>two</em> adjacent filled columns, and a line-at-a-time scan compares the
    /// outermost against its neighbour, finds them in perfect agreement — both are the same fill —
    /// and keeps both. The reference has to be the first line that is not part of the run.
    /// </remarks>
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

        // Nothing flat at this end, or the whole axis is flat and there is no terrain to compare
        // against. Either way there is nothing here this guard can be confident about.
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

    /// <summary>The bit-identical value most of a line shares, when it shares one.</summary>
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

    /// <summary>
    /// How far a candidate fill value sits from the terrain beside it, robustly.
    /// </summary>
    /// <remarks>
    /// ⛔ The median, never the maximum — and <see cref="SurfaceTinSanitiser"/> shares it for exactly
    /// that reason. Genuinely flat ground at the edge of a site (the Bay, in the bundle that
    /// motivated all this) routinely has a cliff somewhere along it, so a maximum would condemn the
    /// water along with the fill. A median asks whether the value joins the terrain beside it
    /// <em>generally</em>, which is the question that separates the two.
    /// </remarks>
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
