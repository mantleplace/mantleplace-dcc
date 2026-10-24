using System.Text.Json;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The reader takes a 2.0 bundle as well as every 1.x one: the 2.0 layout is reached through its
/// pointers alone, matched to the archive regardless of case, and never through a folder name.
/// </summary>
/// <remarks>
/// <para>
/// 2.0 moves files between folders and renames them (<c>spec/changelog.md</c>, 2.0.0), and removes
/// <c>hosts.revit.vectors</c>: a projected-CRS reader reads each vector layer's <c>-Projected</c> twin,
/// which <c>layout.projected_variants</c> names, in place of the 1.x carrier. Bundles already in a vault
/// keep their 1.x version, so the 1.x path stays beside it.
/// </para>
/// </remarks>
internal static class ManifestMajorTwoTests
{
    private const string PointsCsvPath = "Elevation/SurfacePoints.csv";
    private const string PointsSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static readonly string[] Entries =
    [
        "README.md",
        "Metadata/manifest.json",
        "Metadata/stac-item.json",
        PointsCsvPath,
        "Elevation/SteepSlopes.gpkg",
        "Features/RoadsDraped.geojson",
        "Features/RoadsDraped.gpkg",
        "Features/RoadsDraped-Projected.gpkg",
        "Features/Water.geojson",
        "Features/Water.gpkg",
        "Features/Water-Projected.gpkg",
        "Features/LandUse.geojson",
        "Features/LandUse.gpkg",
        "Records/FloodZones.gpkg",
        "Site/Context.usdz",
    ];

    internal static int Run()
    {
        TestRun run = new();

        RunVersionCases(run);
        RunPointerCases(run);
        RunCaseCases(run);
        RunCorpusBundleCase(run);

        return run.Report("manifest major 2");
    }

    private static void RunVersionCases(TestRun run)
    {
        run.Case("a 2.0.0 manifest is read", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse(TwoManifest());
            run.True(manifest.IsValid, $"accepted (rejected with: {manifest.Error})");
            run.Equal(manifest.Version, "2.0.0", "the version, verbatim");
        });

        run.Case("the major after the newest this reader knows is refused with the update remedy", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse(TwoManifest(version: "3.0.0"));
            run.False(manifest.IsValid, "refused");
            run.Contains(manifest.Error, "newer than this plugin understands", "the update refusal, not the re-download one");
            run.Contains(manifest.Error, "1.x to 2.x", "says which majors it reads");
        });

        run.Case("every 1.x the floor admits is still read", () =>
        {
            foreach (string version in new[] { "1.0.0", "1.14.0", "1.15.0" })
            {
                BundleManifest manifest = BundleManifestReader.Parse(OneManifest(version));
                run.True(manifest.IsValid, $"{version} accepted (rejected with: {manifest.Error})");
            }
        });
    }

    private static void RunPointerCases(TestRun run)
    {
        run.Case("a vector layer is read from its projected twin, found through layout.projected_variants", () =>
        {
            ImportStep? roads = Find(Plan(TwoManifest()), ImportStepKind.RoadCentrelines);

            run.Equal(roads?.EntryName, "Features/RoadsDraped-Projected.gpkg", "the twin the pointer names");
            run.Equal(roads?.Table, "road_splines", "the table named for the layer");
            run.True(roads?.SoleTable == true, "a row that lists no tables holds one, whatever its name: the importer reads that one");
            run.True(
                roads?.Layer == new LayerFrame(LayerCoordinates.AbsoluteProjected, LinearUnit.Metre),
                "absolute, in the origin's CRS and unit");
            run.Equal(roads?.GeoJsonFallback?.EntryName, "Features/RoadsDraped.geojson", "the lon/lat file stays the fallback");
        });

        run.Case("a layer split into tables reads the table of the geometry its step draws from", () =>
        {
            ImportStep? water = Find(Plan(TwoManifest()), ImportStepKind.Water);

            run.Equal(water?.EntryName, "Features/Water-Projected.gpkg", "the twin");
            run.Equal(water?.Table, "Water_polygon", "the polygons, which water bodies are drawn from");
            run.True(water?.SoleTable == false, "a row that lists tables is read by the table it names");
        });

        run.Case("a layer with no twin keeps its lon/lat GeoJSON, never its lon/lat GeoPackage", () =>
        {
            ImportStep? landUse = Find(Plan(TwoManifest()), ImportStepKind.LandUse);

            run.Equal(landUse?.EntryName, "Features/LandUse.geojson", "the shared GeoJSON");
            run.True(landUse?.Table is null, "no table: the GeoPackage in Features/ is lon/lat");
        });

        run.Case("the hazard layers are read through their layout pointers", () =>
        {
            BundleImportPlan plan = Plan(TwoManifest());

            ImportStep? flood = Find(plan, ImportStepKind.FloodZones);
            run.Equal(flood?.EntryName, "Records/FloodZones.gpkg", "layout.flood_zones");
            run.Equal(flood?.Table, "flood_zones", "the table named for the layer");
            run.True(flood?.SoleTable == true, "a layout pointer names a file of one table");

            ImportStep? steep = Find(plan, ImportStepKind.SteepGround);
            run.Equal(steep?.EntryName, "Elevation/SteepSlopes.gpkg", "layout.steep_slope, already in the order's CRS");
            run.Equal(steep?.Table, "steep_slope", "the table named for the layer");
            run.True(steep?.SoleTable == true, "a layout pointer names a file of one table");
            run.Equal(steep?.Hazard?.Threshold, "35.0", "the manifest's threshold rides along");
        });

        run.Case("a hazard layer with a twin is read from the twin", () =>
        {
            string twins = "\"Records/FloodZones.gpkg\": \"Records/FloodZones-Projected.gpkg\"";
            ImportStep? flood = Find(
                Plan(TwoManifest(extraTwins: twins), [.. Entries, "Records/FloodZones-Projected.gpkg"]),
                ImportStepKind.FloodZones);

            run.Equal(flood?.EntryName, "Records/FloodZones-Projected.gpkg", "the twin");
        });

        run.Case("the terrain points are found by their pointer in the 2.0 layout", () =>
        {
            ImportStep? points = Find(Plan(TwoManifest()), ImportStepKind.ToposurfaceFromPointsFile);

            run.Equal(points?.EntryName, PointsCsvPath, "Elevation/, not the 1.x Surface/");
            run.Equal(points?.ExpectedSha256, PointsSha, "verified by the block's hash");
        });

        run.Case("hosts.revit.vectors, removed at 2.0, is not read from a 2.0 manifest", () =>
        {
            string carrier = """
                , "vectors": { "gpkg": { "path": "Site/Vector/SiteVectors.gpkg", "tables": ["road_splines"] } }
                """;
            BundleManifest manifest = BundleManifestReader.Parse(TwoManifest(revitExtra: carrier));

            run.True(manifest.IsValid, $"no 1.x hash rule applies to a removed member (rejected with: {manifest.Error})");
            run.Equal(manifest.RoadSplines?.Path, "Features/RoadsDraped-Projected.gpkg", "the twin, not the 1.x carrier");
            run.False(manifest.VectorsFromOwnBlock, "the shared set's absence rules, not the own copy's");
        });
    }

    private static void RunCaseCases(TestRun run)
    {
        run.Case("a pointer finds the archive's entry whatever its case", () =>
        {
            string[] lowered = [.. Entries.Select(entry => entry.ToLowerInvariant())];
            BundleImportPlan plan = Plan(TwoManifest(), lowered);

            run.Equal(Find(plan, ImportStepKind.RoadCentrelines)?.EntryName, "features/roadsdraped-projected.gpkg", "the archive's own spelling");
            run.Equal(Find(plan, ImportStepKind.ToposurfaceFromPointsFile)?.EntryName, "elevation/surfacepoints.csv", "the archive's own spelling");
            run.False(plan.Skipped.Any(skip => skip.ReasonCode == SkipReasonCode.EntryNotInArchive), "nothing is missing");
        });

        run.Case("a projected_variants key matches its layer's path whatever its case", () =>
        {
            string manifest = TwoManifest().Replace(
                "\"Features/RoadsDraped.gpkg\": \"Features/RoadsDraped-Projected.gpkg\"",
                "\"features/roadsdraped.GPKG\": \"Features/RoadsDraped-Projected.gpkg\"",
                StringComparison.Ordinal);

            run.Equal(Find(Plan(manifest), ImportStepKind.RoadCentrelines)?.EntryName, "Features/RoadsDraped-Projected.gpkg", "the twin");
        });

        run.Case("a file entry is found whatever the case of the path asked for", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse(TwoManifest());

            run.Equal(OdblContent.RoleOf(manifest.FileEntries, "site/context.usdz"), OdblRole.ProducedWork, "the stated role, not the missing-role default");
            run.Equal(OdblContent.RoleOf(manifest.FileEntries, "ELEVATION/SURFACEPOINTS.CSV"), OdblRole.None, "the stated role");
        });
    }

    private static void RunCorpusBundleCase(TestRun run)
    {
        run.Case("the corpus's full 2.0 bundle is read, and everything planned is a file it holds", () =>
        {
            if (ConformanceCorpus.FindCorpusDirectory() is not { } corpus)
            {
                run.Fail("conformance corpus not found");
                return;
            }

            using JsonDocument bundle = JsonDocument.Parse(File.ReadAllText(Path.Combine(corpus, "bundle", "v2-full.json")));
            JsonElement files = bundle.RootElement.GetProperty("files");
            string[] entries = [.. files.EnumerateObject().Select(file => file.Name)];

            BundleManifest manifest = BundleManifestReader.Parse(files.GetProperty("Metadata/manifest.json").GetRawText());
            run.True(manifest.IsValid, $"accepted (rejected with: {manifest.Error})");

            BundleImportPlan plan = BundleImportPlanner.Plan(manifest, entries, _ => new ImageSize(4000, 3000));
            run.True(plan.Steps.Any(step => step.EntryName.Length > 0), "something in it is planned from a file");
            foreach (ImportStep step in plan.Steps.Where(step => step.EntryName.Length > 0))
            {
                run.True(entries.Contains(step.EntryName, StringComparer.Ordinal), $"{step.Kind} reads {step.EntryName}, a file of the bundle");
            }

            run.False(plan.Skipped.Any(skip => skip.ReasonCode == SkipReasonCode.EntryNotInArchive), "no pointer names a missing file");
        });
    }

    // ------------------------------------------------------------------ fixtures

    private static string TwoManifest(string version = "2.0.0", string revitExtra = "", string extraTwins = "")
    {
        string twins = "\"Features/RoadsDraped.gpkg\": \"Features/RoadsDraped-Projected.gpkg\", "
            + "\"Features/Water.gpkg\": \"Features/Water-Projected.gpkg\""
            + (extraTwins.Length > 0 ? ", " + extraTwins : string.Empty);

        return $$"""
            {
              "version": "{{version}}",
              "job_id": "0d4cbfb1-0000-4000-8000-000000000002",
              "order_id": "3f285101-0310-425b-b06b-bdb73b025b6b",
              "layout": {
                "readme": "README.md",
                "manifest": "Metadata/manifest.json",
                "stac_item": "Metadata/stac-item.json",
                "points_csv": "{{PointsCsvPath}}",
                "steep_slope": "Elevation/SteepSlopes.gpkg",
                "flood_zones": "Records/FloodZones.gpkg",
                "context_usdz": "Site/Context.usdz",
                "projected_variants": { {{twins}} }
              },
              "vector": {
                "present": true,
                "crs": "EPSG:4326",
                "layers": [
                  { "name": "road_splines", "formats": [
                    { "path": "Features/RoadsDraped.geojson", "format": "geojson" },
                    { "path": "Features/RoadsDraped.gpkg", "format": "gpkg" } ] },
                  { "name": "water", "formats": [
                    { "path": "Features/Water.geojson", "format": "geojson" },
                    { "path": "Features/Water.gpkg", "format": "gpkg", "tables": ["Water_point", "Water_line", "Water_polygon"] } ] },
                  { "name": "land_use", "formats": [
                    { "path": "Features/LandUse.geojson", "format": "geojson" },
                    { "path": "Features/LandUse.gpkg", "format": "gpkg" } ] }
                ]
              },
              "flood": { "nfhl": { "source": "fema-nfhl", "zones": ["AE", "X"], "dfirm_ids": ["28049C"], "panels": [] } },
              "elevation": { "steep_slope": { "threshold_deg": 35.0, "feature_count": 32 } },
              "hosts": {
                "revit": {
                  "georeference": {
                    "crs_projected": "EPSG:32613",
                    "origin": {
                      "lon": -105.32557885004304,
                      "lat": 38.46130517000308,
                      "projected": { "epsg": 32613, "easting": 471595.0, "northing": 4257050.0, "linear_unit": "m" }
                    }
                  },
                  "file_frame": { "type": "projected", "crs": "EPSG:32613", "horizontal_unit": "m", "vertical_unit": "m" },
                  "toposurface_points": {
                    "path": "{{PointsCsvPath}}",
                    "horizontal_frame": "absolute_projected",
                    "units": "m",
                    "sha256": "{{PointsSha}}"
                  },
                  "readiness": {
                    "toposurface_points": { "present": true },
                    "flood_zones": { "present": true },
                    "steep_slope": { "present": true }
                  }{{revitExtra}}
                }
              },
              "file_entries": {
                "Features/RoadsDraped-Projected.gpkg": { "frame": { "horizontal_crs": { "code": "EPSG:32613" }, "coordinate_mode": "absolute" }, "odbl_role": "database", "license": "ODbL-1.0", "sources": [] },
                "Site/Context.usdz": { "frame": { "horizontal_crs": { "code": "EPSG:32613" }, "coordinate_mode": "offset" }, "odbl_role": "produced-work", "license": "LicenseRef-MantlePlace-Terms", "sources": [] },
                "{{PointsCsvPath}}": { "frame": { "horizontal_crs": { "code": "EPSG:32613" }, "coordinate_mode": "absolute" }, "odbl_role": "none", "license": "CC0-1.0", "sources": [] }
              }
            }
            """;
    }

    private static string OneManifest(string version) => $$"""
        {
          "version": "{{version}}",
          "job_id": "0d4cbfb1-0000-4000-8000-000000000001",
          "layout": { "manifest": "Metadata/manifest.json", "points_csv": "Surface/SurfacePoints.csv" },
          "hosts": {
            "revit": {
              "toposurface_points": { "path": "Surface/SurfacePoints.csv", "horizontal_frame": "local_enu", "units": "m", "sha256": "{{PointsSha}}" },
              "readiness": { "toposurface_points": { "present": true } }
            }
          }
        }
        """;

    private static BundleImportPlan Plan(string manifestJson, IEnumerable<string>? entries = null)
    {
        BundleManifest manifest = BundleManifestReader.Parse(manifestJson);
        if (!manifest.IsValid)
        {
            throw new InvalidOperationException($"fixture refused: {manifest.Error}");
        }

        return BundleImportPlanner.Plan(manifest, entries ?? Entries, _ => new ImageSize(4000, 3000));
    }

    private static ImportStep? Find(BundleImportPlan plan, ImportStepKind kind)
        => plan.Steps.FirstOrDefault(step => step.Kind == kind);
}
