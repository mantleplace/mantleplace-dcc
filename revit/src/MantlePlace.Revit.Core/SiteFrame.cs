namespace MantlePlace.Revit.Core;

/// <summary>What a layer file's plan coordinates are, and so how they reach a <see cref="SiteFrame"/>.</summary>
public enum LayerCoordinates
{
    /// <summary>WGS84 lon/lat, through the one projection <c>HPS-45</c> permits.</summary>
    Geographic,

    /// <summary>Absolute eastings and northings in the origin's own CRS: subtraction alone.</summary>
    AbsoluteProjected,

    /// <summary>East/north offsets about the origin — <c>local_enu</c> — already local.</summary>
    LocalOffsets,
}

/// <summary>A layer file's coordinates and the linear unit they, and any Z, are in.</summary>
/// <remarks>
/// The unit is the FILE's (<c>spec/format.md</c> §4.2), not the origin's: on a local grid the origin
/// is published in metres while the files are in feet.
/// </remarks>
public readonly record struct LayerFrame(LayerCoordinates Coordinates, LinearUnit Unit)
{
    /// <summary>The shared <c>vector</c> set's frame: lon/lat, with any Z in metres.</summary>
    public static LayerFrame Geographic => new(LayerCoordinates.Geographic, LinearUnit.Metre);
}

/// <summary>
/// The AOI-centroid frame the bundle's geometry is placed in: east/north metres from the manifest's
/// pre-derived origin, with Z left as the absolute orthometric height every artifact carries.
/// </summary>
/// <remarks>
/// <para>
/// It exists because the bundle's artifacts do not agree about frames and are not meant to.
/// <c>Surface/SurfacePoints.csv</c> is already local — east/north offsets from the AOI centroid —
/// which is what lets the toposolid land near the project origin. <c>Landcover/TreePoints.csv</c> is
/// absolute, in the delivery CRS. The shared <c>vector</c> GeoJSON layers are lon/lat, and this
/// host's own copy of them (<c>hosts.revit.vectors</c>) is absolute in the origin's CRS or offsets
/// about it. All of them describe the same ground, and the origin that reconciles them is the one
/// this host already applies verbatim as its survey point (<c>revit.georeference.origin.projected</c>).
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
    /// An unknown layer CRS (<c>0</c>) is never assumed to match. The tree-points CSV is published
    /// in the delivery CRS, so this is normally true for it — by coincidence rather than by
    /// contract (<c>HPS-52</c>), which is why the CRS it states is still checked. The imagery drape
    /// is built in the AOI's metric UTM zone for the fixed-frame host, so on a State Plane tier this
    /// is genuinely false for it and the layer is skipped with a stated reason.
    /// </remarks>
    public bool CanPlaceProjected(int layerEpsg) => layerEpsg != 0 && layerEpsg == Epsg;

    /// <summary>
    /// Whether absolute coordinates in <paramref name="unit"/> can be subtracted from this origin —
    /// true only in the origin's OWN unit.
    /// </summary>
    /// <remarks>
    /// The subtraction happens in the origin's unit (<see cref="TryToLocalMetres"/>), so a file
    /// stating another has contradicted its own CRS and is refused rather than rescaled
    /// (<c>HPS-35</c>). An origin that states no unit is metric, as every such bundle was.
    /// </remarks>
    public bool IsInOriginUnit(LinearUnit unit) => Normalised(unit) == Normalised(Origin.LinearUnit);

    /// <summary>
    /// Whether a host block's declared <c>file_frame</c> is this frame — the showing <c>HPS-53</c>
    /// asks for before a file the block points at is placed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A projected file frame must name the origin's own CRS. A local one must sit on the origin's
    /// CRS and about the origin itself: offsets about any other point are a frame whose files would
    /// all land displaced by the difference (<c>spec/format.md</c> §4.2). Its origin is compared in
    /// metres to a millimetre, because the two are published in the units each chose.
    /// </para>
    /// <para>
    /// An unknown frame type fails closed, as the schema instructs: a frame this host cannot read is
    /// a file it cannot place.
    /// </para>
    /// </remarks>
    public bool Holds(FileFrame fileFrame)
    {
        ArgumentNullException.ThrowIfNull(fileFrame);

        switch (fileFrame.Kind)
        {
            case FileFrameKind.Projected:
                return CanPlaceProjected(fileFrame.Epsg ?? 0);

            case FileFrameKind.Local:
                if (!CanPlaceProjected(fileFrame.Epsg ?? 0)
                    || fileFrame.Origin is not { Easting: { } easting, Northing: { } northing } local
                    || !TryToLocalMetres(0.0, 0.0, out double originEastM, out double originNorthM))
                {
                    return false;
                }

                // Both origins in metres about the projected CRS's own zero, then compared.
                double metresPerUnit = LinearUnits.MetresPerUnit(local.LinearUnit);
                return Math.Abs((easting * metresPerUnit) + originEastM) < OriginToleranceM
                    && Math.Abs((northing * metresPerUnit) + originNorthM) < OriginToleranceM;

            default:
                return false;
        }
    }

    /// <summary>
    /// Places one plan position of a layer file in frame-local metres, by whichever route its
    /// coordinates take.
    /// </summary>
    /// <returns>
    /// <c>false</c> when this frame cannot place the position — a lon/lat layer on an origin the one
    /// projection cannot reach, or absolute coordinates in a unit other than the origin's.
    /// </returns>
    public bool TryPlace(LayerFrame layer, double x, double y, out double eastMetres, out double northMetres)
    {
        eastMetres = 0.0;
        northMetres = 0.0;

        switch (layer.Coordinates)
        {
            case LayerCoordinates.Geographic:
                return TryProjectToLocalMetres(x, y, out eastMetres, out northMetres);

            case LayerCoordinates.AbsoluteProjected:
                return IsInOriginUnit(layer.Unit) && TryToLocalMetres(x, y, out eastMetres, out northMetres);

            case LayerCoordinates.LocalOffsets:
                double metresPerUnit = LinearUnits.MetresPerUnit(layer.Unit);
                eastMetres = x * metresPerUnit;
                northMetres = y * metresPerUnit;
                return true;

            default:
                return false;
        }
    }

    /// <summary>How far apart two statements of the same origin may be and still be the same point.</summary>
    private const double OriginToleranceM = 0.001;

    private static LinearUnit Normalised(LinearUnit unit) => unit == LinearUnit.Unspecified ? LinearUnit.Metre : unit;

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
