using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The Revit host reads its vector layers from the shared carrier: the manifest pointer finds it
/// (<c>hosts.revit.vectors.gpkg</c>), each layer is the table of its own name, and a bundle with no
/// carrier — or a layer the carrier cannot serve — keeps the GeoJSON file it always had.
/// </summary>
/// <remarks>
/// <para>
/// The carrier is a GeoPackage in the order's own CRS, absolute, on every tier
/// (<c>revit/docs/vector-carrier.md</c>), so a step reading it is placed against the origin in the
/// origin's unit, not in the Revit files' frame. Where that differs — the <c>local_ft</c> tier, whose
/// GeoJSON is foot offsets while the carrier is absolute metres — the plan holds both, the carrier
/// first and the GeoJSON as the fallback.
/// </para>
/// </remarks>
internal static class VectorCarrierPlanTests
{
    private const string CarrierPath = "Site/Vector/SiteVectors.gpkg";
    private const string CarrierSha = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string OwnSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly ImportStepKind[] VectorKinds =
    [
        ImportStepKind.RoadCentrelines,
        ImportStepKind.LandUse,
        ImportStepKind.LandCover,
        ImportStepKind.Water,
        ImportStepKind.RoadPolygons,
    ];

    private static readonly string[] VectorNames = ["road_splines", "land_use", "land_cover", "water", "road_polygons"];

    /// <summary>Every table the producer writes: the Revit layers, the lines of water, and the footprints.</summary>
    private static readonly string[] AllTables =
        [.. VectorNames, "water_line", "flood_zones", "steep_slope", "building_footprints"];

    internal static int Run()
    {
        TestRun run = new();

        RunPointerCases(run);
        RunFrameCases(run);
        RunFallbackCases(run);
        RunRefusalCases(run);

        return run.Report("vector carrier plan");
    }

    private static void RunPointerCases(TestRun run)
    {
        run.Case("the manifest pointer finds the carrier, and each layer reads the table of its own name", () =>
        {
            BundleImportPlan plan = Plan(Manifest(FootGeoreference, FootFileFrame, Carrier(AllTables)));

            for (int index = 0; index < VectorKinds.Length; index++)
            {
                ImportStep? step = Find(plan, VectorKinds[index]);
                run.True(step is not null, $"{VectorKinds[index]} is planned");
                run.Equal(step?.EntryName, CarrierPath, $"{VectorKinds[index]} reads the carrier the pointer names");
                run.Equal(step?.Table, VectorNames[index], $"{VectorKinds[index]} reads its own table");
                run.Equal(step?.ExpectedSha256, CarrierSha, $"{VectorKinds[index]} verifies the carrier's hash");
                run.True(step?.GeoJsonFallback is null, $"{VectorKinds[index]} has no other file to fall back to");
            }
        });

        run.Case("a carrier is placed by subtraction from the origin, in the origin's unit, with a Z in the same unit", () =>
        {
            ImportStep roads = Find(Plan(Manifest(FootGeoreference, FootFileFrame, Carrier(AllTables))), ImportStepKind.RoadCentrelines)!;

            run.True(
                roads.Layer == new LayerFrame(LayerCoordinates.AbsoluteProjected, LinearUnit.UsSurveyFoot),
                "State Plane feet, absolute");
            run.True(roads.Frame is not null, "the origin it is subtracted from");
            run.Equal(roads.Units, LinearUnit.UsSurveyFoot, "the step's unit is the layer's");

            ImportStep metric = Find(Plan(Manifest(MetricGeoreference, MetricFileFrame, Carrier(AllTables))), ImportStepKind.Water)!;
            run.True(
                metric.Layer == new LayerFrame(LayerCoordinates.AbsoluteProjected, LinearUnit.Metre),
                "a metric order is the same path in metres");
        });

        run.Case("a layer is matched to its table without regard to case, as SQLite matches names", () =>
        {
            BundleImportPlan plan = Plan(Manifest(MetricGeoreference, MetricFileFrame, Carrier(["Road_Splines", "WATER"])));

            run.Equal(Find(plan, ImportStepKind.RoadCentrelines)?.Table, "Road_Splines", "the table as the pointer spells it");
            run.Equal(Find(plan, ImportStepKind.Water)?.Table, "WATER", "the table as the pointer spells it");
        });

        run.Case("the hazard layers are read from the carrier too", () =>
        {
            BundleImportPlan plan = Plan(Manifest(MetricGeoreference, MetricFileFrame, Carrier(AllTables), hazards: true));

            run.Equal(Find(plan, ImportStepKind.FloodZones)?.Table, "flood_zones", "flood zones");
            run.Equal(Find(plan, ImportStepKind.SteepGround)?.Table, "steep_slope", "steep ground");
            run.Equal(Find(plan, ImportStepKind.SteepGround)?.Hazard?.Threshold, "35.0", "the manifest's threshold still rides along");
        });

        run.Case("a carrier-only block, with no layers array, is a vector copy and is not read as the shared set", () =>
        {
            BundleManifest manifest = Parse(Manifest(MetricGeoreference, MetricFileFrame, Carrier(AllTables)));

            run.True(manifest.VectorsFromOwnBlock, "the block's copy, so an absent layer is 'none in this area'");
            run.True(manifest.RoadSplines is { IsGeoPackage: true }, "road splines are a carrier layer");
            run.True(manifest.RoadSplines?.GeoJsonFallback is null, "and have no GeoJSON beside them");
        });

        run.Case("the carrier's table list is the layers it holds: a layer it lacks is none in this area", () =>
        {
            BundleImportPlan plan = Plan(Manifest(MetricGeoreference, MetricFileFrame, Carrier(["road_splines", "water"])));

            run.True(Has(plan, ImportStepKind.RoadCentrelines), "a listed layer is planned");
            run.False(Has(plan, ImportStepKind.LandUse), "an unlisted layer is not");
            run.Contains(Skip(plan, ImportStepKind.LandUse)?.Reason, "none in this area", "and says why, in the curator's words");
        });

        run.Case("a bundle with no carrier keeps reading its GeoJSON files, as before", () =>
        {
            BundleImportPlan plan = Plan(Manifest(FootGeoreference, FootFileFrame, GeoJsonLayers("absolute_projected", "ftUS", VectorNames)));

            for (int index = 0; index < VectorKinds.Length; index++)
            {
                ImportStep? step = Find(plan, VectorKinds[index]);
                run.Equal(step?.EntryName, OwnPath(VectorNames[index]), $"{VectorKinds[index]} reads its GeoJSON file");
                run.True(step?.Table is null, $"{VectorKinds[index]} names no table");
                run.True(step?.GeoJsonFallback is null, $"{VectorKinds[index]} has nothing to fall back to");
            }
        });

        run.Case("a layer the carrier does not list is read from its GeoJSON file, and the rest from the carrier", () =>
        {
            string vectors = Carrier(["road_splines"], GeoJsonLayerList("absolute_projected", "m", ["road_splines", "water"]));
            BundleImportPlan plan = Plan(Manifest(MetricGeoreference, MetricFileFrame, vectors));

            run.Equal(Find(plan, ImportStepKind.RoadCentrelines)?.Table, "road_splines", "the carrier's layer");
            run.Equal(Find(plan, ImportStepKind.Water)?.EntryName, OwnPath("water"), "the layer the carrier lacks");
            run.True(Find(plan, ImportStepKind.Water)?.Table is null, "from a file of its own");
        });
    }

    private static void RunFrameCases(TestRun run)
    {
        run.Case("on the local_ft tier the carrier is absolute metres while the GeoJSON is foot offsets, and the plan holds both", () =>
        {
            string vectors = Carrier(AllTables, GeoJsonLayerList("local_enu", "ft", VectorNames));
            BundleImportPlan plan = Plan(Manifest(MetricGeoreference, LocalFootFileFrame, vectors));

            ImportStep? roads = Find(plan, ImportStepKind.RoadCentrelines);
            run.True(
                roads?.Layer == new LayerFrame(LayerCoordinates.AbsoluteProjected, LinearUnit.Metre),
                "the carrier, absolute, in the origin's own unit and not the files' feet");

            ImportStep? fallback = roads?.GeoJsonFallback;
            run.True(fallback is not null, "the GeoJSON is planned beside it");
            run.Equal(fallback?.EntryName, OwnPath("road_splines"), "the layer's own file");
            run.True(
                fallback?.Layer == new LayerFrame(LayerCoordinates.LocalOffsets, LinearUnit.InternationalFoot),
                "in its own frame, foot offsets, with every refusal the GeoJSON always met");
            run.True(fallback?.Table is null, "and no table");
            run.Equal(fallback?.ExpectedSha256, OwnSha, "verified by its own hash");
        });

        run.Case("a carrier needs no declared file frame: the file states its CRS and the origin is what it is placed against", () =>
        {
            BundleImportPlan plan = Plan(Manifest(FootGeoreference, fileFrame: null, Carrier(AllTables)));

            run.True(Has(plan, ImportStepKind.RoadCentrelines), "planned, where the GeoJSON would be refused for lack of a frame");
        });

        run.Case("a bundle publishing no origin places no carrier layer, rather than dropping it at the project origin", () =>
        {
            string noOrigin = """
                "georeference": { "crs_projected": "EPSG:32613" }
                """;
            BundleImportPlan plan = Plan(Manifest(noOrigin, fileFrame: null, Carrier(AllTables)));

            run.False(Has(plan, ImportStepKind.RoadCentrelines), "nothing is planned");
            run.True(
                Skip(plan, ImportStepKind.RoadCentrelines)?.ReasonCode == SkipReasonCode.NoSiteFrame,
                "refused as having no frame");
        });
    }

    private static void RunFallbackCases(TestRun run)
    {
        run.Case("a carrier the archive lacks does not take its layers with it when their GeoJSON files are there", () =>
        {
            string vectors = Carrier(AllTables, GeoJsonLayerList("absolute_projected", "m", VectorNames));
            BundleImportPlan plan = Plan(
                Manifest(MetricGeoreference, MetricFileFrame, vectors),
                Entries.Where(entry => entry != CarrierPath));

            ImportStep? roads = Find(plan, ImportStepKind.RoadCentrelines);
            run.Equal(roads?.EntryName, OwnPath("road_splines"), "the GeoJSON file is the step");
            run.True(roads?.Table is null, "read whole");
            run.True(Skip(plan, ImportStepKind.RoadCentrelines) is null, "nothing is skipped");
        });

        run.Case("a carrier the archive lacks, with no GeoJSON beside it, is skipped by name", () =>
        {
            BundleImportPlan plan = Plan(
                Manifest(MetricGeoreference, MetricFileFrame, Carrier(AllTables)),
                Entries.Where(entry => entry != CarrierPath));

            run.False(Has(plan, ImportStepKind.RoadCentrelines), "nothing is planned");
            run.True(
                Skip(plan, ImportStepKind.RoadCentrelines)?.ReasonCode == SkipReasonCode.EntryNotInArchive,
                "the entry is missing from the archive");
            run.Contains(Skip(plan, ImportStepKind.RoadCentrelines)?.Reason, CarrierPath, "naming the file");
        });

        run.Case("a GeoJSON file that would be refused is not a fallback, and the carrier still stands", () =>
        {
            string vectors = Carrier(AllTables, GeoJsonLayerList("absolute_projected", "chain", VectorNames));
            ImportStep? roads = Find(Plan(Manifest(MetricGeoreference, MetricFileFrame, vectors)), ImportStepKind.RoadCentrelines);

            run.Equal(roads?.Table, "road_splines", "the carrier is the step");
            run.True(roads?.GeoJsonFallback is null, "the refused file is not offered as a fallback");
        });

        run.Case("a fidelity level the layer's GeoJSON entry publishes applies to its carrier table", () =>
        {
            string vectors = Carrier(["road_polygons"], GeoJsonLayerList("absolute_projected", "m", ["road_polygons"], RoadLevels));
            BundleManifest manifest = Parse(Manifest(MetricGeoreference, MetricFileFrame, vectors));

            run.True(manifest.RoadPolygons is { IsGeoPackage: true, Levels: not null }, "the carrier layer keeps the entry's levels");
            ImportStep? step = Find(
                BundleImportPlanner.Plan(manifest, Entries, _ => new ImageSize(4000, 3000)),
                ImportStepKind.RoadPolygons);
            run.Equal(step?.Table, "road_polygons", "MAX reads the table");
            run.True(step?.Levels is not null, "the window still offers the levels");
        });
    }

    private static void RunRefusalCases(TestRun run)
    {
        run.Case("a carrier pointer with no sha256 refuses the bundle, as every Revit deliverable does", () =>
        {
            string vectors = Carrier(AllTables).Replace($", \"sha256\": \"{CarrierSha}\"", string.Empty, StringComparison.Ordinal);
            BundleManifest manifest = BundleManifestReader.Parse(Manifest(MetricGeoreference, MetricFileFrame, vectors));

            run.False(manifest.IsValid, "a carrier this host cannot verify is not read");
            run.Contains(manifest.Error, "hosts.revit.vectors.gpkg", "naming the pointer");
        });

        run.Case("a withheld copy leaves the shared set in place and the carrier unread", () =>
        {
            BundleManifest manifest = Parse(
                Manifest(MetricGeoreference, MetricFileFrame, Carrier(AllTables), vectorsReadiness: "{ \"present\": false, \"reason\": \"not_produced\" }"));

            run.False(manifest.VectorsFromOwnBlock, "the block's copy was not produced");
            run.True(manifest.RoadSplines is null, "so no layer is taken from it");
        });
    }

    private static string Carrier(string[] tables, string? layers = null)
    {
        string list = string.Join(", ", tables.Select(table => $"\"{table}\""));
        string geoJson = layers is null ? string.Empty : $", \"format\": \"geojson\", \"layers\": [ {layers} ]";
        return $$"""
            "vectors": { "gpkg": { "path": "{{CarrierPath}}", "tables": [ {{list}} ], "sha256": "{{CarrierSha}}", "size_bytes": 1048576 }{{geoJson}} }
            """;
    }

    private static string GeoJsonLayers(string horizontalFrame, string units, string[] names)
        => $$"""
            "vectors": { "format": "geojson", "layers": [ {{GeoJsonLayerList(horizontalFrame, units, names)}} ] }
            """;

    private static string GeoJsonLayerList(string horizontalFrame, string units, string[] names, string? extra = null)
        => string.Join(
            ", ",
            names.Select(name =>
                $$"""
                { "name": "{{name}}", "path": "{{OwnPath(name)}}", "horizontal_frame": "{{horizontalFrame}}",
                  "units": "{{units}}", "feature_count": 1, "sha256": "{{OwnSha}}"{{(extra is null ? string.Empty : ", " + extra)}} }
                """));

    /// <summary>A road-surface entry's levels: MAX and a field cut for MED and MIN, RAW the same as MAX.</summary>
    private const string RoadLevels = """
        "levels": {
          "RAW": { "same_as": "MAX", "reason": "not_derived" },
          "MAX": { "cost_driver": { "unit": "cuts", "count": 40 } },
          "MED": { "cut": { "lowest_level_field": "lowest_level" }, "cost_driver": { "unit": "cuts", "count": 20 } },
          "MIN": { "cut": { "lowest_level_field": "lowest_level" }, "cost_driver": { "unit": "cuts", "count": 10 } }
        }
        """;

    private static string OwnPath(string name) => $"Site/Vector/{name}.geojson";

    private static readonly string[] Entries =
    [
        "Metadata/manifest.json",
        CarrierPath,
        .. VectorNames.Select(OwnPath),
        OwnPath("flood_zones"),
        OwnPath("steep_slope"),
    ];

    private const string VectorsPresent = "{ \"present\": true }";

    private static string Manifest(
        string georeference,
        string? fileFrame,
        string vectors,
        string vectorsReadiness = VectorsPresent,
        bool hazards = false)
    {
        string readiness = "\"toposurface_points\": { \"present\": false, \"reason\": \"not_produced\" }, \"vectors\": " + vectorsReadiness
            + (hazards ? ", \"flood_zones\": { \"present\": true }, \"steep_slope\": { \"present\": true }" : string.Empty);

        string revit = string.Join(
            ",\n",
            new[] { georeference, fileFrame, vectors, $"\"readiness\": {{ {readiness} }}" }.Where(member => member is not null));

        string blocks = hazards
            ? """
                , "flood": { "nfhl": { "path": "Flood/FloodZones.gpkg", "source": "fema-nfhl", "license": "public-domain",
                    "feature_count": 65, "zones": ["AE", "X"], "dfirm_ids": ["28049C"], "panels": [],
                    "sha256": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" } },
                  "elevation": { "steep_slope": { "path": "Elevation/SteepSlope.gpkg", "threshold_deg": 35.0, "feature_count": 32 } }
                """
            : string.Empty;

        return $$"""
            {
              "version": "1.14.0",
              "job_id": "0d4cbfb1-0000-4000-8000-000000000000",
              "hosts": { "revit": { {{revit}} } }{{blocks}}
            }
            """;
    }

    private const string FootGeoreference = """
        "georeference": {
          "crs_projected": "EPSG:2231",
          "origin": {
            "lon": -105.32557885004304,
            "lat": 38.46130517000308,
            "projected": { "epsg": 2231, "easting": 1450131.2, "northing": 13171825.6, "linear_unit": "ftUS" }
          }
        }
        """;

    private const string MetricGeoreference = """
        "georeference": {
          "crs_projected": "EPSG:32613",
          "origin": {
            "lon": -105.32557885004304,
            "lat": 38.46130517000308,
            "projected": { "epsg": 32613, "easting": 471595.0, "northing": 4257050.0, "linear_unit": "m" }
          }
        }
        """;

    private const string FootFileFrame =
        "\"file_frame\": { \"type\": \"projected\", \"crs\": \"EPSG:2231\", \"horizontal_unit\": \"ftUS\", \"vertical_unit\": \"ftUS\" }";

    private const string MetricFileFrame =
        "\"file_frame\": { \"type\": \"projected\", \"crs\": \"EPSG:32613\", \"horizontal_unit\": \"m\", \"vertical_unit\": \"m\" }";

    private const string LocalFootFileFrame =
        "\"file_frame\": { \"type\": \"local\", \"base_crs\": \"EPSG:32613\", "
        + "\"origin\": { \"easting\": 471595.0, \"northing\": 4257050.0, \"linear_unit\": \"m\" }, "
        + "\"horizontal_unit\": \"ft\", \"vertical_unit\": \"ft\" }";

    private static BundleManifest Parse(string json)
    {
        BundleManifest manifest = BundleManifestReader.Parse(json);
        if (!manifest.IsValid)
        {
            throw new InvalidOperationException($"fixture refused: {manifest.Error}");
        }

        return manifest;
    }

    private static BundleImportPlan Plan(string manifestJson, IEnumerable<string>? entries = null)
        => BundleImportPlanner.Plan(Parse(manifestJson), entries ?? Entries, _ => new ImageSize(4000, 3000));

    private static bool Has(BundleImportPlan plan, ImportStepKind kind) => Find(plan, kind) is not null;

    private static ImportStep? Find(BundleImportPlan plan, ImportStepKind kind)
        => plan.Steps.FirstOrDefault(step => step.Kind == kind);

    private static SkippedImport? Skip(BundleImportPlan plan, ImportStepKind kind)
        => plan.Skipped.FirstOrDefault(skip => skip.Kind == kind);
}
