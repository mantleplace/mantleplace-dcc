namespace MantlePlace.Revit.Core;

/// <summary>Linear unit a bundle's coordinates are expressed in (<c>delivery.linear_unit</c>).</summary>
public enum LinearUnit
{
    /// <summary>The manifest did not say. Not an error — older/metric bundles omit the block.</summary>
    Unspecified,
    Metre,
    /// <summary>US survey foot, exactly 1200/3937 m.</summary>
    UsSurveyFoot,
    /// <summary>International foot, exactly 0.3048 m.</summary>
    InternationalFoot,
}

/// <summary>Coarse unit system (<c>delivery.unit_system</c>).</summary>
public enum UnitSystem
{
    Unspecified,
    Metric,
    Imperial,
}

/// <summary>
/// A pointer to one artifact inside the bundle, plus whatever frame metadata the ETL attached.
/// </summary>
/// <remarks>
/// <para>
/// Every nullable member means <em>unknown</em>, never <em>absent</em> and never <em>zero</em>
/// (HPS-20). A <c>null</c> <see cref="Sha256"/> in particular means the ETL published no hash for
/// this artifact — which is the normal case for the Revit deliverables today — so the integrity
/// check is <em>skipped</em> and the artifact is valid-but-unverified. Coercing it to "corrupt"
/// makes every current bundle un-importable; coercing it to "verified" is a lie (HPS-27, HPS-34).
/// </para>
/// </remarks>
public sealed class BundleArtifact
{
    /// <summary>Bundle-relative path, taken from a manifest pointer and never from convention (HPS-32).</summary>
    public required string Path { get; init; }

    /// <summary>Lowercase hex sha256, or <c>null</c> when the manifest advertised none.</summary>
    public string? Sha256 { get; init; }

    /// <summary>True only when a hash was actually advertised.</summary>
    public bool IsSha256Known => !string.IsNullOrWhiteSpace(Sha256);

    /// <summary>Raw <c>units</c> string as published (<c>"m"</c>, <c>"ftUS"</c>, <c>"ft"</c>).</summary>
    public string? Units { get; init; }

    /// <summary>Raw <c>vertical_datum</c>, e.g. <c>"EGM2008-orthometric"</c>.</summary>
    public string? VerticalDatum { get; init; }

    /// <summary>
    /// Raw <c>horizontal_frame</c> (local artifacts) or <c>horizontal_crs</c> (absolute ones).
    /// Free prose today; a machine-readable enum is proposed to the platform.
    /// </summary>
    public string? HorizontalFrame { get; init; }

    /// <summary>Raw <c>format</c> / <c>surf_type</c> / IFC <c>schema</c> discriminator.</summary>
    public string? Format { get; init; }

    /// <summary>Raw <c>georeference</c> prose, IFC only.</summary>
    public string? Georeference { get; init; }

    public int? TriangleCount { get; init; }

    public int? FootprintCount { get; init; }

    /// <summary>
    /// Raw <c>foliage_type_vocabulary</c>, tree points only — the closed vocabulary the CSV's
    /// <c>foliage_type</c> values are drawn from, or <c>null</c> where the manifest named none.
    /// </summary>
    /// <remarks>
    /// Optional, and added by MPB 1.2.0. <c>null</c> is <em>the manifest named no vocabulary</em>,
    /// which is not the same as naming the one this build knows: without it the column's values are
    /// uninterpretable and every point is a tree (<c>spec/format.md</c> §4.4).
    /// </remarks>
    public string? FoliageTypeVocabulary { get; init; }
}

/// <summary>One <c>hosts.&lt;hostId&gt;.readiness.&lt;path&gt;</c> entry.</summary>
/// <remarks>
/// The v18 schema requires <c>reason</c> exactly when <c>present</c> is false and forbids it
/// otherwise, so an empty reason on an absent path is a producer bug worth surfacing rather than
/// swallowing (HPS-36).
/// </remarks>
public sealed class ReadinessPath
{
    public bool Present { get; init; }

    public string Reason { get; init; } = string.Empty;

    /// <summary>True when the manifest carried this entry at all.</summary>
    public bool Declared { get; init; }
}

/// <summary>The <c>hosts.revit.readiness</c> block. A host reads only its own subtree (HPS-36).</summary>
public sealed class RevitReadiness
{
    public ReadinessPath ToposurfacePoints { get; init; } = new();

    public ReadinessPath IfcSite { get; init; } = new();

    public ReadinessPath SurfaceDxf { get; init; } = new();

    /// <summary>True when <c>hosts.revit.readiness</c> was present at all.</summary>
    public bool Declared { get; init; }
}

/// <summary>A pre-derived geographic origin, applied verbatim and never re-derived (HPS-33).</summary>
/// <remarks>
/// The plan coordinates are NOT metres by definition. <c>delivery.local_origin</c> spells its pair
/// <c>easting_m</c>/<c>northing_m</c> and is metric by field name, but the v19
/// <c>revit.georeference.origin.projected</c> block carries its own <c>linear_unit</c> and publishes
/// a State-Plane foot origin on the foot tiers. Naming the fields <c>EastingM</c> and handing them
/// to Revit as metres placed such a site 3.28× out from true, which is why the unit travels WITH the
/// coordinates rather than being assumed by whoever consumes them.
/// </remarks>
public sealed class GeoOrigin
{
    public double? Lon { get; init; }

    public double? Lat { get; init; }

    public int? Epsg { get; init; }

    /// <summary>Plan easting, in <see cref="LinearUnit"/> — not necessarily metres.</summary>
    public double? Easting { get; init; }

    /// <summary>Plan northing, in <see cref="LinearUnit"/> — not necessarily metres.</summary>
    public double? Northing { get; init; }

    /// <summary>
    /// The unit <see cref="Easting"/> and <see cref="Northing"/> are expressed in.
    /// <see cref="LinearUnit.Unspecified"/> is metric, the same reading every other unit site in
    /// this host takes for an unstated one.
    /// </summary>
    public LinearUnit LinearUnit { get; init; }

    /// <summary>True only when both plan coordinates and their EPSG are known.</summary>
    public bool IsUsable => Easting.HasValue && Northing.HasValue && Epsg.HasValue;
}

/// <summary>
/// The v19 <c>revit.georeference</c> block — this host's OWN placement statement (HPS-33).
/// </summary>
/// <remarks>
/// It exists because the host-neutral <c>delivery.local_origin</c> is emitted on the <c>local_ft</c>
/// tier alone, so on every other tier there was nothing this host was allowed to read and the model
/// was imported un-georeferenced.
/// A sibling host's <c>georeference</c> is never a fallback for a missing value here: the shared
/// corpus states the two in conflict on purpose, and merging them reports the wrong CRS.
/// </remarks>
public sealed class RevitGeoreference
{
    /// <summary>Raw <c>crs_projected</c>, e.g. <c>"EPSG:32613"</c>.</summary>
    public string CrsProjected { get; init; } = string.Empty;

    /// <summary>Raw <c>vertical_datum</c>, e.g. <c>"EGM2008-orthometric"</c>.</summary>
    public string VerticalDatum { get; init; } = string.Empty;

    /// <summary>
    /// <c>grid_rotation_deg</c>, or <c>null</c> when the block stated none — unknown, not zero
    /// (HPS-20). What an unknown rotation means for placement is the planner's call, not this
    /// record's.
    /// </summary>
    public double? GridRotationDeg { get; init; }

    /// <summary><c>origin</c> plus <c>origin.projected</c>, flattened.</summary>
    public GeoOrigin? Origin { get; init; }

    /// <summary>True when the manifest carried a <c>revit.georeference</c> block at all.</summary>
    public bool Declared { get; init; }
}

/// <summary>
/// An axis-aligned ground extent in a projected CRS, as the manifest publishes it:
/// <c>[left, bottom, right, top]</c>.
/// </summary>
/// <remarks>
/// The coordinates are in <see cref="Epsg"/>'s own unit, which is not assumed to be metres for the
/// same reason <see cref="GeoOrigin"/> does not assume it. Nothing here converts; converting is
/// <see cref="SiteFrame"/>'s job, and it needs the origin to do it.
/// </remarks>
public sealed class GroundExtent
{
    public required double Left { get; init; }

    public required double Bottom { get; init; }

    public required double Right { get; init; }

    public required double Top { get; init; }

    /// <summary>The CRS the four coordinates are in, or <c>0</c> when the manifest named none.</summary>
    public int Epsg { get; init; }

    /// <summary>
    /// True only for a non-degenerate extent in a named CRS.
    /// </summary>
    /// <remarks>
    /// An inverted or zero-area rectangle is refused rather than normalised. A drape stretched over
    /// a rectangle this host silently flipped would land mirrored on the terrain, which reads as a
    /// plausible aerial photo of somewhere else.
    /// </remarks>
    public bool IsUsable => Epsg != 0 && Right > Left && Top > Bottom;

    /// <summary>Width in the CRS's own unit.</summary>
    public double WidthUnits => Right - Left;

    /// <summary>Height in the CRS's own unit.</summary>
    public double HeightUnits => Top - Bottom;
}

/// <summary>One <c>attribution.sources[]</c> entry, verbatim.</summary>
/// <remarks>
/// Pass-through, and nothing more. This host writes these words into the project as the manifest
/// gave them and makes no licensing decision of its own: which terms a source carries, and what they
/// oblige, is the platform's statement. Every member but <see cref="ProviderId"/> is <c>null</c> when
/// the manifest gave none — <c>license_url</c> is published as <c>null</c> for some sources today,
/// though the schema types it as a string.
/// </remarks>
/// <param name="ProviderId"><c>provider_id</c>, the one field the schema requires; empty when absent.</param>
/// <param name="AttributionText"><c>attribution_text</c>.</param>
/// <param name="License"><c>license</c> — the licence text, not an identifier.</param>
/// <param name="LicenseUrl"><c>license_url</c>.</param>
public sealed record AttributionSource(
    string ProviderId,
    string? AttributionText,
    string? License,
    string? LicenseUrl);

/// <summary>
/// <c>location.time_zone</c>, verbatim — a fact about the place, published for
/// every host, and applied here without being derived (<c>HPS-33</c>).
/// </summary>
/// <remarks>
/// The offset is the one field a Revit site location takes, so it is the one this record cannot do
/// without: a block whose <c>utc_offset_standard_h</c> does not read as a number is not read. It is kept as
/// published — fractional, and unclamped to any host's range. Fitting it into Revit's is
/// <see cref="SiteTimeZone"/>'s job, not the reader's.
/// </remarks>
public sealed class PublishedTimeZone
{
    /// <summary><c>iana</c>, e.g. <c>"America/Denver"</c>; empty when the block named none.</summary>
    public string Iana { get; init; } = string.Empty;

    /// <summary><c>utc_offset_standard_h</c>: standard time, in hours east of UTC.</summary>
    public required double UtcOffsetStandardH { get; init; }

    /// <summary><c>observes_dst</c>, or <c>null</c> when the block did not say — unknown, not no.</summary>
    public bool? ObservesDst { get; init; }
}

/// <summary>The top-level <c>delivery</c> block — units and grid, host-neutral.</summary>
public sealed class DeliveryFacts
{
    public UnitSystem UnitSystem { get; init; }

    /// <summary>
    /// Raw <c>delivery.unit_system</c>, kept so a value this reader does not know can still be shown
    /// as published (<see cref="DeliveryHeader"/>) rather than as <see cref="UnitSystem.Unspecified"/>.
    /// </summary>
    public string UnitSystemValue { get; init; } = string.Empty;

    /// <summary>Raw <c>delivery.tier</c> (<c>metric</c>, <c>sp_ftus</c>, <c>sp_ft</c>, <c>local_ft</c>).</summary>
    public string Tier { get; init; } = string.Empty;

    public LinearUnit LinearUnit { get; init; }

    public int? HorizontalEpsg { get; init; }

    /// <summary>
    /// <c>delivery.label</c> (MPB 1.3.0): the display words for the delivery CRS, the same words the
    /// platform shows a person. Empty on every earlier bundle.
    /// </summary>
    public string Label { get; init; } = string.Empty;

    /// <summary><c>delivery.local_origin</c> — emitted only on the <c>local_ft</c> tier.</summary>
    public GeoOrigin? LocalOrigin { get; init; }

    /// <summary>True when the manifest carried a <c>delivery</c> block at all.</summary>
    public bool Declared { get; init; }
}

/// <summary>
/// Everything the Revit plugin reads out of <c>Metadata/manifest.json</c>, plus the verdict.
/// </summary>
/// <remarks>
/// <para>
/// The result is always returned, even on refusal: a bundle that is not importable must still
/// yield its top-level facts — order id, bounding box, layout pointers, delivery model — so the
/// vault join key and the streaming path survive (HPS-37). Callers check
/// <see cref="IsValid"/> and show <see cref="Error"/>; they do not treat a refusal as an exception.
/// </para>
/// <para>
/// <see cref="IsValid"/> answers "is this a usable description of a materialized bundle", NOT
/// "does it have Revit content" — that second question is <see cref="HasRevitContent"/>, and a
/// bundle can legitimately be valid with nothing for Revit in it. Collapsing the two would refuse
/// manifests the shared corpus requires every host to accept.
/// </para>
/// </remarks>
public sealed class BundleManifest
{
    public bool IsValid { get; internal set; }

    public string Error { get; internal set; } = string.Empty;

    /// <summary>The ETL job id. Changes on every rebuild — <em>not</em> the vault join key (HPS-37).</summary>
    public string JobId { get; internal set; } = string.Empty;

    /// <summary>The vault order id. This, and only this, joins to the vault.</summary>
    public string OrderId { get; internal set; } = string.Empty;

    /// <summary>
    /// The manifest's <c>version</c> verbatim, e.g. <c>"1.0.0"</c>; empty when absent.
    /// </summary>
    /// <remarks>
    /// A STRING in the MPB era, and kept unparsed so a refusal can quote exactly what the bundle
    /// said — including an integer-era value this reader does not speak, which would be
    /// unrecoverable once coerced to a number.
    /// </remarks>
    public string Version { get; internal set; } = string.Empty;

    /// <summary><c>packaging.delivery_model</c>, e.g. <c>"base_on_demand"</c>.</summary>
    public string DeliveryModel { get; internal set; } = string.Empty;

    public double BboxWestDeg { get; internal set; }

    public double BboxSouthDeg { get; internal set; }

    public double BboxEastDeg { get; internal set; }

    public double BboxNorthDeg { get; internal set; }

    public bool HasBbox { get; internal set; }

    /// <summary><c>layout.cesiumTerrain</c> — read even on a refusal so streaming still works.</summary>
    public string CesiumTerrainPath { get; internal set; } = string.Empty;

    /// <summary>The whole <c>layout</c> pointer table, verbatim.</summary>
    public IReadOnlyDictionary<string, string> Layout { get; internal set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// <c>vector.layers[name=="road_splines"].formats[format=="geojson"]</c>. Host-invariant: the
    /// top-level vector pointers bind every host, not just the one that draws splines (HPS-41).
    /// </summary>
    public string RoadSplinesPath { get; internal set; } = string.Empty;

    public string RoadSplinesSha256 { get; internal set; } = string.Empty;

    public bool HasRoadSplines { get; internal set; }

    /// <summary>
    /// The same <c>road_splines</c> geojson as an artifact the planner can resolve — Forma's "Roads"
    /// row.
    /// </summary>
    /// <remarks>
    /// The three flat members above are what the shared corpus asserts, and they stay: the corpus
    /// pins layer selection (name AND format, no fallback to a format this host cannot read), which
    /// is a contract fact every host shares. This is the same pointer shaped like every other
    /// artifact so one planner path can handle it.
    /// </remarks>
    public BundleArtifact? RoadSplines { get; internal set; }

    /// <summary>
    /// <c>vector.layers[name=="land_use"]</c> geojson — Forma's "Site limits / property boundaries"
    /// row.
    /// </summary>
    public BundleArtifact? LandUse { get; internal set; }

    /// <summary>
    /// <c>vector.layers[name=="land_cover"]</c> geojson — the physical ground cover, whose
    /// <c>subtype</c> names the renderer keyword a subdivision's material carries.
    /// </summary>
    public BundleArtifact? LandCover { get; internal set; }

    /// <summary>
    /// <c>vector.layers[name=="water"]</c> geojson — streams as centrelines and water bodies as
    /// polygons in one layer. Only its polygons become subdivisions.
    /// </summary>
    public BundleArtifact? Water { get; internal set; }

    /// <summary>
    /// <c>vector.layers[name=="road_polygons"]</c> geojson — the road surfaces derived from
    /// <c>road</c>, already widened, merged per class and cut so no two overlap.
    /// </summary>
    public BundleArtifact? RoadPolygons { get; internal set; }

    /// <summary>
    /// Whether the bundle carries the base <c>road</c> layer at all, in any format.
    /// </summary>
    /// <remarks>
    /// The one thing that tells "this area has no roads" apart from "the road surfaces were not
    /// derived for this order": <c>road_polygons</c> is best-effort and can be absent beside a
    /// <c>road</c> layer that is not, and a consumer that reports no roads in that case is wrong
    /// about the area rather than about the bundle (<c>spec/format.md</c> §6.3).
    /// </remarks>
    public bool HasRoadLayer { get; internal set; }

    /// <summary><c>Landcover/TreePoints.csv</c> — Forma's "Vegetation" row.</summary>
    public BundleArtifact? TreePoints { get; internal set; }

    /// <summary>
    /// <c>attribution.sources[]</c>, in the manifest's order — empty when the block is absent, which
    /// the schema says it is when no source contributed.
    /// </summary>
    public IReadOnlyList<AttributionSource> AttributionSources { get; internal set; } = [];

    public DeliveryFacts Delivery { get; internal set; } = new();

    /// <summary>
    /// <c>location.time_zone</c>, or <c>null</c> on a manifest with no <c>location</c> block, and on
    /// one whose block this reader could not use.
    /// </summary>
    public PublishedTimeZone? TimeZone { get; internal set; }

    public RevitReadiness Readiness { get; internal set; } = new();

    /// <summary>The <c>revit.georeference</c> block — this host's own placement statement.</summary>
    public RevitGeoreference Georeference { get; internal set; } = new();

    /// <summary><c>Surface/SurfacePoints.csv</c> — the editable-toposurface path.</summary>
    public BundleArtifact? ToposurfacePoints { get; internal set; }

    /// <summary><c>Surface/Surface.dxf</c> — the CAD-linework toposurface path.</summary>
    public BundleArtifact? SurfaceDxf { get; internal set; }

    /// <summary><c>Site/Site.ifc</c> — terrain context plus building massing.</summary>
    public BundleArtifact? SiteIfc { get; internal set; }

    /// <summary><c>Surface/Surface.landxml</c> — the Civil 3D path, carried for completeness.</summary>
    public BundleArtifact? LandXml { get; internal set; }

    /// <summary><c>Surface/Contours.dxf</c> — 2-D plan linework.</summary>
    public BundleArtifact? ContoursDxf { get; internal set; }

    /// <summary><c>Imagery/Drape.png</c> — the satellite imagery draped on the terrain.</summary>
    /// <remarks>
    /// Its <see cref="BundleArtifact.Sha256"/> is <c>null</c> today, and that is not an oversight:
    /// v19 publishes the drape's hash under <c>unreal.imagery_drape.sha256</c> alone, which is a
    /// sibling host's block this one may not read (<c>HPS-36</c>). So the drape is
    /// imported valid-but-unverified, exactly as <see cref="BundleArtifact"/> describes, and the
    /// host-neutral <c>imagery.drape</c> block proposed to the platform carries a <c>sha256</c> so
    /// this stops being true.
    /// </remarks>
    public BundleArtifact? ImageryDrape { get; internal set; }

    /// <summary>
    /// <c>imagery.drape.extent</c> — the drape's own statement of the ground it covers, host-neutral
    /// and authoritative when present.
    /// </summary>
    /// <remarks>
    /// Not published by any bundle cut today; the block is the one proposed to the platform.
    /// Reading it now is what makes the fallback below deletable later rather than load-bearing
    /// forever.
    /// </remarks>
    public GroundExtent? ImageryDrapeExtent { get; internal set; }

    /// <summary>
    /// <c>elevation.dem.bounds_target_crs</c> plus <c>elevation.dem.crs</c> — the DEM's own ground
    /// extent, and the drape's only host-neutral extent until <see cref="ImageryDrapeExtent"/> lands.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Undeclared.</b> <c>elevation.dem</c> rides <c>additionalProperties: true</c> in the
    /// published v19 schema and does not declare this field at all, so it is emitted-and-tolerated
    /// rather than contracted. The declared extent — <c>unreal.imagery_drape.extent</c> — is in a
    /// block this host may not read, and the two agree byte-for-byte in the bundles on hand. That
    /// agreement is evidence, not a guarantee, which is why the planner corroborates this value
    /// against the drape's own pixel grid before using it and refuses when it cannot.
    /// </remarks>
    public GroundExtent? DemBounds { get; internal set; }

    /// <summary>
    /// <c>imagery.gsd_m</c> — the imagery's ground sample distance in METRES, or <c>null</c> when
    /// the manifest published none. Host-neutral, and schema-declared as nullable.
    /// </summary>
    public double? ImageryGsdM { get; internal set; }

    /// <summary>
    /// <c>imagery.present</c> — <c>false</c> only when the manifest explicitly said so.
    /// </summary>
    /// <remarks>
    /// Distinguished from "the block was absent" on purpose. A bundle whose ETL produced no imagery
    /// says so here, and telling that curator to re-download would send them round a loop that
    /// cannot end differently.
    /// </remarks>
    public bool ImageryAbsentByDeclaration { get; internal set; }

    /// <summary>True when the bundle carries at least one artifact this plugin can import.</summary>
    public bool HasRevitContent =>
        ToposurfacePoints is not null || SurfaceDxf is not null || SiteIfc is not null;

    /// <summary>
    /// The pre-derived origin for Revit's survey point / shared coordinates, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RevitGeoreference.Origin"/> is the primary source and <c>delivery.local_origin</c>
    /// the fallback: a host reads its OWN block (HPS-33), and the own-block value wins
    /// wherever both are published. That ordering is what makes reading the own block a complete fix
    /// with no coordinated pipeline release — the <c>local_ft</c> bundles already in curators' hands
    /// keep the origin they always had, and every other tier gains the one it was never given.
    /// </para>
    /// <para>
    /// Still pre-derived, never computed. A bundle that publishes neither leaves this <c>null</c>
    /// and <see cref="BundleImportPlan"/> reports the survey point as un-set rather than guessing.
    /// </para>
    /// </remarks>
    public GeoOrigin? SurveyPoint { get; internal set; }

    public bool HasPreDerivedSurveyPoint => SurveyPoint is { IsUsable: true };

    /// <summary>
    /// Directory prefix of a Cesium terrain pointer: <c>"Elevation/Terrain/layer.json"</c> yields
    /// <c>"Elevation/Terrain/"</c>, and a bare <c>"layer.json"</c> yields <c>""</c> — not <c>"/"</c>.
    /// </summary>
    public static string DeriveCesiumTerrainPrefix(string cesiumTerrainPath)
    {
        if (string.IsNullOrEmpty(cesiumTerrainPath))
        {
            return string.Empty;
        }

        int cut = cesiumTerrainPath.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? string.Empty : cesiumTerrainPath[..(cut + 1)];
    }
}
