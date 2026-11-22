namespace MantlePlace.Core;

public enum ExtractionLifetime
{
    Transient,

    Retained,
}

public static class ImportStepKinds
{
    public static ExtractionLifetime LifetimeOf(ImportStepKind kind) => kind switch
    {
        ImportStepKind.ToposurfaceFromPointsFile => ExtractionLifetime.Transient,

        ImportStepKind.ToposurfaceFromSurfaceTin => ExtractionLifetime.Transient,

        ImportStepKind.PublishedContours => ExtractionLifetime.Transient,

        ImportStepKind.RoadCentrelines => ExtractionLifetime.Transient,
        ImportStepKind.LandUse => ExtractionLifetime.Transient,
        ImportStepKind.LandCover => ExtractionLifetime.Transient,
        ImportStepKind.Water => ExtractionLifetime.Transient,
        ImportStepKind.RoadPolygons => ExtractionLifetime.Transient,
        ImportStepKind.Vegetation => ExtractionLifetime.Transient,

        ImportStepKind.FloodZones => ExtractionLifetime.Transient,
        ImportStepKind.SteepGround => ExtractionLifetime.Transient,

        ImportStepKind.ContextBuildings => ExtractionLifetime.Transient,

        ImportStepKind.ImageryDrape => ExtractionLifetime.Retained,

        _ => ExtractionLifetime.Retained,
    };

    public static bool ImportsContent(ImportStepKind kind) => kind is not (
        ImportStepKind.SetSharedCoordinates
        or ImportStepKind.SetSiteLocation
        or ImportStepKind.AttributionAndProvenance
        or ImportStepKind.SiteContextView);
}
