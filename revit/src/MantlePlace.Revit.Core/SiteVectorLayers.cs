namespace MantlePlace.Revit.Core;

/// <summary>One vector layer this host places.</summary>
/// <param name="Name">The manifest's name for the layer, in <c>vector.layers</c> and <c>hosts.revit.vectors</c>.</param>
/// <param name="DrawnFrom">The geometry its import step takes from the layer's features.</param>
/// <param name="Label">What the step places, in the words a curator reads: "water bodies".</param>
public sealed record SiteVectorLayer(string Name, SiteGeometryKinds DrawnFrom, string Label);

/// <summary>
/// Which vector layer each import step places, which geometry it draws from, and what the step is
/// called. Pure.
/// </summary>
/// <remarks>
/// The one home for all three. The manifest reader picks a layer's file by them
/// (<see cref="BundleManifestReader"/>), the planner plans and explains each step with them
/// (<see cref="BundleImportPlanner"/>), the importer parses the file for the same geometry
/// (<see cref="SiteVectorReader"/>), and <see cref="GroundLayerWords"/> takes its labels from here —
/// so a split layer's file and the features taken out of it cannot disagree, and a step has one
/// spelling.
/// </remarks>
public static class SiteVectorLayers
{
    public static readonly SiteVectorLayer RoadSplines = new("road_splines", SiteGeometryKinds.Lines, "road centrelines");

    public static readonly SiteVectorLayer LandUse = new("land_use", SiteGeometryKinds.Areas, "site boundaries");

    public static readonly SiteVectorLayer LandCover = new("land_cover", SiteGeometryKinds.Areas, "land cover");

    public static readonly SiteVectorLayer Water = new("water", SiteGeometryKinds.Areas, "water bodies");

    public static readonly SiteVectorLayer RoadPolygons = new("road_polygons", SiteGeometryKinds.Areas, "road surfaces");

    public static readonly SiteVectorLayer FloodZones = new("flood_zones", SiteGeometryKinds.Areas, "flood zones");

    public static readonly SiteVectorLayer SteepGround = new("steep_slope", SiteGeometryKinds.Areas, "steep ground");

    /// <summary>The layer <paramref name="kind"/> places, or <c>null</c> for a step that places none.</summary>
    public static SiteVectorLayer? Of(ImportStepKind kind) => kind switch
    {
        ImportStepKind.RoadCentrelines => RoadSplines,
        ImportStepKind.SiteBoundaries => LandUse,
        ImportStepKind.LandCover => LandCover,
        ImportStepKind.Water => Water,
        ImportStepKind.RoadPolygons => RoadPolygons,
        ImportStepKind.FloodZones => FloodZones,
        ImportStepKind.SteepGround => SteepGround,
        _ => null,
    };

    /// <summary>The layer <paramref name="kind"/> places, for a step that is known to place one.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The step places no vector layer.</exception>
    public static SiteVectorLayer For(ImportStepKind kind) => Of(kind) ?? throw new ArgumentOutOfRangeException(
        nameof(kind),
        kind,
        "This import step places no vector layer, so there is no geometry to read for it.");
}
