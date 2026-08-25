namespace MantlePlace.Revit.Core;

/// <summary>
/// Brings a TIN read from <c>Surface/Surface.dxf</c> into the frame the toposolid is built in. Pure.
/// </summary>
/// <remarks>
/// <para>
/// The manifest declares that file <c>absolute_projected</c>: eastings and northings in the bundle's
/// own projected CRS, around 500 000 m, which is where Revit's precision warnings start. The points
/// file is <c>local_enu</c> already, which is the only reason the toposolid has ever landed near the
/// project origin. This closes that gap, and closes it the same way
/// <see cref="TreePointsReader"/> already does for the absolute tree points: by subtracting the
/// origin the manifest publishes, through <see cref="SiteFrame"/>, and doing nothing else
/// (<c>HPS-33</c>).
/// </para>
/// <para>
/// ⛔ <b>Z is not re-referenced, only converted.</b> Every artifact's Z is an absolute orthometric
/// height, as this host's own block says in as many words, so subtracting anything from it would put
/// the terrain underground. What happens to Z is a unit conversion and nothing more.
/// </para>
/// </remarks>
public static class SurfaceTinFrame
{
    /// <summary>
    /// The TIN's vertices in local metres, or <c>null</c> with a stated reason.
    /// </summary>
    /// <remarks>
    /// The output is metres on every tier, because the subtraction consumes the artifact's unit here
    /// rather than deferring it to the shim — the same shape as <see cref="SiteTree"/>, whose
    /// coordinates arrive converted for the same reason. A caller that also applied
    /// <c>ImportStep.Units</c> would convert twice.
    /// </remarks>
    public static IReadOnlyList<SurfacePoint>? TryToLocalMetres(
        SurfaceTin tin,
        SiteFrame frame,
        LinearUnit artifactUnit,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(tin);
        ArgumentNullException.ThrowIfNull(frame);
        reason = null;

        // ⛔ The artifact's coordinates are in its CRS's own unit, and the CRS is the origin's. When
        // the manifest states both units and they disagree, the bundle contradicts itself and there
        // is no reading of it that is safe to guess at — a foot/metre mix-up is a site 3.28× wrong
        // with nothing on screen to suggest it (HPS-35, the same fail-closed shape SiteFrame uses
        // for a foot unit on a UTM origin).
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
