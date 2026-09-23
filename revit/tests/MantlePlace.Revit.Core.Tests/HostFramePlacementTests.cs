using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The vector layers and the imagery drape, placed from this host's own block first (<c>HPS-52</c>),
/// and refused when the block points at a file it cannot show to be in this host's frame
/// (<c>HPS-53</c>).
/// </summary>
/// <remarks>
/// MPB 1.3.0 hands the Revit host its own copy of both, in its own frame on every delivery
/// (<c>spec/format.md</c> §6.4, §6.5). Before it, a State Plane order placed neither: the shared
/// vector set is lon/lat, which this host may project only into a UTM origin (<c>HPS-45</c>), and
/// the shared drape is on the AOI's UTM grid. A bundle cut before 1.3.0 keeps that path, and what it
/// imports today.
/// </remarks>
internal static class HostFramePlacementTests
{
    private const double UsFootM = 1200.0 / 3937.0;

    private static readonly ImportStepKind[] VectorKinds =
    [
        ImportStepKind.RoadCentrelines,
        ImportStepKind.SiteBoundaries,
        ImportStepKind.LandCover,
        ImportStepKind.Water,
        ImportStepKind.RoadPolygons,
    ];

    /// <summary>The layer names of the Revit copy, one per kind above and in the same order.</summary>
    private static readonly string[] VectorNames = ["road_splines", "land_use", "land_cover", "water", "road_polygons"];

    private static readonly ImageSize DrapePixels = new(4000, 3000);

    internal static int Run()
    {
        TestRun run = new();

        RunReaderCases(run);
        RunVectorPlanCases(run);
        RunVectorRefusalCases(run);
        RunDrapeCases(run);

        return run.Report("host-frame placement");
    }

    private static void RunReaderCases(TestRun run)
    {
        run.Case("a layer in the origin's own projected CRS is placed by subtraction, in its own unit", () =>
        {
            // 328.0833… US survey feet east of the origin is 100 m. The Z is real orthometric
            // height in the file's unit, never an offset (spec/format.md §6.5).
            string? error = SiteVectorReader.TryParse(
                LineString("[1450459.28333, 13171825.6, 1000.0], [1450131.2, 13172153.68333, 1000.0]"),
                FootFrame,
                new LayerFrame(LayerCoordinates.AbsoluteProjected, LinearUnit.UsSurveyFoot),
                SiteGeometryKinds.Lines,
                "road centrelines",
                out IReadOnlyList<SiteFeature> features);

            run.True(error is null, $"parsed: {error}");
            run.Within(features[0].Vertices[0].EastM, 100.0, 1e-3, "east, subtracted in feet and converted once");
            run.Within(features[0].Vertices[0].NorthM, 0.0, 1e-6, "north");
            run.Within(features[0].Vertices[1].NorthM, 100.0, 1e-3, "the second vertex, north");
            run.Within(features[0].Vertices[0].ElevationM ?? 0.0, 1000.0 * UsFootM, 1e-9, "Z is feet, stored as metres");
        });

        run.Case("a local_enu layer is offsets about the origin, scaled out of its own unit", () =>
        {
            string? error = SiteVectorReader.TryParse(
                LineString("[328.083989501, -164.041994751, 1000.0], [0.0, 0.0, 1000.0]"),
                MetricFrame,
                new LayerFrame(LayerCoordinates.LocalOffsets, LinearUnit.InternationalFoot),
                SiteGeometryKinds.Lines,
                "road centrelines",
                out IReadOnlyList<SiteFeature> features);

            run.True(error is null, $"parsed: {error}");
            run.Within(features[0].Vertices[0].EastM, 100.0, 1e-6, "east offset in metres");
            run.Within(features[0].Vertices[0].NorthM, -50.0, 1e-6, "north offset in metres");
            run.Within(features[0].Vertices[0].ElevationM ?? 0.0, 304.8, 1e-9, "Z is international feet, stored as metres");
        });

        run.Case("an own drape or vector layer with no sha256 refuses the bundle, as every Revit deliverable does", () =>
        {
            // The 1.3.0 schema requires the hash on both, and a required-and-missing hash is a
            // producer defect that fails closed rather than importing unverifiable bytes (HPS-34).
            BundleManifest noDrapeHash = BundleManifestReader.Parse(Manifest(
                FootGeoreference,
                FootFileFrame,
                vectors: null,
                FootDrape.Replace($"\"sha256\": \"{DrapeSha}\"", "\"sha256\": null", StringComparison.Ordinal),
                VectorsPresent));
            run.False(noDrapeHash.IsValid, "a drape with no hash is refused");
            run.Contains(noDrapeHash.Error, "hosts.revit.drape", "naming the drape");

            BundleManifest noLayerHash = BundleManifestReader.Parse(Manifest(
                FootGeoreference,
                FootFileFrame,
                AllLayers("absolute_projected", "ftUS").Replace($", \"sha256\": \"{OwnSha}\"", string.Empty, StringComparison.Ordinal),
                drape: null,
                VectorsPresent));
            run.False(noLayerHash.IsValid, "a vector layer with no hash is refused");
            run.Contains(noLayerHash.Error, "road_splines", "naming the layer");
        });

        run.Case("an absolute layer whose unit is not the origin's places nothing", () =>
        {
            // The subtraction is in the ORIGIN's unit. A metre file against a foot origin would be
            // a number that looks like a position and is 3.28 times out.
            string? error = SiteVectorReader.TryParse(
                LineString("[1450459.28333, 13171825.6], [1450131.2, 13172153.68333]"),
                FootFrame,
                new LayerFrame(LayerCoordinates.AbsoluteProjected, LinearUnit.Metre),
                SiteGeometryKinds.Lines,
                "road centrelines",
                out IReadOnlyList<SiteFeature> features);

            run.True(error is null, $"the file itself is well-formed: {error}");
            run.Equal(features.Count, 0, "and not one vertex is placed");
        });
    }

    private static void RunVectorPlanCases(TestRun run)
    {
        run.Case("a State Plane delivery places every vector layer from the Revit block's own copy", () =>
        {
            // The case issue 212 is about: before 1.3.0 this bundle's origin refused every one of
            // these layers, because the only copy it had was lon/lat.
            BundleImportPlan plan = Plan(Manifest(FootGeoreference, FootFileFrame, AllLayers("absolute_projected", "ftUS"), drape: null, VectorsPresent));

            for (int index = 0; index < VectorKinds.Length; index++)
            {
                ImportStep? step = Find(plan, VectorKinds[index]);
                run.True(step is not null, $"{VectorKinds[index]} is planned");
                run.Equal(step?.EntryName, OwnPath(VectorNames[index]), $"{VectorKinds[index]} reads the own copy, not the shared set");
                run.Equal(step?.ExpectedSha256, OwnSha, $"{VectorKinds[index]} verifies the own copy's hash");
                run.True(
                    step?.Layer == new LayerFrame(LayerCoordinates.AbsoluteProjected, LinearUnit.UsSurveyFoot),
                    $"{VectorKinds[index]} is placed by subtraction, in feet");
            }
        });

        run.Case("a metric bundle takes the same path — one path, not a metric one and an imperial one", () =>
        {
            BundleImportPlan plan = Plan(Manifest(MetricGeoreference, MetricFileFrame, AllLayers("absolute_projected", "m"), drape: null, VectorsPresent));

            ImportStep? roads = Find(plan, ImportStepKind.RoadCentrelines);
            run.Equal(roads?.EntryName, OwnPath("road_splines"), "the own copy wins where the shared set would also place");
            run.True(roads?.Layer == new LayerFrame(LayerCoordinates.AbsoluteProjected, LinearUnit.Metre), "by subtraction, not projection");
        });

        run.Case("a local-grid delivery places its offset files about the origin", () =>
        {
            // local_ft: no projected foot zone exists, so the files are offsets in international
            // feet about an origin published in metric UTM (spec/format.md §6.5).
            BundleImportPlan plan = Plan(Manifest(MetricGeoreference, LocalFootFileFrame, AllLayers("local_enu", "ft"), drape: null, VectorsPresent));

            ImportStep? water = Find(plan, ImportStepKind.Water);
            run.Equal(water?.EntryName, OwnPath("water"), "the own copy is planned");
            run.True(water?.Layer == new LayerFrame(LayerCoordinates.LocalOffsets, LinearUnit.InternationalFoot), "as offsets in feet");
        });

        run.Case("a bundle from before 1.3.0 keeps the shared lon/lat set, and what it imports today", () =>
        {
            // No readiness verdict for `vectors` is what marks a bundle cut before the key existed.
            BundleImportPlan metric = Plan(Manifest(MetricGeoreference, fileFrame: null, vectors: null, drape: null, vectorsReadiness: null, version: "1.2.0"));
            ImportStep? roads = Find(metric, ImportStepKind.RoadCentrelines);
            run.Equal(roads?.EntryName, SharedPath("RoadSplines"), "a metric origin still projects the shared set");
            run.True(roads?.Layer == LayerFrame.Geographic, "through the one projection HPS-45 permits");

            BundleImportPlan foot = Plan(Manifest(FootGeoreference, fileFrame: null, vectors: null, drape: null, vectorsReadiness: null, version: "1.2.0"));
            foreach (ImportStepKind kind in VectorKinds)
            {
                run.True(
                    Skip(foot, kind)?.ReasonCode == SkipReasonCode.CoordinateSystemNotSupported,
                    $"{kind} is still refused on a State Plane origin, with the reason it always had");
            }
        });

        run.Case("a 1.3.0 bundle whose copy was withheld falls back to the shared set, as HPS-52 says", () =>
        {
            // The host-neutral pointer is the fallback for content the block does not carry. On a
            // metric origin that places what it always placed; on a State Plane one it is refused
            // for the reason it always was.
            const string Withheld = "{ \"present\": false, \"reason\": \"not_produced\" }";

            BundleImportPlan metric = Plan(Manifest(MetricGeoreference, MetricFileFrame, vectors: null, drape: null, Withheld));
            ImportStep? roads = Find(metric, ImportStepKind.RoadCentrelines);
            run.Equal(roads?.EntryName, SharedPath("RoadSplines"), "a metric origin places the shared set");
            run.True(roads?.Layer == LayerFrame.Geographic, "through the HPS-45 projection");

            BundleImportPlan foot = Plan(Manifest(FootGeoreference, FootFileFrame, vectors: null, drape: null, Withheld));
            foreach (ImportStepKind kind in VectorKinds)
            {
                run.True(
                    Skip(foot, kind)?.ReasonCode == SkipReasonCode.CoordinateSystemNotSupported,
                    $"{kind} is refused on a State Plane origin, as before the copy existed");
            }
        });

        run.Case("a layer missing from the copy had no features in the area, and says so", () =>
        {
            BundleImportPlan plan = Plan(Manifest(
                FootGeoreference,
                FootFileFrame,
                Layers("absolute_projected", "ftUS", "road_splines"),
                drape: null,
                VectorsPresent));

            run.True(Has(plan, ImportStepKind.RoadCentrelines), "the layer that shipped is planned");
            SkippedImport? water = Skip(plan, ImportStepKind.Water);
            run.True(water?.ReasonCode == SkipReasonCode.DeclaredAbsent, "the missing one is declared absent, not merely unlisted");
            run.Contains(water?.Reason, "none in this area", "and is not sent back to the vault for it");
        });
    }

    private static void RunVectorRefusalCases(TestRun run)
    {
        run.Case("a copy the block points at but cannot show to be in this frame is refused, not placed", () =>
        {
            // HPS-53: a pointer sitting in the host block is not itself the showing. Each of these
            // is a producer defect, and each fails closed by name rather than converting anything.
            (string Label, string FileFrame, string Vectors, SkipReasonCode Code)[] cases =
            [
                ("a metre file against a foot origin", FootFileFrame, AllLayers("absolute_projected", "m"), SkipReasonCode.CoordinateSystemNotSupported),
                ("a file frame in another CRS", MetricFileFrame, AllLayers("absolute_projected", "ftUS"), SkipReasonCode.CoordinateSystemNotSupported),
                ("a horizontal frame nobody emits", FootFileFrame, AllLayers("geocentric", "ftUS"), SkipReasonCode.CoordinateSystemNotSupported),
                ("a file frame of an unknown type", "\"file_frame\": { \"type\": \"spherical\", \"horizontal_unit\": \"ftUS\", \"vertical_unit\": \"ftUS\" }", AllLayers("absolute_projected", "ftUS"), SkipReasonCode.CoordinateSystemNotSupported),
                ("a unit that is not the file frame's", FootFileFrame, AllLayers("local_enu", "ft"), SkipReasonCode.CoordinateSystemNotSupported),
                ("a unit this plugin cannot read", FootFileFrame, AllLayers("absolute_projected", "chain"), SkipReasonCode.UnitNotUnderstood),
            ];

            foreach ((string label, string fileFrame, string vectors, SkipReasonCode code) in cases)
            {
                BundleImportPlan plan = Plan(Manifest(FootGeoreference, fileFrame, vectors, drape: null, VectorsPresent));
                run.False(Has(plan, ImportStepKind.RoadCentrelines), $"{label}: nothing is planned");
                run.True(Skip(plan, ImportStepKind.RoadCentrelines)?.ReasonCode == code, $"{label}: refused as {code}");
            }
        });

        run.Case("a copy whose block declares no file frame is refused, not placed on the pointer's word", () =>
        {
            // The schema pairs file_frame with georeference, so this bundle is malformed, and where
            // the pointer sits is not the showing HPS-53 asks for.
            BundleImportPlan plan = Plan(Manifest(FootGeoreference, fileFrame: null, AllLayers("absolute_projected", "ftUS"), drape: null, VectorsPresent));

            run.False(Has(plan, ImportStepKind.RoadCentrelines), "nothing is planned");
            run.True(
                Skip(plan, ImportStepKind.RoadCentrelines)?.ReasonCode == SkipReasonCode.CoordinateSystemNotSupported,
                "the layer is refused as not shown to be in this frame");
        });

        run.Case("an absolute file has to be in the origin's unit even where the file frame agrees with it", () =>
        {
            // The frame's CRS is the origin's but its declared unit is not: a contradiction the
            // subtraction would turn into a 3.28 times error.
            string metreFrame = FootFileFrame.Replace("\"ftUS\"", "\"m\"", StringComparison.Ordinal);
            BundleImportPlan plan = Plan(Manifest(FootGeoreference, metreFrame, AllLayers("absolute_projected", "m"), drape: null, VectorsPresent));

            run.True(
                Skip(plan, ImportStepKind.RoadCentrelines)?.ReasonCode == SkipReasonCode.CoordinateSystemNotSupported,
                "metres subtracted from a foot origin are refused");
        });

        run.Case("a height reference nobody emits is refused, not read as a height", () =>
        {
            string relative = AllLayers("absolute_projected", "ftUS")
                .Replace("\"feature_count\": 1", "\"vertical_reference\": \"relative\", \"feature_count\": 1", StringComparison.Ordinal);
            BundleImportPlan plan = Plan(Manifest(FootGeoreference, FootFileFrame, relative, drape: null, VectorsPresent));

            run.True(
                Skip(plan, ImportStepKind.RoadCentrelines)?.ReasonCode == SkipReasonCode.CoordinateSystemNotSupported,
                "an offset read as an orthometric height would bury or float the road");

            string absolute = AllLayers("absolute_projected", "ftUS")
                .Replace("\"feature_count\": 1", "\"vertical_reference\": \"absolute\", \"feature_count\": 1", StringComparison.Ordinal);
            run.True(
                Has(Plan(Manifest(FootGeoreference, FootFileFrame, absolute, drape: null, VectorsPresent)), ImportStepKind.RoadCentrelines),
                "the declared one is placed");
        });

        run.Case("a local file frame about another origin is not this host's frame", () =>
        {
            string elsewhere = LocalFootFileFrame.Replace("471595.0", "471695.0", StringComparison.Ordinal);
            BundleImportPlan plan = Plan(Manifest(MetricGeoreference, elsewhere, AllLayers("local_enu", "ft"), drape: null, VectorsPresent));

            run.True(
                Skip(plan, ImportStepKind.Water)?.ReasonCode == SkipReasonCode.CoordinateSystemNotSupported,
                "offsets about a point 100 m away would move every feature 100 m");
        });
    }

    private static void RunDrapeCases(TestRun run)
    {
        run.Case("a State Plane delivery drapes the Revit block's own image, on the origin's grid", () =>
        {
            BundleImportPlan plan = Plan(Manifest(FootGeoreference, FootFileFrame, vectors: null, FootDrape, VectorsPresent));

            ImportStep? step = Find(plan, ImportStepKind.ImageryDrape);
            run.True(step is not null, "the drape is planned where the shared one never could be");
            run.Equal(step?.EntryName, "Imagery/Drape.StatePlane.png", "from the own pointer");
            run.Equal(step?.ExpectedSha256, DrapeSha, "verified against the own pointer's hash");
            run.Within(step?.Drape?.LeftM ?? 0.0, -2000.0 * UsFootM, 1e-6, "west edge, feet to frame-local metres");
            run.Within(step?.Drape?.BottomM ?? 0.0, -1500.0 * UsFootM, 1e-6, "south edge");
            run.Within(step?.Drape?.RightM ?? 0.0, 2000.0 * UsFootM, 1e-6, "east edge");
            run.Within(step?.Drape?.TopM ?? 0.0, 1500.0 * UsFootM, 1e-6, "north edge");
            run.True(step?.Drape?.ExtentFromDrapeBlock ?? false, "a declared extent, applied rather than corroborated");
        });

        run.Case("an own drape the block cannot show to be on the origin's grid is refused, never swapped", () =>
        {
            (string Label, string Drape, SkipReasonCode Code)[] cases =
            [
                ("an extent in another CRS", FootDrape.Replace("\"EPSG:2231\"", "\"EPSG:32613\"", StringComparison.Ordinal), SkipReasonCode.CoordinateSystemNotSupported),
                ("an extent in another unit", FootDrape.Replace("\"units\": \"ftUS\"", "\"units\": \"m\"", StringComparison.Ordinal), SkipReasonCode.CoordinateSystemNotSupported),
                ("an extent in an unknown unit", FootDrape.Replace("\"units\": \"ftUS\"", "\"units\": \"rod\"", StringComparison.Ordinal), SkipReasonCode.UnitNotUnderstood),
            ];

            foreach ((string label, string drape, SkipReasonCode code) in cases)
            {
                BundleImportPlan plan = Plan(Manifest(FootGeoreference, FootFileFrame, vectors: null, drape, VectorsPresent));
                run.False(Has(plan, ImportStepKind.ImageryDrape), $"{label}: not draped");
                run.True(Skip(plan, ImportStepKind.ImageryDrape)?.ReasonCode == code, $"{label}: refused as {code}");
            }
        });

        run.Case("with no own drape the shared one is still refused on a State Plane origin", () =>
        {
            // A State Plane delivery cut before 1.3.0 has no drape on Revit's grid at all.
            BundleImportPlan plan = Plan(Manifest(FootGeoreference, fileFrame: null, vectors: null, drape: null, vectorsReadiness: null, version: "1.2.0"));

            run.True(
                Skip(plan, ImportStepKind.ImageryDrape)?.ReasonCode == SkipReasonCode.CoordinateSystemNotSupported,
                "the UTM drape is not draped against a State Plane origin");
        });
    }

    // ---- fixtures -------------------------------------------------------------------------------

    private static readonly SiteFrame MetricFrame = new()
    {
        Origin = new GeoOrigin { Epsg = 32613, Easting = 471595.0, Northing = 4257050.0, LinearUnit = LinearUnit.Metre },
    };

    private static readonly SiteFrame FootFrame = new()
    {
        Origin = new GeoOrigin { Epsg = 2231, Easting = 1450131.2, Northing = 13171825.6, LinearUnit = LinearUnit.UsSurveyFoot },
    };

    private const string OwnSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SharedSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string DrapeSha = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    private const string VectorsPresent = "{ \"present\": true }";

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

    /// <summary>4000 × 3000 US survey feet centred on the foot origin.</summary>
    private const string FootDrape = """
        "drape": {
          "path": "Imagery/Drape.StatePlane.png",
          "format": "png-rgb-8bit",
          "extent": [1448131.2, 13170325.6, 1452131.2, 13173325.6],
          "extent_crs": "EPSG:2231",
          "units": "ftUS",
          "width": 4000,
          "height": 3000,
          "effective_gsd_m": 0.3048006096,
          "sha256": "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
        }
        """;

    private static string OwnPath(string name) => $"Site/Vector/{name}.geojson";

    private static string SharedPath(string stem) => $"Vector/{stem}.geojson";

    private static string AllLayers(string horizontalFrame, string units)
        => Layers(horizontalFrame, units, VectorNames);

    private static string Layers(string horizontalFrame, string units, params string[] names)
    {
        IEnumerable<string> layers = names.Select(name =>
            $$"""
            { "name": "{{name}}", "path": "{{OwnPath(name)}}", "horizontal_frame": "{{horizontalFrame}}",
              "units": "{{units}}", "feature_count": 1, "sha256": "{{OwnSha}}" }
            """);
        return $$"""
            "vectors": { "format": "geojson", "layers": [ {{string.Join(", ", layers)}} ] }
            """;
    }

    /// <summary>
    /// A bundle carrying the shared lon/lat set and the shared UTM drape beside whichever parts of
    /// the Revit block the case is about. <paramref name="vectorsReadiness"/> <c>null</c> is a
    /// bundle from before <c>hosts.revit.readiness.vectors</c> existed.
    /// </summary>
    private static string Manifest(
        string georeference,
        string? fileFrame,
        string? vectors,
        string? drape,
        string? vectorsReadiness,
        string version = "1.3.0")
    {
        string revit = string.Join(
            ",\n",
            new[]
            {
                georeference,
                fileFrame,
                vectors,
                drape,
                "\"readiness\": { \"toposurface_points\": { \"present\": false, \"reason\": \"not_produced\" }"
                    + (vectorsReadiness is null ? string.Empty : ", \"vectors\": " + vectorsReadiness)
                    + " }",
            }.Where(member => member is not null));

        string shared = string.Join(
            ", ",
            new[] { ("road_splines", "RoadSplines"), ("land_use", "LandUse"), ("land_cover", "LandCover"), ("water", "Water"), ("road_polygons", "RoadPolygons") }
                .Select(layer => $$"""{ "name": "{{layer.Item1}}", "formats": [{ "format": "geojson", "path": "{{SharedPath(layer.Item2)}}", "sha256": "{{SharedSha}}" }] }"""));

        return $$"""
            {
              "version": "{{version}}",
              "layout": { "imagery_drape": "Imagery/Drape.png" },
              "hosts": { "revit": { {{revit}} } },
              "vector": { "layers": [ {{shared}} ] },
              "imagery": { "present": true, "gsd_m": 0.3, "drape": {
                "extent": [470880.0, 4256340.0, 472310.0, 4257760.0], "extent_crs": "EPSG:32613" } }
            }
            """;
    }

    private static readonly string[] Entries =
    [
        "Metadata/manifest.json",
        "Imagery/Drape.png",
        "Imagery/Drape.StatePlane.png",
        .. VectorNames.Select(OwnPath),
        .. new[] { "RoadSplines", "LandUse", "LandCover", "Water", "RoadPolygons" }.Select(SharedPath),
    ];

    private static string LineString(string coordinates) =>
        $$"""
        {
          "type": "FeatureCollection",
          "features": [
            { "type": "Feature", "properties": {}, "geometry": { "type": "LineString", "coordinates": [ {{coordinates}} ] } }
          ]
        }
        """;

    private static BundleImportPlan Plan(string manifestJson)
    {
        BundleManifest manifest = BundleManifestReader.Parse(manifestJson);
        if (!manifest.IsValid)
        {
            throw new InvalidOperationException($"fixture refused: {manifest.Error}");
        }

        return BundleImportPlanner.Plan(manifest, Entries, _ => DrapePixels);
    }

    private static bool Has(BundleImportPlan plan, ImportStepKind kind) => Find(plan, kind) is not null;

    private static ImportStep? Find(BundleImportPlan plan, ImportStepKind kind)
        => plan.Steps.FirstOrDefault(step => step.Kind == kind);

    private static SkippedImport? Skip(BundleImportPlan plan, ImportStepKind kind)
        => plan.Skipped.FirstOrDefault(skip => skip.Kind == kind);
}
