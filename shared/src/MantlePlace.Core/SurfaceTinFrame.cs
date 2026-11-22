namespace MantlePlace.Core;

public static class SurfaceTinFrame
{
    public static IReadOnlyList<SurfacePoint>? TryToLocalMetres(
        SurfaceTin tin,
        SiteFrame frame,
        LinearUnit artifactUnit,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(tin);
        ArgumentNullException.ThrowIfNull(frame);
        reason = null;

        if (artifactUnit is not LinearUnit.Unspecified
            && frame.Origin.LinearUnit is not LinearUnit.Unspecified
            && artifactUnit != frame.Origin.LinearUnit)
        {
            reason = "The surface DXF states a different linear unit from the origin it is measured "
                + "against, so this plugin cannot place it without guessing which one is right.";
            return null;
        }

        double metresPerUnit = LinearUnits.MetresPerUnit(artifactUnit);
        List<SurfacePoint> local = new(tin.Vertices.Count);

        foreach (SurfacePoint vertex in tin.Vertices)
        {
            if (!frame.TryToLocalMetres(vertex.X, vertex.Y, out double east, out double north))
            {
                reason = "This bundle publishes no origin this plugin can measure the surface DXF "
                    + "against, so the terrain would have landed at the project origin rather than "
                    + "on the site.";
                return null;
            }

            local.Add(new SurfacePoint(east, north, vertex.Z * metresPerUnit));
        }

        return local;
    }
}
