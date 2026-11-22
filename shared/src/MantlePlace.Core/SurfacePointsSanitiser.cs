using System.Globalization;

namespace MantlePlace.Core;

public sealed class SurfaceCleanReport
{
    public required int Kept { get; init; }

    public required int DroppedOutsideAoi { get; init; }

    public required int DroppedFilledEdge { get; init; }

    public string Explanation { get; init; } = string.Empty;

    public int TotalDropped => DroppedOutsideAoi + DroppedFilledEdge;
}

public static class SurfacePointsSanitiser
{
    public const double EdgeToleranceM = 0.001;

    public static IReadOnlyList<SurfacePoint> Clean(
        IReadOnlyList<SurfacePoint> points,
        SurfaceCropWindow? window,
        out SurfaceCleanReport report,
        string? noWindowReason = null)
    {
        ArgumentNullException.ThrowIfNull(points);

        SurfaceGrid.FilledEdges filled = SurfaceGrid.DetectFilledEdges(points, out string? capReport);

        List<SurfacePoint> kept = new(points.Count);
        int outside = 0;
        int onFill = 0;

        foreach (SurfacePoint point in points)
        {
            if (window is { IsUsable: true } aoi && !aoi.Contains(point.X, point.Y, EdgeToleranceM))
            {
                outside++;
                continue;
            }

            if (filled.Any && filled.Covers(point))
            {
                onFill++;
                continue;
            }

            kept.Add(point);
        }

        if (kept.Count < SurfacePointsReader.MinimumPoints && kept.Count < points.Count)
        {
            report = new SurfaceCleanReport
            {
                Kept = points.Count,
                DroppedOutsideAoi = 0,
                DroppedFilledEdge = 0,
                Explanation = "Cropping this terrain to the area of interest would have left almost "
                    + "nothing, so nothing was cropped. The bundle's published extent and its points "
                    + "file disagree; report the order.",
            };
            return points;
        }

        report = new SurfaceCleanReport
        {
            Kept = kept.Count,
            DroppedOutsideAoi = outside,
            DroppedFilledEdge = onFill,
            Explanation = Describe(points.Count, outside, onFill, window, capReport, noWindowReason),
        };

        return report.TotalDropped == 0 ? points : kept;
    }

    private static string Describe(
        int total,
        int outside,
        int onFill,
        SurfaceCropWindow? window,
        string? capReport,
        string? noWindowReason)
    {
        if (capReport is not null)
        {
            return capReport;
        }

        if (outside == 0 && onFill == 0)
        {
            return window is { IsUsable: true }
                ? string.Empty
                : SurfaceCrop.NoWindowNotice(noWindowReason, "every point in the file");
        }

        List<string> clauses = [];
        if (outside > 0)
        {
            clauses.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} lay outside the area you ordered — elevation rasters snap to their source pixel "
                + "grid, so they can overhang it by a few metres",
                outside));
        }

        if (onFill > 0)
        {
            clauses.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} sat on edge lines where every point carried one identical elevation, which is a "
                + "producer's fill value rather than ground",
                onFill));
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "Dropped {0} of {1} terrain points ({2:0.##}%): {3}.",
            outside + onFill,
            total,
            (outside + onFill) * 100.0 / total,
            string.Join("; ", clauses));
    }
}
