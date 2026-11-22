using System.Globalization;

namespace MantlePlace.Core;

public static class SurfaceTinSanitiser
{
    public static IReadOnlyList<SurfacePoint> Clean(
        SurfaceTin tin,
        IReadOnlyList<SurfacePoint> vertices,
        SurfaceCropWindow? window,
        out SurfaceCleanReport report,
        string? noWindowReason = null)
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
            Explanation = Describe(vertices.Count, outside, onFill, window, capReport, noWindowReason),
        };

        return report.TotalDropped == 0 ? vertices : kept;
    }

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
                : SurfaceCrop.NoWindowNotice(noWindowReason, "every vertex in the surface");
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
