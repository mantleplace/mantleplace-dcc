using System.Globalization;
using System.Text.Json;

namespace MantlePlace.Core;

public static class BundleManifestReader
{
    public const string HostKey = "revit";

    internal const string NotJsonMessage = "manifest.json is not valid JSON.";

    private const string LayoutPointsCsv = "points_csv";
    private const string LayoutSurfaceDxf = "surface_dxf";
    private const string LayoutBuildingsIfc = "buildings_ifc";
    private const string LayoutLandXml = "landxml";
    private const string LayoutContours = "contours";
    private const string LayoutTreePoints = "tree_points";
    private const string LayoutImageryDrape = "imagery_drape";

    private const string LayoutProjectedVariants = "projected_variants";

    private const string LayoutFloodZones = "flood_zones";
    private const string LayoutSteepSlope = "steep_slope";

    internal const string GeographicFrame = "EPSG:4326";

    internal const string ProjectedFrame = "absolute_projected";

    internal const string LocalFrame = "local_enu";

    internal const string VectorCarrierKey = "gpkg";

    internal const string AbsoluteHeight = "absolute";

    private const string RevitToposurfacePoints = "toposurface_points";
    private const string RevitSurfaceDxf = "surface_dxf";
    private const string RevitIfcSite = "ifc_site";

    private const string RevitDrape = "drape";
    private const string RevitContours = "contours";

    private static readonly string[] RevitDeliverableKeys =
        [RevitToposurfacePoints, RevitSurfaceDxf, RevitIfcSite, RevitDrape, RevitContours];

    private const string HostsKey = "hosts";

    private static JsonElement? RevitHostBlock(JsonElement root) =>
        root.Object(HostsKey)?.Object(HostKey);

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

        manifest.Version = root.Str("version");
        manifest.OrderId = ReadOrderId(root);
        manifest.FilingKey = ReadFilingKey(root);

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

        if (parsedVersion.Major > ManifestVersions.NewestSupportedMajor)
        {
            return Refuse(
                manifest,
                ManifestVersions.NewerMajorMessage(manifest.Version, ManifestVersions.NewestSupportedMajor));
        }

        bool layoutTwo = parsedVersion.Major >= 2;

        string? refusal = null;

        manifest.DeliveryModel = root.Object("packaging")?.Str("delivery_model") ?? string.Empty;

        ReadBbox(manifest, root);
        ReadLayout(manifest, root);
        ReadSharedVectorLayers(manifest, root);
        ReadAttribution(manifest, root);
        ReadFileEntries(manifest, root);
        ReadTimeZone(manifest, root);
        refusal ??= ReadDelivery(manifest, root);
        ReadReadiness(manifest, root);
        ReadFileFrame(manifest, root);
        if (layoutTwo)
        {
            ReadProjectedTwins(manifest, root);
        }
        else
        {
            ReadOwnVectors(manifest, root);
        }

        ReadHazards(manifest, root, layoutTwo);
        ReadArtifacts(manifest, root);
        refusal ??= ReadGeoreference(manifest, root);

        manifest.SurveyPoint = manifest.Georeference.Origin is { IsUsable: true } ownOrigin
            ? ownOrigin
            : manifest.Delivery.LocalOrigin;

        refusal ??= CheckRevitHashes(manifest, root, layoutTwo);
        refusal ??= CheckMaterialized(manifest, root);

        if (refusal is not null)
        {
            return Refuse(manifest, refusal);
        }

        manifest.IsValid = true;
        return manifest;
    }

    private static string ReadOrderId(JsonElement root)
    {
        string orderId = root.Str("order_id");
        if (!string.IsNullOrEmpty(orderId))
        {
            return orderId;
        }

        return root.Object("attribution") is { } attribution && attribution.OptionalStr("job_id") is not null
            ? attribution.Str("order_id")
            : string.Empty;
    }

    private static string ReadFilingKey(JsonElement root)
    {
        string orderId = root.Str("order_id");
        return !string.IsNullOrEmpty(orderId)
            ? orderId
            : root.Object("attribution")?.Str("order_id") ?? string.Empty;
    }

    private static void ReadAttribution(BundleManifest manifest, JsonElement root)
    {
        if (root.Object("attribution")?.Array("sources") is not { } sources)
        {
            return;
        }

        List<AttributionSource> read = [];
        foreach (JsonElement entry in sources.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            AttributionSource source = AttributionSourceJson.Read(entry);

            if (source.ProviderId.Length > 0
                || source.AttributionText is not null
                || source.License is not null
                || source.LicenseUrl is not null)
            {
                read.Add(source);
            }
        }

        manifest.AttributionSources = read;
    }

    internal static void ReadFileEntries(BundleManifest manifest, JsonElement root, string member = "file_entries")
    {
        if (root.Object(member) is not { } entries)
        {
            return;
        }

        Dictionary<string, FileLicensing> read = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty entry in entries.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                read[entry.Name] = new FileLicensing();
                continue;
            }

            List<FileSource> sources = [];
            if (entry.Value.Array("sources") is { } listed)
            {
                foreach (JsonElement source in listed.EnumerateArray())
                {
                    string name = source.Str("name");
                    string license = source.Str("license");
                    if (name.Length > 0 || license.Length > 0)
                    {
                        sources.Add(new FileSource(name, license));
                    }
                }
            }

            read[entry.Name] = new FileLicensing
            {
                Role = OdblContent.ParseRole(entry.Value.OptionalStr("odbl_role")),
                License = entry.Value.Str("license"),
                Sources = sources,
            };
        }

        manifest.FileEntries = read;
    }

    private static void ReadTimeZone(BundleManifest manifest, JsonElement root)
    {
        if (root.Object("location")?.Object("time_zone") is not { } zone
            || zone.OptionalDouble("utc_offset_standard_h") is not { } offset
            || !double.IsFinite(offset))
        {
            return;
        }

        manifest.TimeZone = new PublishedTimeZone
        {
            Iana = zone.Str("iana"),
            UtcOffsetStandardH = offset,
            ObservesDst = zone.OptionalBool("observes_dst"),
        };
    }

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
        Dictionary<string, string> twins = new(StringComparer.OrdinalIgnoreCase);
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

            if (table.Object(LayoutProjectedVariants) is { } variants)
            {
                foreach (JsonProperty variant in variants.EnumerateObject())
                {
                    if (variant.Value.ValueKind == JsonValueKind.String
                        && variant.Value.GetString() is { } twin
                        && !string.IsNullOrWhiteSpace(twin))
                    {
                        twins[variant.Name] = twin;
                    }
                }
            }
        }

        manifest.Layout = layout;
        manifest.ProjectedVariants = twins;
        manifest.CesiumTerrainPath = layout.GetValueOrDefault("cesium_terrain", string.Empty);
    }

    private static void ReadSharedVectorLayers(BundleManifest manifest, JsonElement root)
    {
        HashSet<string> nothingToPlace = new(StringComparer.Ordinal);

        manifest.RoadSplines = ReadVectorLayer(root, SiteVectorLayers.RoadSplines, nothingToPlace);
        manifest.RoadSplinesPath = manifest.RoadSplines?.Path ?? string.Empty;
        manifest.RoadSplinesSha256 = manifest.RoadSplines?.Sha256 ?? string.Empty;
        manifest.HasRoadSplines = !string.IsNullOrEmpty(manifest.RoadSplinesPath);

        manifest.LandUse = ReadVectorLayer(root, SiteVectorLayers.LandUse, nothingToPlace);
        manifest.LandCover = ReadVectorLayer(root, SiteVectorLayers.LandCover, nothingToPlace);
        manifest.Water = ReadVectorLayer(root, SiteVectorLayers.Water, nothingToPlace);
        manifest.RoadPolygons = ReadVectorLayer(root, SiteVectorLayers.RoadPolygons, nothingToPlace);
        manifest.SharedLayersWithNothingToPlace = nothingToPlace;

        manifest.HasRoadLayer = HasVectorLayer(root, "road");
    }

    private static bool HasVectorLayer(JsonElement root, string layerName)
    {
        if (root.Object("vector")?.Array("layers") is not { } layers)
        {
            return false;
        }

        foreach (JsonElement layer in layers.EnumerateArray())
        {
            if (layer.ValueKind == JsonValueKind.Object
                && string.Equals(layer.Str("name"), layerName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static BundleArtifact? ReadVectorLayer(
        JsonElement root,
        SiteVectorLayer wanted,
        HashSet<string> nothingToPlace)
    {
        if (root.Object("vector")?.Array("layers") is not { } layers)
        {
            return null;
        }

        foreach (JsonElement layer in layers.EnumerateArray())
        {
            if (layer.ValueKind != JsonValueKind.Object
                || !string.Equals(layer.Str("name"), wanted.Name, StringComparison.Ordinal))
            {
                continue;
            }

            bool listedGeojson = false;
            bool wantedWithoutPath = false;
            BundleArtifact? wholeLayer = null;
            if (layer.Array("formats") is { } formats)
            {
                foreach (JsonElement format in formats.EnumerateArray())
                {
                    if (format.ValueKind != JsonValueKind.Object
                        || !string.Equals(format.Str("format"), "geojson", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    listedGeojson = true;
                    SiteGeometryKinds? holds = SiteGeometryFamilies.Holds(GeometryFamily(format));
                    if (holds is { } known && (known & wanted.DrawnFrom) == 0)
                    {
                        continue;
                    }

                    if (SharedLayerFile(format, layer.OptionalStr("vertical_datum")) is not { } file)
                    {
                        wantedWithoutPath = true;
                        continue;
                    }

                    if (holds is not null)
                    {
                        return file;
                    }

                    wholeLayer ??= file;
                }
            }

            if (wholeLayer is not null)
            {
                return wholeLayer;
            }

            if (listedGeojson && !wantedWithoutPath)
            {
                nothingToPlace.Add(wanted.Name);
            }

            break;
        }

        return null;
    }

    private static BundleArtifact? SharedLayerFile(JsonElement format, string? layerVerticalDatum)
    {
        string path = format.Str("path");
        return string.IsNullOrWhiteSpace(path)
            ? null
            : new BundleArtifact
            {
                Path = path,
                Sha256 = format.OptionalStr("sha256"),
                Format = "geojson",

                HorizontalFrame = GeographicFrame,
                VerticalDatum = layerVerticalDatum,
            };
    }

    private static string? GeometryFamily(JsonElement format)
        => format.TryGetProperty("geometry_family", out JsonElement family) && family.ValueKind == JsonValueKind.String
            ? family.GetString()
            : null;

    private static string? ReadDelivery(BundleManifest manifest, JsonElement root)
    {
        if (root.Object("delivery") is not { } delivery)
        {
            manifest.Delivery = new DeliveryFacts();
            return null;
        }

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
            UnitSystemValue = delivery.Str("unit_system"),
            Tier = delivery.Str("tier"),
            LinearUnit = linearUnit,
            HorizontalEpsg = delivery.OptionalInt("horizontal_epsg"),
            Label = delivery.OptionalStr("label"),
            LocalOrigin = localOrigin,
        };

        return null;
    }

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

    private static string UnsupportedLinearUnit(string field, string raw)
        => string.Format(
            CultureInfo.InvariantCulture,
            "Unsupported {0} \"{1}\": this plugin understands only \"m\", \"ftUS\" and \"ft\".",
            field,
            raw);

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
            Vectors = ReadReadinessPath(revit, "vectors"),
            Contours = ReadReadinessPath(revit, "contours"),
            FloodZones = ReadReadinessPath(revit, "flood_zones"),
            SteepGround = ReadReadinessPath(revit, "steep_slope"),
        };
    }

    private static void ReadFileFrame(BundleManifest manifest, JsonElement root)
    {
        if (RevitHostBlock(root)?.Object("file_frame") is not { } frame)
        {
            return;
        }

        FileFrameKind kind = frame.Str("type") switch
        {
            "projected" => FileFrameKind.Projected,
            "local" => FileFrameKind.Local,
            _ => FileFrameKind.Unknown,
        };

        GeoOrigin? origin = null;
        if (frame.Object("origin") is { } local)
        {
            bool unitRead = TryReadLinearUnit(local.Str("linear_unit"), out LinearUnit unit);
            origin = new GeoOrigin
            {
                Easting = unitRead ? local.OptionalDouble("easting") : null,
                Northing = unitRead ? local.OptionalDouble("northing") : null,
                LinearUnit = unit,
            };
        }

        manifest.FileFrame = new FileFrame
        {
            Kind = kind,
            Epsg = GeoProjection.TryParseEpsg(frame.OptionalStr(kind == FileFrameKind.Local ? "base_crs" : "crs")),
            Origin = origin,
            HorizontalUnit = TryReadLinearUnit(frame.Str("horizontal_unit"), out LinearUnit horizontal)
                && horizontal != LinearUnit.Unspecified
                    ? horizontal
                    : null,
            VerticalUnit = TryReadLinearUnit(frame.Str("vertical_unit"), out LinearUnit vertical)
                && vertical != LinearUnit.Unspecified
                    ? vertical
                    : null,
        };
    }

    private static void ReadOwnVectors(BundleManifest manifest, JsonElement root)
    {
        if (RevitHostBlock(root)?.Object("vectors") is not { } vectors || !HasOwnVectorLayers(vectors))
        {
            return;
        }

        ReadinessPath shared = manifest.Readiness.Vectors;
        if (shared.Declared && !shared.Present)
        {
            return;
        }

        manifest.VectorsFromOwnBlock = true;

        manifest.RoadSplines = ReadOwnVectorLayer(vectors, SiteVectorLayers.RoadSplines.Name);
        manifest.LandUse = ReadOwnVectorLayer(vectors, SiteVectorLayers.LandUse.Name);
        manifest.LandCover = ReadOwnVectorLayer(vectors, SiteVectorLayers.LandCover.Name);
        manifest.Water = ReadOwnVectorLayer(vectors, SiteVectorLayers.Water.Name);
        manifest.RoadPolygons = ReadOwnVectorLayer(vectors, SiteVectorLayers.RoadPolygons.Name);
    }

    private static bool HasOwnVectorLayers(JsonElement vectors)
        => vectors.Array("layers") is not null || vectors.Object(VectorCarrierKey) is not null;

    private static void ReadHazards(BundleManifest manifest, JsonElement root, bool layoutTwo)
    {
        if (layoutTwo)
        {
            manifest.FloodZones = LayoutGeoPackage(manifest, LayoutFloodZones, SiteVectorLayers.FloodZones.Name);
            manifest.SteepGround = LayoutGeoPackage(manifest, LayoutSteepSlope, SiteVectorLayers.SteepGround.Name);
        }
        else if (RevitHostBlock(root)?.Object("vectors") is { } vectors && HasOwnVectorLayers(vectors))
        {
            manifest.FloodZones = ReadOwnVectorLayer(vectors, SiteVectorLayers.FloodZones.Name);
            manifest.SteepGround = ReadOwnVectorLayer(vectors, SiteVectorLayers.SteepGround.Name);
        }

        if (root.Object("flood")?.Object("nfhl") is { } nfhl)
        {
            List<FloodPanel> panels = [];
            if (nfhl.Array("panels") is { } published)
            {
                foreach (JsonElement panel in published.EnumerateArray())
                {
                    if (panel.ValueKind == JsonValueKind.Object && panel.OptionalStr("firm_pan") is { } number)
                    {
                        panels.Add(new FloodPanel(number, panel.OptionalStr("effective_date")));
                    }
                }
            }

            manifest.FloodMap = new FloodMap
            {
                Zones = nfhl.StringArray("zones"),
                DfirmIds = nfhl.StringArray("dfirm_ids"),
                Panels = panels,
                Source = nfhl.Str("source"),
            };
        }

        if (root.Object("elevation")?.Object("steep_slope") is { } steep)
        {
            manifest.HasSteepGroundBlock = true;
            manifest.SteepGroundThreshold = steep.RawNumber("threshold_deg");
        }
    }

    private static void ReadProjectedTwins(BundleManifest manifest, JsonElement root)
    {
        if (manifest.ProjectedVariants.Count == 0)
        {
            return;
        }

        manifest.RoadSplines = ProjectedTwin(manifest, root, SiteVectorLayers.RoadSplines, manifest.RoadSplines);
        manifest.RoadSplinesPath = manifest.RoadSplines?.Path ?? string.Empty;
        manifest.RoadSplinesSha256 = manifest.RoadSplines?.Sha256 ?? string.Empty;
        manifest.HasRoadSplines = !string.IsNullOrEmpty(manifest.RoadSplinesPath);

        manifest.LandUse = ProjectedTwin(manifest, root, SiteVectorLayers.LandUse, manifest.LandUse);
        manifest.LandCover = ProjectedTwin(manifest, root, SiteVectorLayers.LandCover, manifest.LandCover);
        manifest.Water = ProjectedTwin(manifest, root, SiteVectorLayers.Water, manifest.Water);
        manifest.RoadPolygons = ProjectedTwin(manifest, root, SiteVectorLayers.RoadPolygons, manifest.RoadPolygons);
    }

    private static BundleArtifact? ProjectedTwin(
        BundleManifest manifest,
        JsonElement root,
        SiteVectorLayer wanted,
        BundleArtifact? geoJson)
    {
        if (root.Object("vector")?.Array("layers") is not { } layers)
        {
            return geoJson;
        }

        foreach (JsonElement layer in layers.EnumerateArray())
        {
            if (layer.ValueKind != JsonValueKind.Object
                || !string.Equals(layer.Str("name"), wanted.Name, StringComparison.Ordinal))
            {
                continue;
            }

            if (layer.Array("formats") is not { } formats)
            {
                break;
            }

            foreach (JsonElement format in formats.EnumerateArray())
            {
                if (format.ValueKind != JsonValueKind.Object
                    || !string.Equals(format.Str("format"), "gpkg", StringComparison.Ordinal))
                {
                    continue;
                }

                SiteGeometryKinds? holds = SiteGeometryFamilies.Holds(GeometryFamily(format));
                if (holds is { } known && (known & wanted.DrawnFrom) == 0)
                {
                    continue;
                }

                string original = format.Str("path");
                if (string.IsNullOrWhiteSpace(original)
                    || !manifest.ProjectedVariants.TryGetValue(original.Trim(), out string? twin)
                    || TwinTable(format, wanted) is not { } table)
                {
                    continue;
                }

                bool listsTables = format.StringArray("tables").Count > 0;

                return new BundleArtifact
                {
                    Path = twin,
                    Format = "gpkg",
                    Table = table,
                    SoleTable = !listsTables,
                    HorizontalFrame = ProjectedFrame,
                    VerticalReference = AbsoluteHeight,
                    VerticalDatum = layer.OptionalStr("vertical_datum"),
                    Levels = geoJson?.Levels,
                    GeoJsonFallback = geoJson,
                };
            }

            break;
        }

        return geoJson;
    }

    private static string? TwinTable(JsonElement format, SiteVectorLayer wanted)
    {
        IReadOnlyList<string> tables = format.StringArray("tables");
        if (tables.Count == 0)
        {
            return wanted.Name;
        }

        string family = wanted.DrawnFrom == SiteGeometryKinds.Lines ? "_line" : "_polygon";
        return tables.FirstOrDefault(name => string.Equals(name, wanted.Name, StringComparison.OrdinalIgnoreCase))
            ?? tables.FirstOrDefault(name => name.EndsWith(family, StringComparison.OrdinalIgnoreCase));
    }

    private static BundleArtifact? LayoutGeoPackage(BundleManifest manifest, string layoutKey, string table)
    {
        string path = manifest.Layout.GetValueOrDefault(layoutKey, string.Empty);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return new BundleArtifact
        {
            Path = manifest.ProjectedVariants.TryGetValue(path.Trim(), out string? twin) ? twin : path,
            Format = "gpkg",
            Table = table,
            SoleTable = true,
            HorizontalFrame = ProjectedFrame,
            VerticalReference = AbsoluteHeight,
        };
    }

    private static BundleArtifact? ReadOwnVectorLayer(JsonElement vectors, string layerName)
    {
        BundleArtifact? geoJson = ReadOwnGeoJsonLayer(vectors, layerName);
        return ReadCarrierLayer(vectors, layerName, geoJson) ?? geoJson;
    }

    private static BundleArtifact? ReadOwnGeoJsonLayer(JsonElement vectors, string layerName)
    {
        if (vectors.Array("layers") is not { } layers)
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

            return OwnLayerFile(layer, FidelityLevelsReader.Read(layer.Object("levels"), level => OwnLayerFile(level)));
        }

        return null;
    }

    private static BundleArtifact? ReadCarrierLayer(JsonElement vectors, string layerName, BundleArtifact? geoJson)
    {
        if (vectors.Object(VectorCarrierKey) is not { } carrier)
        {
            return null;
        }

        string path = carrier.Str("path");
        string? sha256 = carrier.OptionalStr("sha256");
        string? table = carrier.StringArray("tables")
            .FirstOrDefault(name => string.Equals(name, layerName, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sha256) || table is null)
        {
            return null;
        }

        return new BundleArtifact
        {
            Path = path,
            Sha256 = sha256,
            Format = "gpkg",
            Table = table,
            HorizontalFrame = ProjectedFrame,
            VerticalReference = AbsoluteHeight,
            FromOwnBlock = true,
            Levels = geoJson?.Levels ?? FidelityLevelsReader.Read(carrier.Object("levels"), level => OwnLayerFile(level)),
            GeoJsonFallback = geoJson,
        };
    }

    private static BundleArtifact? OwnLayerFile(JsonElement pointer, FidelityLevels? levels = null)
    {
        string path = pointer.Str("path");
        return string.IsNullOrWhiteSpace(path)
            ? null
            : new BundleArtifact
            {
                Path = path,
                Sha256 = pointer.OptionalStr("sha256"),
                Format = "geojson",
                HorizontalFrame = pointer.Str("horizontal_frame"),
                Units = pointer.Str("units"),
                VerticalReference = pointer.OptionalStr("vertical_reference"),
                FromOwnBlock = true,
                Levels = levels,
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
                VerticalDatum = detail?.OptionalStr("vertical_datum"),
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

        manifest.TreePoints = BuildArtifact(
            manifest,
            LayoutTreePoints,
            root.Object("landcover")?.Object("tree_points"),
            detail => new BundleArtifact
            {
                Path = string.Empty,
                HorizontalFrame = detail?.OptionalStr("crs"),
                Units = detail?.OptionalStr("units"),
                HorizontalUnits = detail?.OptionalStr("horizontal_units"),
                FootprintCount = detail?.OptionalInt("point_count"),
                FoliageTypeVocabulary = detail?.OptionalStr("foliage_type_vocabulary"),
                VerticalDatum = detail?.OptionalStr("vertical_datum"),
            });

        ReadImagery(manifest, root, elevation);
    }

    private static void ReadImagery(BundleManifest manifest, JsonElement root, JsonElement? elevation)
    {
        JsonElement? imagery = root.Object("imagery");

        manifest.ImageryGsdM = imagery?.OptionalDouble("gsd_m");

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
            },
            levelExtent: DrapeLevelExtent);

        manifest.ImageryDrapeExtent = ReadGroundExtent(drape, "extent", "extent_crs");
        manifest.DemBounds = ReadGroundExtent(elevation?.Object("dem"), "bounds_target_crs", "crs");

        if (RevitHostBlock(root)?.Object("drape") is { } own
            && own.Str("path") is { Length: > 0 } ownPath)
        {
            manifest.RevitDrape = OwnDrapeFile(
                own,
                FidelityLevelsReader.Read(own.Object("levels"), level => OwnDrapeFile(level), DrapeLevelExtent));
            manifest.RevitDrapeExtent = ReadGroundExtent(own, "extent", "extent_crs");
        }

        if (RevitHostBlock(root)?.Object(RevitContours) is { } contours
            && contours.Str("path") is { Length: > 0 } contoursPath)
        {
            manifest.RevitContours = OwnContoursFile(
                contours,
                FidelityLevelsReader.Read(contours.Object("levels"), level => OwnContoursFile(level)));
        }
    }

    private static BundleArtifact? OwnDrapeFile(JsonElement pointer, FidelityLevels? levels = null)
        => pointer.Str("path") is { Length: > 0 } path
            ? new BundleArtifact
            {
                Path = path,
                Sha256 = pointer.OptionalStr("sha256"),
                Format = pointer.OptionalStr("format"),
                Units = pointer.Str("units"),
                HorizontalFrame = pointer.OptionalStr("extent_crs"),
                FromOwnBlock = true,
                Levels = levels,
            }
            : null;

    private static GroundExtent? DrapeLevelExtent(JsonElement pointer) => ReadGroundExtent(pointer, "extent", "extent_crs");

    private static BundleArtifact? OwnContoursFile(JsonElement pointer, FidelityLevels? levels = null)
        => pointer.Str("path") is { Length: > 0 } path
            ? new BundleArtifact
            {
                Path = path,
                Sha256 = pointer.OptionalStr("sha256"),
                Units = pointer.Str("units"),
                HorizontalUnits = pointer.OptionalStr("horizontal_units"),
                VerticalUnits = pointer.OptionalStr("vertical_units"),
                HorizontalFrame = pointer.Str("horizontal_frame"),
                VerticalReference = pointer.OptionalStr("vertical_reference"),
                VerticalDatum = pointer.OptionalStr("vertical_datum"),
                FromOwnBlock = true,
                Levels = levels,
            }
            : null;

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

    private static BundleArtifact? BuildArtifact(
        BundleManifest manifest,
        string layoutKey,
        JsonElement? detail,
        Func<JsonElement?, BundleArtifact> shape,
        JsonElement? hostDetail = null,
        Func<JsonElement, GroundExtent?>? levelExtent = null)
    {
        string layoutPath = manifest.Layout.GetValueOrDefault(layoutKey, string.Empty);
        string detailPath = detail?.Str("path") ?? string.Empty;
        string hostPath = hostDetail?.Str("path") ?? string.Empty;

        string neutralPath = string.IsNullOrWhiteSpace(layoutPath) ? detailPath : layoutPath;
        string path = string.IsNullOrWhiteSpace(hostPath) ? neutralPath : hostPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string described = string.IsNullOrWhiteSpace(detailPath) ? neutralPath : detailPath;
        if (!SamePath(described, path))
        {
            detail = null;
        }

        BundleArtifact template = shape(detail);

        JsonElement? levelsOwner = hostDetail ?? detail;

        BundleArtifact? Pointer(JsonElement level)
        {
            string levelPath = level.Str("path");
            if (string.IsNullOrWhiteSpace(levelPath))
            {
                return null;
            }

            BundleArtifact own = shape(level);
            return new BundleArtifact
            {
                Path = levelPath,
                Sha256 = level.OptionalStr("sha256"),
                Format = own.Format,
                Units = level.OptionalStr("units") ?? own.Units,
                VerticalDatum = own.VerticalDatum,
                HorizontalFrame = hostDetail is null ? own.HorizontalFrame : level.OptionalStr("horizontal_frame") ?? own.HorizontalFrame,
                Georeference = own.Georeference,
                TriangleCount = own.TriangleCount,
                FootprintCount = own.FootprintCount,
                FoliageTypeVocabulary = own.FoliageTypeVocabulary ?? template.FoliageTypeVocabulary,
                HorizontalUnits = own.HorizontalUnits,
                NamedByOwnBlock = hostDetail is not null,
            };
        }

        return new BundleArtifact
        {
            Path = path,
            Sha256 = hostDetail?.OptionalStr("sha256") ?? detail?.OptionalStr("sha256"),
            Format = template.Format,
            Units = hostDetail?.OptionalStr("units") ?? template.Units,
            VerticalDatum = template.VerticalDatum,
            HorizontalFrame = hostDetail?.OptionalStr("horizontal_frame") ?? template.HorizontalFrame,
            Georeference = template.Georeference,
            TriangleCount = template.TriangleCount,
            FootprintCount = template.FootprintCount,
            FoliageTypeVocabulary = template.FoliageTypeVocabulary,
            HorizontalUnits = template.HorizontalUnits,
            NamedByOwnBlock = hostDetail is not null,
            Levels = FidelityLevelsReader.Read(levelsOwner?.Object("levels"), Pointer, levelExtent),
        };
    }

    private static string? CheckRevitHashes(BundleManifest manifest, JsonElement root, bool layoutTwo)
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

        if (layoutTwo)
        {
            return null;
        }

        if (revit.Object("vectors")?.Object(VectorCarrierKey) is { } carrier && carrier.OptionalStr("sha256") is null)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "hosts.revit.vectors.gpkg has no sha256, and this bundle's manifest ({0}) is required to publish "
                + "one. Re-download this AOI from your vault at mantle.place/vault — rebuilding it there "
                + "re-cuts the bundle on the current pipeline.",
                manifest.Version);
        }

        if (revit.Object("vectors")?.Array("layers") is { } layers)
        {
            foreach (JsonElement layer in layers.EnumerateArray())
            {
                if (layer.ValueKind == JsonValueKind.Object && layer.OptionalStr("sha256") is null)
                {
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "hosts.revit.vectors layer \"{0}\" has no sha256, and this bundle's manifest ({1}) is "
                        + "required to publish one. Re-download this AOI from your vault at mantle.place/vault — "
                        + "rebuilding it there re-cuts the bundle on the current pipeline.",
                        layer.Str("name"),
                        manifest.Version);
                }
            }
        }

        return null;
    }

    private static string? CheckMaterialized(BundleManifest manifest, JsonElement root)
    {
        if (manifest.HasRevitContent || manifest.LandXml is not null || manifest.ContoursDxf is not null)
        {
            return null;
        }

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
