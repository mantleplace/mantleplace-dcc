namespace MantlePlace.Revit.Core;

/// <summary>
/// The AOI-centroid frame the bundle's geometry is placed in: east/north metres from the manifest's
/// pre-derived origin, with Z left as the absolute orthometric height every artifact carries.
/// </summary>
/// <remarks>
/// <para>
/// It exists because the bundle's artifacts do not agree about frames and are not meant to.
/// <c>Surface/SurfacePoints.csv</c> is already local — east/north offsets from the AOI centroid —
/// which is what lets the toposolid land near the project origin. <c>Landcover/TreePoints.csv</c> is
/// absolute AOI-UTM. The <c>vector</c> GeoJSON layers are lon/lat. All three describe the same
/// ground, and the origin that reconciles them is the one this host already applies verbatim as its
/// survey point (<c>revit.georeference.origin.projected</c>).
/// </para>
/// <para>
/// So this re-derives nothing (<c>HPS-33</c>): it subtracts a published origin and, for the
/// geographic layers alone, performs the one projection <c>HPS-45</c> permits. Both conversions
/// live here rather than in the shim because the interesting cases are the refusals, and a refusal
/// nobody can assert without a Revit licence is a refusal nobody checks (<c>HPS-02</c>).
/// </para>
/// </remarks>
public sealed class SiteFrame
{
    /// <summary>The pre-derived origin, exactly as the manifest published it.</summary>
    public required GeoOrigin Origin { get; init; }

    /// <summary>The origin's projected CRS, or <c>0</c> when it published none.</summary>
    public int Epsg => Origin.Epsg ?? 0;

    /// <summary>
    /// Whether a lon/lat layer can be brought into this frame.
    /// </summary>
    /// <remarks>
    /// Only into a UTM origin, because UTM is the only forward <see cref="GeoProjection"/> has. On a
    /// State-Plane tier the honest answer is "not by this host" — projecting into the wrong CRS and
    /// subtracting produces a number that looks like a coordinate and is hundreds of kilometres out.
    /// The unit is checked too: a UTM zone is metric by definition, so a foot unit on a UTM origin is
    /// an internally inconsistent manifest and fails closed rather than being reconciled (<c>HPS-35</c>).
    /// </remarks>
    public bool CanPlaceGeographic
        => GeoProjection.IsUtmEpsg(Epsg) && Origin.LinearUnit is LinearUnit.Unspecified or LinearUnit.Metre;

    /// <summary>
    /// Whether a layer already in projected coordinates can be placed by subtraction alone — true
    /// only when it is in the origin's OWN CRS.
    /// </summary>
    /// <remarks>
    /// An unknown layer CRS (<c>0</c>) is never assumed to match. The tree-points CSV is AOI-UTM
    /// whatever the delivery tier, so on a foot tier this is genuinely false and the layer is
    /// skipped with a stated reason.
    /// </remarks>
    public bool CanPlaceProjected(int layerEpsg) => layerEpsg != 0 && layerEpsg == Epsg;

    /// <summary>
    /// Converts a plan coordinate in the origin's own CRS into frame-local metres.
    /// </summary>
    /// <remarks>
    /// The subtraction happens in the origin's unit and the result is converted once, so a
    /// State-Plane-foot origin and its own foot coordinates would still yield metres — the unit
    /// travels with the origin rather than being assumed.
    /// </remarks>
    /// <returns><c>false</c> when the frame cannot place coordinates in that CRS at all.</returns>
    public bool TryToLocalMetres(double easting, double northing, out double eastMetres, out double northMetres)
    {
        eastMetres = 0.0;
        northMetres = 0.0;

        if (Origin.Easting is not { } originEasting || Origin.Northing is not { } originNorthing)
        {
            return false;
        }

        double metresPerUnit = LinearUnits.MetresPerUnit(Origin.LinearUnit);
        eastMetres = (easting - originEasting) * metresPerUnit;
        northMetres = (northing - originNorthing) * metresPerUnit;
        return true;
    }

    /// <summary>Projects WGS84 lon/lat into this frame's local metres (<c>HPS-45</c>).</summary>
    /// <returns><c>false</c> when the frame is not one this host can project into.</returns>
    public bool TryProjectToLocalMetres(double lonDeg, double latDeg, out double eastMetres, out double northMetres)
    {
        eastMetres = 0.0;
        northMetres = 0.0;

        return CanPlaceGeographic
            && GeoProjection.TryLonLatToUtm(lonDeg, latDeg, Epsg, out double easting, out double northing)
            && TryToLocalMetres(easting, northing, out eastMetres, out northMetres);
    }

    /// <summary>
    /// The frame a manifest states, or <c>null</c> when it states none.
    /// </summary>
    /// <remarks>
    /// The same origin the survey point is published from, and for the same reason: this host reads
    /// its own block and applies it verbatim (<c>HPS-33</c>). <c>null</c> is a real
    /// answer — a bundle with no published origin gets its absolute layers skipped with a reason,
    /// never placed against a centroid this host worked out for itself.
    /// </remarks>
    public static SiteFrame? For(BundleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return manifest.SurveyPoint is { IsUsable: true } origin ? new SiteFrame { Origin = origin } : null;
    }
}
