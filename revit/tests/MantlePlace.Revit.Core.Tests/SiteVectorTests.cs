using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The three Forma-parity layers' readers: road centrelines and site boundaries out of GeoJSON,
/// vegetation out of the tree-points CSV, all landing in the bundle's own AOI-centroid frame.
/// </summary>
/// <remarks>
/// Host semantics rather than contract semantics, so they live here and not in the shared corpus
/// (<c>HPS-03</c>, <c>DOC-06</c>) — what "the frame the toposolid was built in" means is Revit's
/// question. The one contract-level piece, the lon/lat forward projection itself, is driven from
/// the corpus by <see cref="ProjectionConformanceTests"/>.
/// </remarks>
internal static class SiteVectorTests
{
    /// <summary>The metric-tier frame: AOI centroid in UTM 13N, exactly as the fixture bundle states it.</summary>
    private static readonly SiteFrame MetricFrame = new()
    {
        Origin = new GeoOrigin
        {
            Epsg = 32613,
            Easting = 471595.0,
            Northing = 4257050.0,
            LinearUnit = LinearUnit.Metre,
        },
    };

    /// <summary>A State-Plane foot frame — the tier where a metric UTM layer cannot be placed at all.</summary>
    private static readonly SiteFrame FootFrame = new()
    {
        Origin = new GeoOrigin
        {
            Epsg = 2231,
            Easting = 1450131.2,
            Northing = 13171825.6,
            LinearUnit = LinearUnit.UsSurveyFoot,
        },
    };

    internal static int Run()
    {
        TestRun run = new();

        RunFrameCases(run);
        RunVectorCases(run);
        RunTreeCases(run);

        return run.Report("site vector readers");
    }

    private static void RunFrameCases(TestRun run)
    {
        run.Case("a projected layer places only against an origin in its OWN CRS", () =>
        {
            run.True(MetricFrame.CanPlaceProjected(32613), "UTM 13N against a UTM 13N origin");

            // Not a rounding question. The tree-points CSV is always AOI-UTM whatever the delivery
            // tier, so on a State Plane foot tier the two are different coordinate systems and
            // subtracting one from the other yields a plausible-looking number ~2000 km out.
            run.False(FootFrame.CanPlaceProjected(32613), "UTM 13N against a State Plane foot origin");
            run.False(MetricFrame.CanPlaceProjected(0), "an unstated layer CRS is not assumed to match");
        });

        run.Case("a geographic layer needs a UTM origin, because UTM is the only forward this host has", () =>
        {
            run.True(MetricFrame.CanPlaceGeographic, "a UTM origin");
            run.False(FootFrame.CanPlaceGeographic, "a State Plane origin");
        });

        run.Case("local metres are absolute minus origin, in the ORIGIN's unit", () =>
        {
            run.True(
                MetricFrame.TryToLocalMetres(472195.0, 4257585.0, out double east, out double north),
                "a metric UTM point places");
            run.Within(east, 600.0, 1e-6, "east offset");
            run.Within(north, 535.0, 1e-6, "north offset");
        });

        run.Case("the frame comes from the manifest's own pre-derived origin, or not at all", () =>
        {
            BundleManifest metric = BundleManifestReader.Parse(
                """
                  {
                    "version": "1.0.0",
                    "hosts": {
                      "revit": {
                        "georeference": {
                          "crs_projected": "EPSG:32613",
                          "origin": {
                            "lon": -105.3,
                            "lat": 38.4,
                            "projected": {
                              "epsg": 32613,
                              "easting": 471595.0,
                              "northing": 4257050.0,
                              "linear_unit": "m"
                            }
                          }
                        }
                      }
                    }
                  }
                """);
            run.True(SiteFrame.For(metric) is not null, "a published origin yields a frame");

            BundleManifest none = BundleManifestReader.Parse("""{"version": "1.0.0"}""");
            run.True(SiteFrame.For(none) is null, "no published origin yields no frame — never a guessed one");
        });
    }

    private static void RunVectorCases(TestRun run)
    {
        run.Case("a LineString becomes one open feature, projected into the frame", () =>
        {
            string? error = SiteVectorReader.TryParse(
                """
                {
                  "type": "FeatureCollection",
                  "features": [
                    {
                      "type": "Feature",
                      "properties": { "class": "service", "name": "County Road 3A", "width_m_estimated": 4.0 },
                      "geometry": {
                        "type": "LineString",
                        "coordinates": [
                          [-105.32557885004304, 38.46130517000308, 2034.5],
                          [-105.3250, 38.4615, 2030.25]
                        ]
                      }
                    }
                  ]
                }
                """,
                MetricFrame,
                SiteGeometryKinds.Lines,
                "road centrelines",
                out IReadOnlyList<SiteFeature> features);

            run.True(error is null, $"parsed: {error}");
            run.Equal(features.Count, 1, "one feature");
            run.False(features[0].IsClosed, "a centreline is open");
            run.Equal(features[0].Name, "County Road 3A", "the name rides along");
            run.Equal(features[0].Classification, "service", "the class rides along");
            run.Within(features[0].WidthM ?? 0.0, 4.0, 1e-9, "the estimated width rides along");

            // The manifest's own origin IS this lon/lat, so the first vertex must land on the frame
            // origin to within the corpus tolerance. That is the assertion that catches a projection
            // that is self-consistent and wrong.
            run.Within(features[0].Vertices[0].EastM, 0.0, 0.05, "the first vertex lands on the origin, east");
            run.Within(features[0].Vertices[0].NorthM, 0.0, 0.05, "the first vertex lands on the origin, north");
            run.Within(features[0].Vertices[0].ElevationM ?? 0.0, 2034.5, 1e-9, "Z passes through as absolute metres");
        });

        run.Case("a MultiLineString becomes one feature per part", () =>
        {
            string? error = SiteVectorReader.TryParse(
                """
                {
                  "features": [
                    {
                      "geometry": {
                        "type": "MultiLineString",
                        "coordinates": [
                          [[-105.325, 38.461], [-105.324, 38.462]],
                          [[-105.323, 38.463], [-105.322, 38.464]]
                        ]
                      }
                    }
                  ]
                }
                """,
                MetricFrame,
                SiteGeometryKinds.Lines,
                "road centrelines",
                out IReadOnlyList<SiteFeature> features);

            run.True(error is null, $"parsed: {error}");
            run.Equal(features.Count, 2, "one feature per part, mirroring the ETL's per-part rows");
        });

        run.Case("a Polygon yields closed rings, and only when areas are asked for", () =>
        {
            const string Payload = """
                {
                  "features": [
                    {
                      "properties": { "class": "playground" },
                      "geometry": {
                        "type": "Polygon",
                        "coordinates": [
                          [[-105.3260, 38.4580], [-105.3255, 38.4580], [-105.3255, 38.4585], [-105.3260, 38.4580]]
                        ]
                      }
                    }
                  ]
                }
                """;

            string? error = SiteVectorReader.TryParse(
                Payload, MetricFrame, SiteGeometryKinds.Areas, "site boundaries", out IReadOnlyList<SiteFeature> areas);
            run.True(error is null, $"parsed: {error}");
            run.Equal(areas.Count, 1, "one ring");
            run.True(areas[0].IsClosed, "a boundary ring is closed");

            // The duplicated closing position GeoJSON requires is dropped: Revit's CurveLoop closes
            // itself, and a zero-length final segment is a curve it rejects outright.
            run.Equal(areas[0].Vertices.Count, 3, "the repeated closing position is not a fourth vertex");

            error = SiteVectorReader.TryParse(
                Payload, MetricFrame, SiteGeometryKinds.Lines, "road centrelines", out IReadOnlyList<SiteFeature> lines);
            run.True(error is null, $"parsed: {error}");
            run.Equal(lines.Count, 0, "a polygon is not a centreline");
        });

        run.Case("one malformed feature does not drop the layer, but malformed JSON does", () =>
        {
            string? error = SiteVectorReader.TryParse(
                """
                {
                  "features": [
                    "not an object",
                    { "geometry": { "type": "LineString", "coordinates": [[-105.325, 38.461]] } },
                    { "geometry": { "type": "LineString", "coordinates": [[-105.325, 38.461], [-105.324, 38.462]] } }
                  ]
                }
                """,
                MetricFrame,
                SiteGeometryKinds.Lines,
                "road centrelines",
                out IReadOnlyList<SiteFeature> features);

            run.True(error is null, $"parsed: {error}");
            run.Equal(features.Count, 1, "the single-vertex line and the non-object are dropped, the good one survives");

            error = SiteVectorReader.TryParse(
                "{ not json", MetricFrame, SiteGeometryKinds.Lines, "road centrelines", out _);
            run.Contains(error, "road centrelines", "the failure names the layer the user asked for");

            error = SiteVectorReader.TryParse(
                """{"type": "FeatureCollection"}""",
                MetricFrame,
                SiteGeometryKinds.Lines,
                "road centrelines",
                out _);
            run.Contains(error, "features", "a collection with no features array is a read failure, not an empty layer");
        });
    }

    private static void RunTreeCases(TestRun run)
    {
        run.Case("the tree CSV lands in the frame, carrying the dimensions that make it geometry", () =>
        {
            string? error = TreePointsReader.TryParse(
                """
                x,y,ground_z,height_m,crown_radius_m
                472195.00,4257585.00,2006.71,3.38,1.18
                471835.00,4257485.00,1985.45,3.10,1.08
                """,
                MetricFrame,
                out IReadOnlyList<SiteTree> trees);

            run.True(error is null, $"parsed: {error}");
            run.Equal(trees.Count, 2, "two trees");
            run.Within(trees[0].EastM, 600.0, 1e-6, "east offset");
            run.Within(trees[0].NorthM, 535.0, 1e-6, "north offset");
            run.Within(trees[0].GroundElevationM, 2006.71, 1e-9, "ground_z is absolute orthometric metres");
            run.Within(trees[0].HeightM, 3.38, 1e-9, "height");
            run.Within(trees[0].CrownRadiusM, 1.18, 1e-9, "crown radius");
        });

        run.Case("a row the DEM had no ground for is dropped, not placed at zero", () =>
        {
            // The ETL leaves ground_z empty where the DEM had no data. Reading that as 0.0 puts a
            // tree two kilometres below the terrain it belongs to, which looks like a modelling
            // mistake rather than a data gap (HPS-20: unknown is not zero).
            string? error = TreePointsReader.TryParse(
                """
                x,y,ground_z,height_m,crown_radius_m
                472195.00,4257585.00,,3.38,1.18
                471835.00,4257485.00,1985.45,3.10,1.08
                """,
                MetricFrame,
                out IReadOnlyList<SiteTree> trees);

            run.True(error is null, $"parsed: {error}");
            run.Equal(trees.Count, 1, "the row with no ground elevation is dropped");
            run.Within(trees[0].GroundElevationM, 1985.45, 1e-9, "the row that did have one survives");
        });

        run.Case("a missing or unrecognised header is a read failure, not an empty layer", () =>
        {
            run.Contains(
                TreePointsReader.TryParse("472195.00,4257585.00,2006.71,3.38,1.18", MetricFrame, out _),
                "header",
                "a headerless file is refused");

            run.Contains(
                TreePointsReader.TryParse("a,b,c\n1,2,3", MetricFrame, out _),
                "header",
                "an unrecognised header is refused");

            run.Contains(
                TreePointsReader.TryParse(string.Empty, MetricFrame, out _),
                "empty",
                "an empty file says so");
        });

        run.Case("column ORDER comes from the header, not from position", () =>
        {
            // The manifest publishes `columns`, so the order is contract rather than convention. A
            // reader that indexed positionally would silently swap height for crown radius the day
            // the ETL reorders them, and every tree would still be a tree.
            string? error = TreePointsReader.TryParse(
                """
                crown_radius_m,height_m,ground_z,y,x
                1.18,3.38,2006.71,4257585.00,472195.00
                """,
                MetricFrame,
                out IReadOnlyList<SiteTree> trees);

            run.True(error is null, $"parsed: {error}");
            run.Equal(trees.Count, 1, "one tree");
            run.Within(trees[0].EastM, 600.0, 1e-6, "east offset");
            run.Within(trees[0].HeightM, 3.38, 1e-9, "height, not crown radius");
            run.Within(trees[0].CrownRadiusM, 1.18, 1e-9, "crown radius, not height");
        });
    }
}
