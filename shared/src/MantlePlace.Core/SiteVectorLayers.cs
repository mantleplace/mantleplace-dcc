namespace MantlePlace.Core;

public sealed record SiteVectorLayer(string Name, SiteGeometryKinds DrawnFrom, string Label);

public static class SiteVectorLayers
{
    public static readonly SiteVectorLayer RoadSplines = new("road_splines", SiteGeometryKinds.Lines, "road centrelines");

    public static readonly SiteVectorLayer LandUse = new("land_use", SiteGeometryKinds.Areas, "land use");

    public static readonly SiteVectorLayer LandCover = new("land_cover", SiteGeometryKinds.Areas, "land cover");

    public static readonly SiteVectorLayer Water = new("water", SiteGeometryKinds.Areas, "water bodies");

    public static readonly SiteVectorLayer RoadPolygons = new("road_polygons", SiteGeometryKinds.Areas, "road surfaces");

    public static readonly SiteVectorLayer FloodZones = new("flood_zones", SiteGeometryKinds.Areas, "flood zones");

    public static readonly SiteVectorLayer SteepGround = new("steep_slope", SiteGeometryKinds.Areas, "steep ground");

    public static SiteVectorLayer? Of(ImportStepKind kind) => kind switch
    {
        ImportStepKind.RoadCentrelines => RoadSplines,
        ImportStepKind.LandUse => LandUse,
        ImportStepKind.LandCover => LandCover,
        ImportStepKind.Water => Water,
        ImportStepKind.RoadPolygons => RoadPolygons,
        ImportStepKind.FloodZones => FloodZones,
        ImportStepKind.SteepGround => SteepGround,
        _ => null,
    };

    public static SiteVectorLayer For(ImportStepKind kind) => Of(kind) ?? throw new ArgumentOutOfRangeException(
        nameof(kind),
        kind,
        "This import step places no vector layer, so there is no geometry to read for it.");
}
