namespace MantlePlace.Revit.Core;

/// <summary>
/// The rectangle, in the points file's own local east/north metres, that a point must fall inside.
/// </summary>
/// <remarks>
/// Half-open on neither side: a point exactly on the edge is inside. The AOI corners are themselves
/// the edge, so excluding them would shave a row and a column off every bundle for no reason.
/// </remarks>
public readonly record struct SurfaceCropWindow(double WestM, double SouthM, double EastM, double NorthM)
{
    /// <summary>Whether the window is a usable rectangle rather than a degenerate or inverted one.</summary>
    public bool IsUsable
        => double.IsFinite(WestM) && double.IsFinite(SouthM)
            && double.IsFinite(EastM) && double.IsFinite(NorthM)
            && EastM > WestM && NorthM > SouthM;

    /// <summary>Whether a local-frame point falls inside, with a tolerance for float round-tripping.</summary>
    public bool Contains(double eastM, double northM, double tolerance)
        => eastM >= WestM - tolerance && eastM <= EastM + tolerance
            && northM >= SouthM - tolerance && northM <= NorthM + tolerance;
}

/// <summary>
/// Builds the crop window from what the manifest publishes. Pure.
/// </summary>
/// <remarks>
/// <para>
/// This is defensive parsing of a published contract, not a derived placement value: it subtracts an
/// origin the manifest published and projects the AOI corners the manifest published, both through
/// <see cref="SiteFrame"/>, which already does exactly this for the vector layers under
/// <c>HPS-45</c>. Nothing here selects a source, assembles a mosaic or reasons about coverage.
/// </para>
/// <para>
/// ⛔ <b>Never build the window from <c>elevation.dem.bounds_target_crs</c>.</b> That IS the
/// over-hanging raster extent — for the bundle that surfaced this defect its west edge is
/// 545176.00 while the AOI's is 545184.74, an 8.74 m overhang — and the whole point of the crop is to
/// discard what lives in that overhang. Cropping to the DEM bounds keeps every bad point and reads
/// like a fix.
/// </para>
/// </remarks>
public static class SurfaceCrop
{
    /// <summary>
    /// The AOI rectangle in local metres, or <c>null</c> when the manifest publishes no bbox or the
    /// frame cannot project it.
    /// </summary>
    /// <remarks>
    /// <c>null</c> is a real answer and never a refusal to import. A bundle whose frame cannot
    /// project — a State-Plane-foot tier, say — still gets its terrain, still gets
    /// <see cref="SurfaceGrid"/>'s bbox-free guard, and gets a log line saying the crop was
    /// unavailable. Degrading loudly beats declining.
    /// </remarks>
    public static SurfaceCropWindow? For(BundleManifest manifest, SiteFrame? frame)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (frame is null || !manifest.HasBbox)
        {
            return null;
        }

        // All four corners, not two: a lon/lat rectangle is not axis-aligned in UTM, so the AOI's
        // west edge is not one easting.
        if (!frame.TryProjectToLocalMetres(manifest.BboxWestDeg, manifest.BboxSouthDeg, out double swE, out double swN)
            || !frame.TryProjectToLocalMetres(manifest.BboxWestDeg, manifest.BboxNorthDeg, out double nwE, out double nwN)
            || !frame.TryProjectToLocalMetres(manifest.BboxEastDeg, manifest.BboxSouthDeg, out double seE, out double seN)
            || !frame.TryProjectToLocalMetres(manifest.BboxEastDeg, manifest.BboxNorthDeg, out double neE, out double neN))
        {
            return null;
        }

        // ⛔ The OUTER extremes — the projected quadrilateral's bounding box — not the inner ones.
        // The first version took the inner extremes, reasoning that a window strictly inside the
        // published AOI was the conservative choice. Measured against a real bundle it was the
        // opposite: convergence tilts the quadrilateral by about 8 m over a 1.4 km AOI, so the
        // inscribed rectangle cut two extra rows off the north and south edges of good terrain — it
        // dropped 1,980 of 80,940 points (2.45%) to remove 400 bad ones. The bounding box drops 1,134
        // (1.4%), one outer ring, and still removes every one of them: the defect lives in the
        // raster's 8.74 m overhang, which is outside either rectangle.
        SurfaceCropWindow window = new(
            WestM: Math.Min(swE, nwE),
            SouthM: Math.Min(swN, seN),
            EastM: Math.Max(seE, neE),
            NorthM: Math.Max(nwN, neN));

        return window.IsUsable ? window : null;
    }
}
