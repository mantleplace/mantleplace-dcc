using System.Globalization;
using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>
/// Reads <c>Metadata/manifest.json</c> into a <see cref="BundleManifest"/>. Pure: no file system,
/// no Revit API, no network — text in, facts out (HPS-02).
/// </summary>
public static class BundleManifestReader
{
    /// <summary>Host key this reader claims in the shared conformance corpus.</summary>
    public const string HostKey = "revit";

    internal const string NotJsonMessage = "manifest.json is not valid JSON.";

    /// <summary>
    /// Layout keys for the artifacts this plugin imports. The manifest's <c>layout</c> table is the
    /// pointer of record: schema-required and schema-declared, while the <c>elevation.*</c> and
    /// <c>buildings.ifc</c> detail blocks ride <c>additionalProperties</c>. Those
    /// blocks are read for frame metadata, and for the path only as a fallback (HPS-32).
    /// </summary>
    private const string LayoutPointsCsv = "points_csv";
    private const string LayoutSurfaceDxf = "surface_dxf";
    private const string LayoutBuildingsIfc = "buildings_ifc";
    private const string LayoutLandXml = "landxml";
    private const string LayoutContours = "contours";
    private const string LayoutTreePoints = "tree_points";
    private const string LayoutImageryDrape = "imagery_drape";

    /// <summary>
    /// What <see cref="BundleArtifact.HorizontalFrame"/> reads on a layer whose coordinates are
    /// WGS84 lon/lat rather than a projected CRS.
    /// </summary>
    internal const string GeographicFrame = "EPSG:4326";

    /// <summary>
    /// What <see cref="BundleArtifact.HorizontalFrame"/> reads on a deliverable whose coordinates are
    /// absolute eastings and northings in the bundle's OWN projected CRS.
    /// </summary>
    /// <remarks>
    /// It names a frame rather than a CRS on purpose, and there is nothing to parse an EPSG out of:
    /// the CRS is the one the same block already publishes as
    /// <c>hosts.revit.georeference.crs_projected</c>. Reading the token as "the origin's CRS" applies
    /// two published statements together; it does not derive a third (<c>HPS-33</c>).
    /// </remarks>
    internal const string ProjectedFrame = "absolute_projected";

    /// <summary>
    /// Deliverable sub-objects of the <c>hosts.revit</c> block — this host's OWN block (HPS-33). Each
    /// is optional, and each present one carries a <c>sha256</c> the schema makes required (HPS-34).
    /// </summary>
    private const string RevitToposurfacePoints = "toposurface_points";
    private const string RevitSurfaceDxf = "surface_dxf";
    private const string RevitIfcSite = "ifc_site";

    private static readonly string[] RevitDeliverableKeys =
        [RevitToposurfacePoints, RevitSurfaceDxf, RevitIfcSite];

    /// <summary>
    /// The envelope every host block lives under at MPB 1.0.0. One key replaces the roster: a host
    /// asks whether this object has ANY key, never which keys it has, which is what makes roster
    /// staleness structurally unable to matter rather than merely tolerated.
    /// </summary>
    private const string HostsKey = "hosts";

    /// <summary>
    /// This host's own subtree, <c>hosts.revit</c> — and never a sibling's (HPS-33).
    /// </summary>
    private static JsonElement? RevitHostBlock(JsonElement root) =>
        root.Object(HostsKey)?.Object(HostKey);

    /// <summary>
    /// Parses manifest text. Never throws. On refusal the returned manifest carries
    /// <see cref="BundleManifest.IsValid"/> <c>false</c>, a populated
    /// <see cref="BundleManifest.Error"/>, and every top-level fact that was readable before the
    /// refusal — the order id above all, because that is the vault join key (HPS-37).
    /// </summary>
    public static BundleManifest Parse(string jsonText)
    {
        BundleManifest manifest = new();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(jsonText ?? string.Empty);
        }
        catch (JsonException)
        {
            return Refuse(manifest, NotJsonMessage);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Refuse(manifest, NotJsonMessage);
            }

            return ParseRoot(manifest, root);
        }
    }

    private static BundleManifest ParseRoot(BundleManifest manifest, JsonElement root)
    {
        manifest.JobId = root.Str("job_id");

        // Read as a STRING and parsed as semver. An MPB version IS a string, so the numeric read
        // this used to do is gone entirely — and with it the truncate-vs-round divergence that
        // needed policing, since there is no longer a number to disagree about. Kept verbatim and
        // unparsed on the manifest so a refusal can quote exactly what the bundle said, including
        // an integer-era value this reader does not speak.
        manifest.Version = root.Str("version");
        manifest.OrderId = ReadOrderId(root);

        // Clean break (HPS-31). Anything that fails to parse as semver — an absent version, an
        // integer from the pre-history, a partial "1.0" — is refused. Deliberately NOT coerced
        // through a number first: the integer era read an absent version as 0 and refused it, and
        // letting a string fall to 0 the same way would be an accident that happens to work rather
        // than a decision. This is the one refusal that returns immediately: below the floor, the
        // rest of the document is written in a dialect this host does not speak, so reading on
        // would be dual-parsing by another name.
        ManifestVersion parsedVersion = ManifestVersion.Parse(manifest.Version);
        ManifestVersion floor = ManifestVersion.Parse(ManifestVersions.MinSupportedManifestVersion);
        if (!parsedVersion.IsValid || parsedVersion.CompareTo(floor) < 0)
        {
            return Refuse(
                manifest,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Bundle manifest version {0} is no longer supported (minimum {1}). Re-download this AOI "
                    + "from your vault at mantle.place/vault — rebuilding it there re-cuts the bundle on the "
                    + "current pipeline.",
                    string.IsNullOrEmpty(manifest.Version) ? "(absent)" : manifest.Version,
                    ManifestVersions.MinSupportedManifestVersion));
        }

        // The other end of the semver compatibility policy, which the integer era had no way to
        // express. Minors are strictly additive and unknown fields are ignored, so any 1.x reads
        // here; an unknown higher MAJOR is a graceful refusal rather than a best-effort parse,
        // because a major is exactly the promise that something this reader relies on may have
        // changed meaning. Too-old and too-new are distinct refusals with distinct remedies —
        // re-download the AOI, versus update the plugin — which a bare "unsupported" conflated.
        if (parsedVersion.Major > floor.Major)
        {
            return Refuse(
                manifest,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Bundle manifest version {0} is newer than this plugin understands (it reads {1}.x). "
                    + "Update the Mantle Place plugin to import this bundle.",
                    manifest.Version,
                    floor.Major));
        }

        // From here every refusal is accumulated rather than returned, so a refused manifest still
        // hands the caller a fully-populated set of facts (HPS-37).
        string? refusal = null;

        manifest.DeliveryModel = root.Object("packaging")?.Str("delivery_model") ?? string.Empty;

        ReadBbox(manifest, root);
        ReadLayout(manifest, root);
        ReadRoadSplines(manifest, root);
        refusal ??= ReadDelivery(manifest, root);
        ReadReadiness(manifest, root);
        ReadArtifacts(manifest, root);
        refusal ??= ReadGeoreference(manifest, root);

        // The own block wins; `delivery.local_origin` is the fallback that keeps the `local_ft`
        // bundles already in curators' hands importable (HPS-33). An own block that
        // is present but incomplete falls through rather than shadowing a usable delivery origin —
        // the two are meant to agree, and "published but unusable" is closer to absent than to
        // authoritative.
        manifest.SurveyPoint = manifest.Georeference.Origin is { IsUsable: true } ownOrigin
            ? ownOrigin
            : manifest.Delivery.LocalOrigin;

        refusal ??= CheckRevitHashes(manifest, root);
        refusal ??= CheckMaterialized(manifest, root);

        if (refusal is not null)
        {
            return Refuse(manifest, refusal);
        }

        manifest.IsValid = true;
        return manifest;
    }

    /// <summary>
    /// The vault join key. Top-level <c>order_id</c> is authoritative; <c>attribution.order_id</c> is
    /// the fallback the packager actually emits today. The ETL <c>jobId</c> is deliberately NOT a
    /// fallback — it changes on every rebuild and joining on it would silently address the wrong
    /// row (HPS-37).
    /// </summary>
    private static string ReadOrderId(JsonElement root)
    {
        string orderId = root.Str("order_id");
        if (!string.IsNullOrEmpty(orderId))
        {
            return orderId;
        }

        return root.Object("attribution")?.Str("order_id") ?? string.Empty;
    }

    /// <summary>
    /// Reads the bounding box. Each edge is optional-with-a-companion-flag rather than
    /// defaulted-to-zero: a `null` west is <em>unknown</em>, and coercing it to 0.0 would produce a
    /// box that looks real and spans the Greenwich meridian (HPS-20). All four must be known before
    /// <see cref="BundleManifest.HasBbox"/> can be true.
    /// </summary>
    private static void ReadBbox(BundleManifest manifest, JsonElement root)
    {
        if (root.Object("bbox") is not { } bbox)
        {
            return;
        }

        double? west = bbox.OptionalDouble("west");
        double? south = bbox.OptionalDouble("south");
        double? east = bbox.OptionalDouble("east");
        double? north = bbox.OptionalDouble("north");

        manifest.BboxWestDeg = west ?? 0.0;
        manifest.BboxSouthDeg = south ?? 0.0;
        manifest.BboxEastDeg = east ?? 0.0;
        manifest.BboxNorthDeg = north ?? 0.0;
        manifest.HasBbox = west.HasValue && south.HasValue && east.HasValue && north.HasValue
            && east.Value > west.Value
            && north.Value > south.Value;
    }

    private static void ReadLayout(BundleManifest manifest, JsonElement root)
    {
        Dictionary<string, string> layout = new(StringComparer.Ordinal);
        if (root.Object("layout") is { } table)
        {
            foreach (JsonProperty entry in table.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String)
                {
                    string value = entry.Value.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        layout[entry.Name] = value;
                    }
                }
            }
        }

        manifest.Layout = layout;
        manifest.CesiumTerrainPath = layout.GetValueOrDefault("cesium_terrain", string.Empty);
    }

    /// <summary>
    /// Selects by layer name AND format: the first <c>road_splines</c> layer, then the first
    /// <c>geojson</c> entry inside it. A layer that ships only formats this host cannot read yields
    /// no splines — there is no fallback to gpkg, because "wrong format" and "absent" have the same
    /// correct outcome here and a fallback would hand the importer bytes it cannot parse.
    /// </summary>
    private static void ReadRoadSplines(BundleManifest manifest, JsonElement root)
    {
        manifest.RoadSplines = ReadVectorLayer(root, "road_splines");
        manifest.RoadSplinesPath = manifest.RoadSplines?.Path ?? string.Empty;
        manifest.RoadSplinesSha256 = manifest.RoadSplines?.Sha256 ?? string.Empty;
        manifest.HasRoadSplines = !string.IsNullOrEmpty(manifest.RoadSplinesPath);

        manifest.LandUse = ReadVectorLayer(root, "land_use");
    }

    /// <summary>
    /// Selects one <c>vector</c> layer by layer name AND format: the first layer of that name, then
    /// the first <c>geojson</c> entry inside it. A layer that ships only formats this host cannot
    /// read yields nothing — there is no fallback to gpkg, because "wrong format" and "absent" have
    /// the same correct outcome here and a fallback would hand the importer bytes it cannot parse.
    /// </summary>
    /// <remarks>
    /// The format table's <c>path</c> IS the manifest pointer for these layers (<c>HPS-32</c>) — the
    /// top-level <c>layout.vector</c> names the directory, not the file, so there is no layout key
    /// to prefer over it.
    /// </remarks>
    private static BundleArtifact? ReadVectorLayer(JsonElement root, string layerName)
    {
        if (root.Object("vector")?.Array("layers") is not { } layers)
        {
            return null;
        }

        foreach (JsonElement layer in layers.EnumerateArray())
        {
            if (layer.ValueKind != JsonValueKind.Object
                || !string.Equals(layer.Str("name"), layerName, StringComparison.Ordinal))
            {
                continue;
            }

            if (layer.Array("formats") is { } formats)
            {
                foreach (JsonElement format in formats.EnumerateArray())
                {
                    if (format.ValueKind != JsonValueKind.Object
                        || !string.Equals(format.Str("format"), "geojson", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string path = format.Str("path");
                    return string.IsNullOrWhiteSpace(path)
                        ? null
                        : new BundleArtifact
                        {
                            Path = path,
                            Sha256 = format.OptionalStr("sha256"),
                            Format = "geojson",

                            // RFC 7946 fixes the CRS at WGS84 lon/lat and the bundle's layers say so
                            // in their own `crs` member. Stated rather than left blank so the
                            // planner branches on a value instead of on the layer's name.
                            HorizontalFrame = GeographicFrame,
                        };
                }
            }

            // Only the first layer of that name is ever considered, with or without a geojson.
            break;
        }

        return null;
    }

    /// <summary>
    /// Reads <c>delivery</c>. An unsupported enum value fails closed and names the offending value
    /// rather than falling back to the default (HPS-35) — silently reading an unknown
    /// <c>linear_unit</c> as metres would place an imperial site 3.28× off with nothing to debug.
    /// An <em>absent</em> block is not an error: metric bundles omit it.
    /// </summary>
    private static string? ReadDelivery(BundleManifest manifest, JsonElement root)
    {
        if (root.Object("delivery") is not { } delivery)
        {
            manifest.Delivery = new DeliveryFacts();
            return null;
        }

        // `unit_system` is recorded but NOT gated on. HPS-35 fails closed on an enum the host acts
        // on; this one it does not act on — every scale decision comes from `linear_unit` and the
        // per-artifact `units`. Refusing here would make the whole bundle unimportable the day web
        // adds a third token, over a value that changes nothing about the import.
        UnitSystem unitSystem = delivery.Str("unit_system") switch
        {
            "metric" => UnitSystem.Metric,
            "imperial" => UnitSystem.Imperial,
            _ => UnitSystem.Unspecified,
        };

        if (!TryReadLinearUnit(delivery.Str("linear_unit"), out LinearUnit linearUnit))
        {
            return UnsupportedLinearUnit("delivery.linear_unit", delivery.Str("linear_unit"));
        }

        GeoOrigin? localOrigin = null;
        if (delivery.Object("local_origin") is { } origin)
        {
            // Metric by field name — `easting_m`/`northing_m` — and so unaffected by the block's
            // own `linear_unit`, which describes the ARTIFACTS and reads "ft" on the one tier that
            // publishes this origin.
            localOrigin = new GeoOrigin
            {
                Lon = origin.OptionalDouble("lon"),
                Lat = origin.OptionalDouble("lat"),
                Epsg = origin.OptionalInt("utm_epsg"),
                Easting = origin.OptionalDouble("easting_m"),
                Northing = origin.OptionalDouble("northing_m"),
                LinearUnit = LinearUnit.Metre,
            };
        }

        manifest.Delivery = new DeliveryFacts
        {
            Declared = true,
            UnitSystem = unitSystem,
            Tier = delivery.Str("tier"),
            LinearUnit = linearUnit,
            HorizontalEpsg = delivery.OptionalInt("horizontal_epsg"),
            LocalOrigin = localOrigin,
        };

        return null;
    }

    /// <summary>
    /// Reads <c>revit.georeference</c> — this host's own placement block (HPS-33).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The field names here are NOT <c>delivery.local_origin</c>'s. The origin's plan pair is
    /// <c>projected.easting</c>/<c>projected.northing</c> with an explicit
    /// <c>projected.linear_unit</c>, where the delivery block spells them <c>easting_m</c>/
    /// <c>northing_m</c> and means metres. Reading the first pair with the second pair's assumption
    /// is a State-Plane-foot site placed 3.28× out, which is why the unit is carried on
    /// <see cref="GeoOrigin"/> rather than baked into the field name.
    /// </para>
    /// <para>
    /// An unreadable <c>linear_unit</c> fails the whole manifest closed, exactly as
    /// <c>delivery.linear_unit</c> does (HPS-35): the origin is what positions everything the bundle
    /// carries, so a scale nobody can read is a statement about the bundle rather than about one
    /// artifact. An <em>absent</em> one is metric, the reading every other unit site in this host
    /// takes for an unstated unit.
    /// </para>
    /// </remarks>
    private static string? ReadGeoreference(BundleManifest manifest, JsonElement root)
    {
        if (RevitHostBlock(root)?.Object("georeference") is not { } georeference)
        {
            manifest.Georeference = new RevitGeoreference();
            return null;
        }

        GeoOrigin? origin = null;
        if (georeference.Object("origin") is { } published)
        {
            JsonElement? projected = published.Object("projected");
            string rawLinearUnit = projected?.Str("linear_unit") ?? string.Empty;
            if (!TryReadLinearUnit(rawLinearUnit, out LinearUnit linearUnit))
            {
                return UnsupportedLinearUnit(
                    "revit.georeference.origin.projected.linear_unit",
                    rawLinearUnit);
            }

            origin = new GeoOrigin
            {
                Lon = published.OptionalDouble("lon"),
                Lat = published.OptionalDouble("lat"),
                Epsg = projected?.OptionalInt("epsg"),
                Easting = projected?.OptionalDouble("easting"),
                Northing = projected?.OptionalDouble("northing"),
                LinearUnit = linearUnit,
            };
        }

        manifest.Georeference = new RevitGeoreference
        {
            Declared = true,
            CrsProjected = georeference.Str("crs_projected"),
            VerticalDatum = georeference.Str("vertical_datum"),
            GridRotationDeg = georeference.OptionalDouble("grid_rotation_deg"),
            Origin = origin,
        };

        return null;
    }

    /// <summary>The three linear units the ETL delivers in; an empty token is "unstated".</summary>
    private static bool TryReadLinearUnit(string raw, out LinearUnit unit)
    {
        switch (raw)
        {
            case "":
                unit = LinearUnit.Unspecified;
                return true;
            case "m":
                unit = LinearUnit.Metre;
                return true;
            case "ftUS":
                unit = LinearUnit.UsSurveyFoot;
                return true;
            case "ft":
                unit = LinearUnit.InternationalFoot;
                return true;
            default:
                unit = LinearUnit.Unspecified;
                return false;
        }
    }

    /// <summary>
    /// Fails closed on a unit this host cannot read, naming the field AND the value (HPS-35) —
    /// silently reading an unknown token as metres would place an imperial site 3.28× off with
    /// nothing to debug.
    /// </summary>
    private static string UnsupportedLinearUnit(string field, string raw)
        => string.Format(
            CultureInfo.InvariantCulture,
            "Unsupported {0} \"{1}\": this plugin understands only \"m\", \"ftUS\" and \"ft\".",
            field,
            raw);

    /// <summary>
    /// Reads <c>hosts.revit.readiness</c> and nothing else. A sibling host's block is ignored, never
    /// merged, and the retired v17 anonymous keys are not a fallback — reading them would turn a
    /// clean break into dual-parsing (HPS-36).
    /// </summary>
    private static void ReadReadiness(BundleManifest manifest, JsonElement root)
    {
        if (RevitHostBlock(root)?.Object("readiness") is not { } revit)
        {
            manifest.Readiness = new RevitReadiness();
            return;
        }

        manifest.Readiness = new RevitReadiness
        {
            Declared = true,
            ToposurfacePoints = ReadReadinessPath(revit, "toposurface_points"),
            IfcSite = ReadReadinessPath(revit, "ifc_site"),
            SurfaceDxf = ReadReadinessPath(revit, "surface_dxf"),
        };
    }

    private static ReadinessPath ReadReadinessPath(JsonElement host, string key)
    {
        if (host.Object(key) is not { } path)
        {
            return new ReadinessPath();
        }

        return new ReadinessPath
        {
            Declared = true,
            Present = path.Bool("present"),
            Reason = path.Str("reason"),
        };
    }

    private static void ReadArtifacts(BundleManifest manifest, JsonElement root)
    {
        JsonElement? elevation = root.Object("elevation");
        JsonElement? buildings = root.Object("buildings");
        JsonElement? revit = RevitHostBlock(root);

        manifest.ToposurfacePoints = BuildArtifact(
            manifest,
            LayoutPointsCsv,
            elevation?.Object("points_csv"),
            detail => new BundleArtifact
            {
                Path = string.Empty,
                Format = detail?.OptionalStr("format"),
                Units = detail?.OptionalStr("units"),
                VerticalDatum = detail?.OptionalStr("vertical_datum"),
                HorizontalFrame = detail?.OptionalStr("horizontal_frame"),
            },
            revit?.Object(RevitToposurfacePoints));

        manifest.SurfaceDxf = BuildArtifact(
            manifest,
            LayoutSurfaceDxf,
            elevation?.Object("surface_dxf"),
            detail => new BundleArtifact
            {
                Path = string.Empty,
                Format = detail?.OptionalStr("surf_type"),
                Units = detail?.OptionalStr("units"),
                VerticalDatum = detail?.OptionalStr("vertical_datum"),
                HorizontalFrame = detail?.OptionalStr("horizontal_crs"),
                TriangleCount = detail?.OptionalInt("triangle_count"),
            },
            revit?.Object(RevitSurfaceDxf));

        manifest.SiteIfc = BuildArtifact(
            manifest,
            LayoutBuildingsIfc,
            buildings?.Object("ifc"),
            detail => new BundleArtifact
            {
                Path = string.Empty,
                Format = detail?.OptionalStr("schema"),
                Units = detail?.OptionalStr("units"),
                Georeference = detail?.OptionalStr("georeference"),
                TriangleCount = detail?.OptionalInt("terrain_triangle_count"),
                FootprintCount = detail?.OptionalInt("footprint_count"),
            },
            revit?.Object(RevitIfcSite));

        manifest.LandXml = BuildArtifact(
            manifest,
            LayoutLandXml,
            elevation?.Object("landxml"),
            detail => new BundleArtifact
            {
                Path = string.Empty,
                Format = detail?.OptionalStr("surf_type"),
                Units = detail?.OptionalStr("units"),
                VerticalDatum = detail?.OptionalStr("vertical_datum"),
                HorizontalFrame = detail?.OptionalStr("horizontal_crs"),
                TriangleCount = detail?.OptionalInt("triangle_count"),
            });

        manifest.ContoursDxf = BuildArtifact(
            manifest,
            LayoutContours,
            elevation?.Object("contours"),
            detail => new BundleArtifact
            {
                Path = string.Empty,
                Units = detail?.OptionalStr("units"),
                VerticalDatum = detail?.OptionalStr("vertical_datum"),
            });

        // Host-neutral, like the vector layers and unlike the `revit.*` deliverables: the tree
        // points are the DEM's own raster-aligned product, published in the AOI's projected CRS
        // whatever the delivery tier asks the Revit artifacts to be cut in. That mismatch is real
        // and the planner fails closed on it rather than reconciling it here.
        manifest.TreePoints = BuildArtifact(
            manifest,
            LayoutTreePoints,
            root.Object("landcover")?.Object("tree_points"),
            detail => new BundleArtifact
            {
                Path = string.Empty,
                HorizontalFrame = detail?.OptionalStr("crs"),
                FootprintCount = detail?.OptionalInt("point_count"),
            });

        ReadImagery(manifest, root, elevation);
    }

    /// <summary>
    /// The satellite drape: its pointer, the two candidate extents, and the GSD that corroborates
    /// one of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three host-neutral blocks and not one host block. The drape's declared extent lives in
    /// <c>unreal.imagery_drape</c>, and this reader does not go there — the shared corpus states the
    /// host blocks in conflict on purpose, so a host that reaches into a sibling's is meant to fail
    /// (<c>HPS-36</c>). What it reads instead is <c>imagery.drape</c> when a bundle
    /// carries it, <c>elevation.dem</c>'s bounds when one does not, and <c>imagery.gsd_m</c> either
    /// way.
    /// </para>
    /// <para>
    /// Both extents are read whenever both are published. Choosing between them is the planner's
    /// call, not this reader's: the choice has a refusal attached to it, and refusals belong where a
    /// headless test can assert them (<c>HPS-02</c>).
    /// </para>
    /// </remarks>
    private static void ReadImagery(BundleManifest manifest, JsonElement root, JsonElement? elevation)
    {
        JsonElement? imagery = root.Object("imagery");

        manifest.ImageryGsdM = imagery?.OptionalDouble("gsd_m");

        // `present` is read only for its explicit `false`. An absent block leaves this alone, which
        // keeps "the ETL produced no imagery" distinct from "this manifest never mentioned imagery".
        manifest.ImageryAbsentByDeclaration = imagery is { } block && !block.Bool("present", fallback: true);

        JsonElement? drape = imagery?.Object("drape");

        manifest.ImageryDrape = BuildArtifact(
            manifest,
            LayoutImageryDrape,
            drape,
            detail => new BundleArtifact
            {
                Path = string.Empty,
                Format = detail?.OptionalStr("format"),
                HorizontalFrame = detail?.OptionalStr("extent_crs"),
            });

        manifest.ImageryDrapeExtent = ReadGroundExtent(drape, "extent", "extent_crs");
        manifest.DemBounds = ReadGroundExtent(elevation?.Object("dem"), "bounds_target_crs", "crs");
    }

    /// <summary>
    /// A <c>[left, bottom, right, top]</c> array plus the CRS field naming its coordinates, or
    /// <c>null</c> when either is missing or malformed.
    /// </summary>
    /// <remarks>
    /// An array of any length but four is <c>null</c> rather than partially read. A five-element
    /// extent is a producer whose meaning this host does not know, and taking the first four of it
    /// is how a guess gets mistaken for a reading.
    /// </remarks>
    private static GroundExtent? ReadGroundExtent(JsonElement? block, string arrayField, string crsField)
    {
        if (block is not { } element || element.Array(arrayField) is not { } bounds)
        {
            return null;
        }

        double[] values = new double[4];
        int count = 0;
        foreach (JsonElement item in bounds.EnumerateArray())
        {
            if (count == values.Length || item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double value))
            {
                return null;
            }

            values[count++] = value;
        }

        if (count != values.Length)
        {
            return null;
        }

        return new GroundExtent
        {
            Left = values[0],
            Bottom = values[1],
            Right = values[2],
            Top = values[3],
            Epsg = GeoProjection.TryParseEpsg(element.OptionalStr(crsField)) ?? 0,
        };
    }

    /// <summary>
    /// Resolves one artifact: path from the <c>layout</c> table, falling back to the detail block's
    /// own <c>path</c>; metadata and the optional sha256 from the detail block. No path from either
    /// pointer means the artifact is absent — this never guesses a well-known folder (HPS-32).
    /// </summary>
    /// <remarks>
    /// The two pointers can disagree. <c>layout</c> is schema-required; the <c>elevation.*</c> and
    /// <c>buildings.ifc</c> detail blocks still ride <c>additionalProperties</c> in v18, so producer
    /// drift between them is a live possibility rather than a hypothetical. When they name different
    /// files the <c>layout</c> path wins — it is the declared one — and the detail block's metadata
    /// is DISCARDED rather than transplanted onto a file it does not describe. Carrying a
    /// <c>units: "ftUS"</c> from one file over to another is a site imported 3.28× wrong with
    /// nothing on screen to suggest it (HPS-20: unknown, not assumed).
    /// </remarks>
    private static BundleArtifact? BuildArtifact(
        BundleManifest manifest,
        string layoutKey,
        JsonElement? detail,
        Func<JsonElement?, BundleArtifact> shape,
        JsonElement? hostDetail = null)
    {
        string layoutPath = manifest.Layout.GetValueOrDefault(layoutKey, string.Empty);
        string detailPath = detail?.Str("path") ?? string.Empty;

        string path = string.IsNullOrWhiteSpace(layoutPath) ? detailPath : layoutPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(detailPath) && !SamePath(detailPath, path))
        {
            detail = null;
        }

        BundleArtifact template = shape(detail);
        JsonElement? host = HostBlock(hostDetail, path);
        return new BundleArtifact
        {
            Path = path,
            Sha256 = host?.OptionalStr("sha256") ?? detail?.OptionalStr("sha256"),
            Format = template.Format,
            Units = host?.OptionalStr("units") ?? template.Units,
            VerticalDatum = template.VerticalDatum,
            HorizontalFrame = host?.OptionalStr("horizontal_frame") ?? template.HorizontalFrame,
            Georeference = template.Georeference,
            TriangleCount = template.TriangleCount,
            FootprintCount = template.FootprintCount,
        };
    }

    /// <summary>
    /// This host's own <c>hosts.revit.*</c> sub-object for an artifact, or <c>null</c> when there is
    /// no such block or it describes a different file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is where the deliverables' hashes live and, since MPB 1.0.0, where their metadata lives at
    /// all: that release emptied the generic <c>elevation.*</c> detail blocks, so a reader taking
    /// <c>units</c> and <c>horizontal_frame</c> from them alone now sees null for every artifact and
    /// falls back on a delivery-wide default. The host block is this host's own and is read in
    /// preference to the generic one (<c>HPS-33</c>, <c>HPS-36</c>) — the block's own
    /// <c>units_note</c> says so in as many words: <em>each artifact's own <c>units</c> describes
    /// that file</em>, and on the <c>local_ft</c> tier the artifacts and the origin genuinely differ.
    /// </para>
    /// <para>
    /// A block naming a different file is discarded whole, for the reason its sibling metadata is:
    /// checking one file's bytes against another file's hash reports a corruption that is not there,
    /// and carrying one file's units onto another is a site imported 3.28× wrong.
    /// </para>
    /// </remarks>
    private static JsonElement? HostBlock(JsonElement? hostDetail, string path)
    {
        if (hostDetail is not { } block)
        {
            return null;
        }

        string blockPath = block.Str("path");
        return blockPath.Length > 0 && !SamePath(blockPath, path) ? null : block;
    }

    /// <summary>
    /// Refuses a v19 bundle whose <c>revit</c> block declares a deliverable without its hash.
    /// </summary>
    /// <remarks>
    /// The schema lists <c>sha256</c> in the <c>required</c> set of each deliverable sub-object,
    /// so a present block without one is a producer bug and fails closed (HPS-34) — importing
    /// unverifiable bytes is worse than not importing. The sub-objects themselves remain
    /// optional — a bundle with no Revit deliverables selected is well-formed.
    /// </para>
    /// <para>
    /// This rule used to be version-gated at v19, because the floor still accepted v18, which
    /// published no Revit hashes at all, and an absent hash there had to stay <em>valid but
    /// unverified</em> rather than "corrupt" (HPS-27). The MPB 1.0.0 floor retires that gate
    /// entirely: every version this reader accepts publishes the hashes, so the condition was
    /// unconditionally true and a branch that can never be taken is worse than no branch — it
    /// reads as a live rule protecting a case that no longer exists.
    /// </remarks>
    private static string? CheckRevitHashes(BundleManifest manifest, JsonElement root)
    {
        if (RevitHostBlock(root) is not { } revit)
        {
            return null;
        }

        foreach (string key in RevitDeliverableKeys)
        {
            if (revit.Object(key) is { } deliverable && deliverable.OptionalStr("sha256") is null)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "hosts.revit.{0} has no sha256, and this bundle's manifest ({1}) is required to publish one. "
                    + "Re-download this AOI from your vault at mantle.place/vault — rebuilding it there "
                    + "re-cuts the bundle on the current pipeline.",
                    key,
                    manifest.Version);
            }
        }

        return null;
    }

    /// <summary>
    /// Refuses a bundle the ETL has not built any DCC formats for yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The discriminator is deliberately "has this bundle been materialized for <em>anyone</em>",
    /// not "does it have Revit content" — HPS-47, decided from the manifest's neutral signals:
    /// a non-empty <c>hosts</c> object, or a non-empty
    /// <c>vector.layers</c> array. Those are different questions and only the first one is a
    /// parse verdict: a materialized bundle with no Revit deliverables selected is a perfectly
    /// well-formed manifest, and the shared corpus requires every host to accept it
    /// (<c>manifest.materializationSignals</c>). The "there is nothing here for Revit" case is
    /// <see cref="BundleManifest.HasRevitContent"/>, and the reason lives in
    /// <c>hosts.revit.readiness</c> (HPS-36).
    /// </para>
    /// <para>
    /// A base, not-yet-materialized bundle carries no host block, no vector layers and no artifact
    /// pointers beyond the base tier — its top-level facts are still parsed and returned (HPS-37)
    /// so the vault join key and the Cesium streaming path survive the refusal.
    /// </para>
    /// </remarks>
    private static string? CheckMaterialized(BundleManifest manifest, JsonElement root)
    {
        // Own content is a shortcut, not the rule: content for this host implies materialized.
        // HPS-47 only forbids own content being the ONLY signal — the neutral checks below are
        // what keep a bundle materialized for someone else readable.
        if (manifest.HasRevitContent || manifest.LandXml is not null || manifest.ContoursDxf is not null)
        {
            return null;
        }

        // A non-empty `hosts` object means the bundle reached the readiness stage, so it IS
        // materialized — even when every readiness path inside reads `present: false`. That case is
        // not a parse failure, it is the case HPS-36 exists for: the manifest says WHY each
        // artifact is absent and the plugin must surface those reasons. Refusing here would throw
        // them away and leave the user with a generic "nothing here yet", which is the dead-end the
        // rule bans.
        //
        // At 1.0.0 this ONE check replaces the integer era's three (a known host block, a
        // `dcc_readiness` object, a host roster). Only whether the object has a key is read — no
        // host id is compared, so a bundle materialized solely for a host this plugin has never
        // heard of still answers "materialized".
        //
        // A key, not mere existence: an empty `hosts` object is base-tier scaffolding exactly as an
        // empty `vector.layers` array is.
        if (root.Object(HostsKey) is { } hosts && hosts.EnumerateObject().Any())
        {
            return null;
        }

        if (root.Object("vector")?.Array("layers") is { } layers && layers.GetArrayLength() > 0)
        {
            return null;
        }

        return "This bundle hasn't generated its DCC formats yet. Open your vault at mantle.place/vault, "
            + "choose the Revit deliverables (toposurface points, IFC site model, surface DXF) — or Generate "
            + "all — then re-download.";
    }

    /// <summary>Compares two manifest paths, tolerating separator style only.</summary>
    private static bool SamePath(string left, string right)
        => string.Equals(
            left.Replace('\\', '/').Trim(),
            right.Replace('\\', '/').Trim(),
            StringComparison.OrdinalIgnoreCase);

    private static BundleManifest Refuse(BundleManifest manifest, string error)
    {
        manifest.IsValid = false;
        manifest.Error = error;
        return manifest;
    }
}
