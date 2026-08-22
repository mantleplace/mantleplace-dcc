using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>What cleaning a point set removed, and what to say about it.</summary>
public sealed class SurfaceCleanReport
{
    public required int Kept { get; init; }

    public required int DroppedOutsideAoi { get; init; }

    public required int DroppedFilledEdge { get; init; }

    /// <summary>A line for the import log, or empty when there was nothing to say.</summary>
    public string Explanation { get; init; } = string.Empty;

    public int TotalDropped => DroppedOutsideAoi + DroppedFilledEdge;
}

/// <summary>
/// Removes the points a bundle should not have published before Revit ever sees them. Pure.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>This is a guard against a producer defect, not a fix for one.</b> The real fix is upstream:
/// the ETL samples <c>Elevation/DEM.tif</c> without honouring the nodata value that raster declares,
/// so the sliver of raster that over-hangs the AOI comes back as a constant instead of a hole. In the
/// bundle that surfaced it, 400 points — the two westernmost columns of a 285 × 284 grid — all carry
/// exactly 9.372 m while their true neighbours 5 m east reach 221 m, and Revit renders that as a
/// 200 m vertical fin down the edge of the site. The same fill is in that bundle's LandXML, its
/// heightmap and its mesh, so it is a platform issue and it is filed as one.
/// </para>
/// <para>
/// It is still worth guarding here, because the alternative is that a curator opens a model with a
/// 200 m wall in it and has no way to know why. Cleaning is kept out of
/// <see cref="SurfacePointsReader"/> on purpose: parsing answers "can this file be read", cleaning
/// answers "should these points be built", and conflating them would make the reader's fail-closed
/// contract negotiable.
/// </para>
/// </remarks>
public static class SurfacePointsSanitiser
{
    /// <summary>Slack on the crop comparison, in metres — a point on the AOI edge is inside it.</summary>
    /// <remarks>
    /// The points file is written to three decimal places and the window is projected, so an exact
    /// equality test on the boundary would shave a row off some bundles and not others.
    /// </remarks>
    public const double EdgeToleranceM = 0.001;

    /// <summary>
    /// Drops points outside <paramref name="window"/> and points on a filled edge line.
    /// </summary>
    /// <remarks>
    /// Both guards run. They overlap on the bundle that motivated them — the fill lives in the AOI
    /// overhang, so the crop already removes it — but neither subsumes the other: the crop needs a
    /// bbox and a projectable frame, and a fill could in principle land inside the AOI. A point
    /// removed by both is counted once, against the crop.
    /// </remarks>
    public static IReadOnlyList<SurfacePoint> Clean(
        IReadOnlyList<SurfacePoint> points,
        SurfaceCropWindow? window,
        out SurfaceCleanReport report)
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

        // Refuse to hand back a surface too small to build. Whatever is wrong with the manifest's
        // bbox, a terrain of three points is not the honest answer to it — the original points are,
        // with the reason stated.
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
            Explanation = Describe(points.Count, outside, onFill, window, capReport),
        };

        return report.TotalDropped == 0 ? points : kept;
    }

    private static string Describe(
        int total,
        int outside,
        int onFill,
        SurfaceCropWindow? window,
        string? capReport)
    {
        if (capReport is not null)
        {
            return capReport;
        }

        if (outside == 0 && onFill == 0)
        {
            return window is { IsUsable: true }
                ? string.Empty
                : "This bundle publishes no area of interest this plugin can project, so the terrain "
                    + "was built from every point in the file.";
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
