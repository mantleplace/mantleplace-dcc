namespace MantlePlace.Revit.Core;

/// <summary>One vector layer this host places.</summary>
/// <param name="Name">The manifest's name for the layer, in <c>vector.layers</c> and <c>hosts.revit.vectors</c>.</param>
/// <param name="DrawnFrom">The geometry its import step takes from the layer's features.</param>
/// <param name="Words">The layer named for a curator, as in "its land-use layer".</param>
public sealed record SiteVectorLayer(string Name, SiteGeometryKinds DrawnFrom, string Words);

/// <summary>
/// Which vector layer each import step places, and which geometry it draws from. Pure.
/// </summary>
/// <remarks>
/// <para>
/// The one home for both facts. The manifest reader picks a layer's file by them
/// (<see cref="BundleManifestReader"/>), the planner explains an absence with them
/// (<see cref="BundleImportPlanner"/>), and the importer parses the file for the same geometry
/// (<see cref="SiteVectorReader"/>) — three readers of one table, so a split layer's file and the
/// features taken out of it cannot disagree.
/// </para>
/// <para>
/// The road centrelines are drawn from lines; every layer cut into the ground, and both hazard
/// layers, from areas.
/// </para>
/// </remarks>
public static class SiteVectorLayers
{
    public static readonly SiteVectorLayer RoadSplines = new("road_splines", SiteGeometryKinds.Lines, "road-spline");

    public static readonly SiteVectorLayer LandUse = new("land_use", SiteGeometryKinds.Areas, "land-use");

    public static readonly SiteVectorLayer LandCover = new("land_cover", SiteGeometryKinds.Areas, "land-cover");

    public static readonly SiteVectorLayer Water = new("water", SiteGeometryKinds.Areas, "water");

    public static readonly SiteVectorLayer RoadPolygons = new("road_polygons", SiteGeometryKinds.Areas, "road-surface");

    public static readonly SiteVectorLayer FloodZones = new("flood_zones", SiteGeometryKinds.Areas, "flood-zone");

    public static readonly SiteVectorLayer SteepGround = new("steep_slope", SiteGeometryKinds.Areas, "steep-ground");

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
