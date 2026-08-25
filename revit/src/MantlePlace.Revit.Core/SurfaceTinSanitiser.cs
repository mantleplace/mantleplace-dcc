using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// Removes the TIN vertices a bundle should not have published, before Revit ever sees them. Pure.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>This is a guard against a producer defect, not a fix for one</b>, and it is the TIN's half
/// of the guard <see cref="SurfacePointsSanitiser"/> already applies to the points file. The real
/// fix is upstream: the ETL samples <c>Elevation/DEM.tif</c> without honouring the nodata that
/// raster declares, so the sliver of raster overhanging the AOI comes back as a constant instead of
/// a hole. <b>Removal condition:</b> when re-cut bundles stop carrying it, this class,
/// <see cref="SurfacePointsSanitiser"/> and <see cref="SurfaceGrid"/>'s edge-fill detection are
/// deleted together — a second implementation of the pipeline is what the thin-client rule exists to
/// prevent, and keeping a dead guard as belt-and-braces is how one survives.
/// </para>
/// <para>
/// ⛔ <b><see cref="SurfaceGrid"/>'s detector cannot be reused here, and the crop does not cover for
/// it.</b> That detector reasons about outer grid <em>lines</em>, and a TIN has none. Measured on
/// the bundle that surfaced the defect: the points file carries the fill in one column of 284
/// bit-identical values, which the AOI crop removes outright; the TIN carries <b>1,425</b>
/// bit-identical vertices spread across an 8 m strip, because the decimator placed extra vertices
/// along the fill's boundary, and the crop reaches only 402 vertices in total. A TIN path with no
/// guard of its own re-opens a defect that was already closed once.
/// </para>
/// <para>
/// Measured on that same bundle, this guard separates the two cases by two orders of magnitude
/// rather than by a threshold that had to be tuned: the fill group's median offset to the terrain
/// it touches is <b>152.3 m</b>, and the Bay's is <b>0.000 m</b>.
/// </para>
/// <para>
/// ⛔ <b>"The largest bit-identical group" is not the rule</b>, however well it scores on the TIN. In
/// the same bundle's points file the largest such group is 26,470 points — 32.7% of the site — all
/// at exactly −0.406 m, and it is San Francisco Bay. Genuinely flat ground is common, it is often
/// bit-identical, and it is not a defect. What makes a fill a fill is that it does not join the
/// terrain beside it, which is what <see cref="SurfaceGrid.MedianAbsoluteOffset"/> measures and why
/// this shares that function rather than restating it.
/// </para>
/// </remarks>
public static class SurfaceTinSanitiser
{
    /// <summary>
    /// Removes vertices outside <paramref name="window"/>, then vertices on a producer's fill.
    /// </summary>
    /// <remarks>
    /// Both guards run, and a vertex removed by both is counted once against the crop — the same
    /// arrangement, and the same reasoning, as <see cref="SurfacePointsSanitiser.Clean"/>.
    /// </remarks>
    public static IReadOnlyList<SurfacePoint> Clean(
        SurfaceTin tin,
        IReadOnlyList<SurfacePoint> vertices,
        SurfaceCropWindow? window,
        out SurfaceCleanReport report)
    {
        ArgumentNullException.ThrowIfNull(tin);
        ArgumentNullException.ThrowIfNull(vertices);

        HashSet<int> fill = DetectFill(tin, vertices, out string? capReport);

        List<SurfacePoint> kept = new(vertices.Count);
        int outside = 0;
        int onFill = 0;

        for (int index = 0; index < vertices.Count; index++)
        {
            SurfacePoint vertex = vertices[index];

            if (window is { IsUsable: true } aoi
                && !aoi.Contains(vertex.X, vertex.Y, SurfacePointsSanitiser.EdgeToleranceM))
            {
                outside++;
                continue;
            }

            if (fill.Contains(index))
            {
                onFill++;
                continue;
            }

            kept.Add(vertex);
        }

        // Refuse to hand back a surface too small to build, for the reason the points file does:
        // whatever is wrong with the manifest's bbox, a terrain of three vertices is not the honest
        // answer to it.
        if (kept.Count < SurfacePointsReader.MinimumPoints && kept.Count < vertices.Count)
        {
            report = new SurfaceCleanReport
            {
                Kept = vertices.Count,
                DroppedOutsideAoi = 0,
                DroppedFilledEdge = 0,
                Explanation = "Cropping this terrain to the area of interest would have left almost "
                    + "nothing, so nothing was cropped. The bundle's published extent and its surface "
                    + "disagree; report the order.",
            };
            return vertices;
        }

        report = new SurfaceCleanReport
        {
            Kept = kept.Count,
            DroppedOutsideAoi = outside,
            DroppedFilledEdge = onFill,
            Explanation = Describe(vertices.Count, outside, onFill, window, capReport),
        };

        return report.TotalDropped == 0 ? vertices : kept;
    }

    /// <summary>
    /// The indices of vertices carrying a producer's fill constant rather than ground.
    /// </summary>
    /// <returns>
    /// Empty when nothing looks filled or when the drop would exceed
    /// <see cref="SurfaceGrid.MaxDroppedFraction"/>. <paramref name="report"/> says which.
    /// </returns>
    private static HashSet<int> DetectFill(
        SurfaceTin tin,
        IReadOnlyList<SurfacePoint> vertices,
        out string? report)
    {
        report = null;
        HashSet<int> fill = [];

        if (vertices.Count < SurfacePointsReader.MinimumPoints)
        {
            return fill;
        }

        Dictionary<double, List<int>> byElevation = [];
        double minX = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity;
        double maxY = double.NegativeInfinity;

        for (int index = 0; index < vertices.Count; index++)
        {
            SurfacePoint vertex = vertices[index];
            if (!byElevation.TryGetValue(vertex.Z, out List<int>? bucket))
            {
                bucket = [];
                byElevation[vertex.Z] = bucket;
            }

            bucket.Add(index);
            minX = Math.Min(minX, vertex.X);
            maxX = Math.Max(maxX, vertex.X);
            minY = Math.Min(minY, vertex.Y);
            maxY = Math.Max(maxY, vertex.Y);
        }

        foreach ((double elevation, List<int> group) in byElevation)
        {
            // Three is a triangle's worth, and below it there is nothing to be confident about. It
            // is also cheap to be wrong about: dropping two stray vertices changes no terrain.
            if (group.Count < SurfacePointsReader.MinimumPoints)
            {
                continue;
            }

            if (!TouchesEdge(group, vertices, minX, maxX, minY, maxY))
            {
                continue;
            }

            List<double> beside = Neighbours(tin, group, vertices);
            if (beside.Count > 0
                && SurfaceGrid.MedianAbsoluteOffset(beside, elevation) > SurfaceGrid.SuspiciousOffset)
            {
                fill.UnionWith(group);
            }
        }

        double fraction = (double)fill.Count / vertices.Count;
        if (fraction > SurfaceGrid.MaxDroppedFraction)
        {
            report = string.Format(
                CultureInfo.InvariantCulture,
                "{0} of {1} terrain vertices ({2:0.#}%) look like a producer's fill value rather than "
                + "ground, which is more than this plugin is willing to remove on its own. They were "
                + "kept — expect a wall or a spike at the edge of the terrain, and report the order.",
                fill.Count,
                vertices.Count,
                fraction * 100.0);
            fill.Clear();
        }

        return fill;
    }

    /// <summary>
    /// Whether any of the group lies on the surface's own outer boundary.
    /// </summary>
    /// <remarks>
    /// ⛔ This is what keeps the guard off the middle of the site. The defect is a raster sliver
    /// overhanging the AOI, so it reaches an edge by construction; a bit-identical plateau that
    /// touches no edge is a reservoir, a car park or a roof, and is terrain.
    /// </remarks>
    private static bool TouchesEdge(
        List<int> group,
        IReadOnlyList<SurfacePoint> vertices,
        double minX,
        double maxX,
        double minY,
        double maxY)
    {
        const double tolerance = SurfacePointsSanitiser.EdgeToleranceM;

        foreach (int index in group)
        {
            SurfacePoint vertex = vertices[index];
            if (vertex.X - minX <= tolerance
                || maxX - vertex.X <= tolerance
                || vertex.Y - minY <= tolerance
                || maxY - vertex.Y <= tolerance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The elevations of the vertices the group shares a triangle with, but is not.</summary>
    /// <remarks>
    /// This is the adjacency the points file never had. <see cref="SurfaceGrid"/> had to approximate
    /// it with "the next grid line inward", and needed a run-scan to stop two adjacent filled columns
    /// from vouching for each other. Real triangles make the question direct: a vertex is a
    /// neighbour when a face joins it to the group, and a member of the group is never its own
    /// reference.
    /// </remarks>
    private static List<double> Neighbours(
        SurfaceTin tin,
        List<int> group,
        IReadOnlyList<SurfacePoint> vertices)
    {
        HashSet<int> members = [.. group];
        List<double> beside = [];

        foreach (SurfaceTriangle triangle in tin.Triangles)
        {
            bool touches = members.Contains(triangle.A)
                || members.Contains(triangle.B)
                || members.Contains(triangle.C);

            if (!touches)
            {
                continue;
            }

            Consider(triangle.A, members, vertices, beside);
            Consider(triangle.B, members, vertices, beside);
            Consider(triangle.C, members, vertices, beside);
        }

        return beside;
    }

    private static void Consider(
        int index,
        HashSet<int> members,
        IReadOnlyList<SurfacePoint> vertices,
        List<double> beside)
    {
        if (!members.Contains(index))
        {
            beside.Add(vertices[index].Z);
        }
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
                    + "was built from every vertex in the surface.";
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
                "{0} sat on a patch reaching the edge of the site where every vertex carried one "
                + "identical elevation, which is a producer's fill value rather than ground",
                onFill));
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "Dropped {0} of {1} terrain vertices ({2:0.##}%): {3}.",
            outside + onFill,
            total,
            (outside + onFill) * 100.0 / total,
            string.Join("; ", clauses));
    }
}
