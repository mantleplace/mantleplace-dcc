namespace MantlePlace.Core;

public enum LayerCoordinates
{
    Geographic,

    AbsoluteProjected,

    LocalOffsets,
}

public readonly record struct LayerFrame(LayerCoordinates Coordinates, LinearUnit Unit)
{
    public static LayerFrame Geographic => new(LayerCoordinates.Geographic, LinearUnit.Metre);
}

public sealed class SiteFrame
{
    public required GeoOrigin Origin { get; init; }

    public int Epsg => Origin.Epsg ?? 0;

    public bool CanPlaceGeographic
        => GeoProjection.IsUtmEpsg(Epsg) && Origin.LinearUnit is LinearUnit.Unspecified or LinearUnit.Metre;

    public bool CanPlaceProjected(int layerEpsg) => layerEpsg != 0 && layerEpsg == Epsg;

    public bool IsInOriginUnit(LinearUnit unit) => Normalised(unit) == Normalised(Origin.LinearUnit);

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

                double metresPerUnit = LinearUnits.MetresPerUnit(local.LinearUnit);
                return Math.Abs((easting * metresPerUnit) + originEastM) < OriginToleranceM
                    && Math.Abs((northing * metresPerUnit) + originNorthM) < OriginToleranceM;

            default:
                return false;
        }
    }

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

    private const double OriginToleranceM = 0.001;

    private static LinearUnit Normalised(LinearUnit unit) => unit == LinearUnit.Unspecified ? LinearUnit.Metre : unit;

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

    public bool TryProjectToLocalMetres(double lonDeg, double latDeg, out double eastMetres, out double northMetres)
    {
        eastMetres = 0.0;
        northMetres = 0.0;

        return CanPlaceGeographic
            && GeoProjection.TryLonLatToUtm(lonDeg, latDeg, Epsg, out double easting, out double northing)
            && TryToLocalMetres(easting, northing, out eastMetres, out northMetres);
    }

    public static SiteFrame? For(BundleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return manifest.SurveyPoint is { IsUsable: true } origin ? new SiteFrame { Origin = origin } : null;
    }
}
