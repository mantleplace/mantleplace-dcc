using MantlePlace.Core;

namespace MantlePlace.Core.Tests;

internal static class SiteVectorTests
{
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
        RunFoliageVocabularyCases(run);

        return run.Report("site vector readers");
    }

    private static void RunFrameCases(TestRun run)
    {
        run.Case("a projected layer places only against an origin in its OWN CRS", () =>
        {
            run.True(MetricFrame.CanPlaceProjected(32613), "UTM 13N against a UTM 13N origin");

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

            run.Equal(areas[0].Vertices.Count, 3, "the repeated closing position is not a fourth vertex");

            error = SiteVectorReader.TryParse(
                Payload, MetricFrame, SiteGeometryKinds.Lines, "road centrelines", out IReadOnlyList<SiteFeature> lines);
            run.True(error is null, $"parsed: {error}");
            run.Equal(lines.Count, 0, "a polygon is not a centreline");
        });

        run.Case("the published subtype rides along on every ring, and a hole says it is one", () =>
        {
            string? error = SiteVectorReader.TryParse(
                """
                {
                  "features": [
                    {
                      "properties": { "overture_id": "x", "subtype": "forest" },
                      "geometry": {
                        "type": "MultiPolygon",
                        "coordinates": [
                          [
                            [[-105.3270, 38.4570], [-105.3250, 38.4570], [-105.3250, 38.4590], [-105.3270, 38.4570]],
                            [[-105.3262, 38.4575], [-105.3258, 38.4575], [-105.3258, 38.4579], [-105.3262, 38.4575]]
                          ],
                          [
                            [[-105.3240, 38.4570], [-105.3230, 38.4570], [-105.3230, 38.4580], [-105.3240, 38.4570]]
                          ]
                        ]
                      }
                    },
                    {
                      "properties": { "class": "park" },
                      "geometry": {
                        "type": "Polygon",
                        "coordinates": [
                          [[-105.3220, 38.4570], [-105.3210, 38.4570], [-105.3210, 38.4580], [-105.3220, 38.4570]]
                        ]
                      }
                    }
                  ]
                }
                """,
                MetricFrame,
                SiteGeometryKinds.Areas,
                "land cover",
                out IReadOnlyList<SiteFeature> rings);

            run.True(error is null, $"parsed: {error}");
            run.Equal(rings.Count, 4, "three rings from the MultiPolygon, one from the Polygon");
            run.Equal(rings[0].Subtype, "forest", "the outer ring carries the subtype");
            run.False(rings[0].IsHole, "an outer ring is not a hole");
            run.Equal(rings[1].Subtype, "forest", "so does the hole, verbatim");
            run.True(rings[1].IsHole, "the clearing is a hole");
            run.False(rings[2].IsHole, "the second polygon's outer ring is not a hole");
            run.Equal(rings[3].Subtype, string.Empty, "no subtype property reads as empty, not as the class");

            run.Equal(rings[0].PolygonOrdinal, 1, "the first polygon of the layer");
            run.Equal(rings[1].PolygonOrdinal, 1, "its clearing belongs to it");
            run.Equal(rings[2].PolygonOrdinal, 2, "the MultiPolygon's second polygon is the layer's second");
            run.Equal(rings[3].PolygonOrdinal, 3, "and the next feature's is the third");
        });

        run.Case("a line belongs to no polygon", () =>
        {
            string? error = SiteVectorReader.TryParse(
                """
                {
                  "features": [
                    { "geometry": { "type": "LineString", "coordinates": [[-105.325, 38.461], [-105.324, 38.462]] } }
                  ]
                }
                """,
                MetricFrame,
                SiteGeometryKinds.Lines,
                "water",
                out IReadOnlyList<SiteFeature> lines);

            run.True(error is null, $"parsed: {error}");
            run.Equal(lines[0].PolygonOrdinal, 0, "a stream centreline is no polygon's ring");
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
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m
                472195.00,4257585.00,2006.71,3.38,1.18
                471835.00,4257485.00,1985.45,3.10,1.08
                """,
                MetricFrame,
                foliageVocabulary: null, groundUnit: LinearUnit.Metre);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            IReadOnlyList<SiteTreePoint> trees = parse.Points;
            run.Equal(trees.Count, 2, "two trees");
            run.Within(trees[0].EastM, 600.0, 1e-6, "east offset");
            run.Within(trees[0].NorthM, 535.0, 1e-6, "north offset");
            run.Within(trees[0].GroundElevationM, 2006.71, 1e-9, "ground_z is absolute orthometric metres");
            run.Within(trees[0].HeightM, 3.38, 1e-9, "height");
            run.Within(trees[0].CrownRadiusM, 1.18, 1e-9, "crown radius");
        });

        run.Case("a 2.x tree file stays absolute: no stated frame subtracts the origin and adds no height", () =>
        {
            const string csv = """
                x,y,ground_z,height_m,crown_radius_m
                472195.00,4257585.00,2006.71,3.38,1.18
                """;

            TreePointsParse unstated = TreePointsReader.Parse(
                csv, MetricFrame, foliageVocabulary: null, groundUnit: LinearUnit.Metre, layer: null, groundBaseM: 0.0);
            TreePointsParse absolute = TreePointsReader.Parse(
                csv,
                MetricFrame,
                foliageVocabulary: null,
                groundUnit: LinearUnit.Metre,
                layer: new LayerFrame(LayerCoordinates.AbsoluteProjected, LinearUnit.Metre));

            foreach (TreePointsParse parse in new[] { unstated, absolute })
            {
                run.Within(parse.Points[0].EastM, 600.0, 1e-6, "east is the offset from the origin");
                run.Within(parse.Points[0].NorthM, 535.0, 1e-6, "north is the offset from the origin");
                run.Within(parse.Points[0].GroundElevationM, 2006.71, 1e-9, "ground_z is absolute, nothing added");
            }
        });

        run.Case("a local-offsets file is taken as it is, in its own unit, whatever the origin", () =>
        {
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m
                100,-50,10,3,1
                """,
                FootFrame,
                foliageVocabulary: null,
                groundUnit: LinearUnit.Metre,
                layer: new LayerFrame(LayerCoordinates.LocalOffsets, LinearUnit.Metre),
                groundBaseM: 2.5);

            run.Within(parse.Points[0].EastM, 100.0, 1e-9, "east is metres, not reduced by the foot origin");
            run.Within(parse.Points[0].NorthM, -50.0, 1e-9, "north likewise");
            run.Within(parse.Points[0].GroundElevationM, 12.5, 1e-9, "ground_z is above the stated base");
        });

        run.Case("ground_z is read in the unit it is published in, so a foot delivery's trees stand on the ground", () =>
        {
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m
                1450459.2833,13171825.6,313.56,19.62,6.87
                """,
                FootFrame,
                foliageVocabulary: null,
                groundUnit: LinearUnit.UsSurveyFoot);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Within(parse.Points[0].EastM, 100.0, 1e-3, "x is feet in the origin's CRS, converted once");
            run.Within(parse.Points[0].GroundElevationM, 313.56 * 1200.0 / 3937.0, 1e-9, "ground_z is feet, stored as metres");
            run.Within(parse.Points[0].HeightM, 19.62, 1e-9, "height_m stays metres");
            run.Within(parse.Points[0].CrownRadiusM, 6.87, 1e-9, "crown_radius_m stays metres");
        });

        run.Case("a row the DEM had no ground for is dropped, not placed at zero", () =>
        {
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m
                472195.00,4257585.00,,3.38,1.18
                471835.00,4257485.00,1985.45,3.10,1.08
                """,
                MetricFrame,
                foliageVocabulary: null, groundUnit: LinearUnit.Metre);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points.Count, 1, "the row with no ground elevation is dropped");
            run.Within(parse.Points[0].GroundElevationM, 1985.45, 1e-9, "the row that did have one survives");
            run.Equal(parse.RowsWithoutGround, 1, "and counted, so the log can say so");
            run.Equal(parse.UnreadableRows, 0, "a DEM gap is not an unreadable row");
        });

        run.Case("a dropped row is counted as a DEM gap or as unreadable, never silently", () =>
        {
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m
                472195.00,4257585.00,  ,3.38,1.18
                471835.00,4257485.00,1985.45,tall,1.08
                471835.00,4257485.00,1985.45,3.10,1.08
                """,
                MetricFrame,
                foliageVocabulary: null, groundUnit: LinearUnit.Metre);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points.Count, 1, "only the whole row survives");
            run.Equal(parse.RowsWithoutGround, 1, "a whitespace ground_z is no ground elevation");
            run.Equal(parse.UnreadableRows, 1, "a non-numeric height is unreadable");
        });

        run.Case("a ground_z that is present but not a number is unreadable, not a DEM gap", () =>
        {
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m
                472195.00,4257585.00,nodata,3.38,1.18
                """,
                MetricFrame,
                foliageVocabulary: null, groundUnit: LinearUnit.Metre);

            run.Equal(parse.RowsWithoutGround, 0, "the cell has a value; it is the wrong kind");
            run.Equal(parse.UnreadableRows, 1, "contract drift");
        });

        run.Case("a row that is both a DEM gap and unreadable is counted once, as unreadable", () =>
        {
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m
                472195.00,4257585.00,,tall,1.18
                """,
                MetricFrame,
                foliageVocabulary: null, groundUnit: LinearUnit.Metre);

            run.Equal(parse.UnreadableRows, 1, "the contract drifting is the finding worth reporting");
            run.Equal(parse.RowsWithoutGround, 0, "and the row is not counted twice");
        });

        run.Case("a row short of a geometry column is unreadable", () =>
        {
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m
                472195.00,4257585.00,2006.71
                """,
                MetricFrame,
                foliageVocabulary: null, groundUnit: LinearUnit.Metre);

            run.Equal(parse.Points.Count, 0, "dropped");
            run.Equal(parse.RowsWithoutGround, 0, "its ground_z was there");
            run.Equal(parse.UnreadableRows, 1, "counted");
        });

        run.Case("a row the frame cannot place is unreadable", () =>
        {
            SiteFrame unplaceable = new() { Origin = new GeoOrigin { Epsg = 32613, LinearUnit = LinearUnit.Metre } };

            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m
                472195.00,4257585.00,2006.71,3.38,1.18
                """,
                unplaceable,
                foliageVocabulary: null, groundUnit: LinearUnit.Metre);

            run.Equal(parse.Points.Count, 0, "nothing placed");
            run.Equal(parse.UnreadableRows, 1, "a row the frame refuses is counted as unreadable");
            run.Equal(parse.RowsWithoutGround, 0, "not a DEM gap");
        });

        run.Case("blank lines and a header-only file drop nothing", () =>
        {
            TreePointsParse headerOnly = TreePointsReader.Parse(
                "x,y,ground_z,height_m,crown_radius_m\n",
                MetricFrame,
                foliageVocabulary: null, groundUnit: LinearUnit.Metre);

            run.True(headerOnly.Failure is null, $"a header-only file parses: {headerOnly.Failure}");
            run.Equal(headerOnly.Points.Count, 0, "no points");
            run.Equal(headerOnly.RowsWithoutGround + headerOnly.UnreadableRows, 0, "and nothing dropped");

            TreePointsParse spaced = TreePointsReader.Parse(
                "x,y,ground_z,height_m,crown_radius_m\r\n\r\n472195.00,4257585.00,2006.71,3.38,1.18\r\n\r\n   \r\n"
                    + "471835.00,4257485.00,1985.45,3.10,1.08\r\n\r\n",
                MetricFrame,
                foliageVocabulary: null, groundUnit: LinearUnit.Metre);

            run.Equal(spaced.Points.Count, 2, "the two real rows, either side of the blank ones");
            run.Equal(spaced.RowsWithoutGround + spaced.UnreadableRows, 0, "a blank line is not a row");
        });

        run.Case("a missing or unrecognised header is a read failure, not an empty layer", () =>
        {
            run.Contains(
                TreePointsReader.Parse("472195.00,4257585.00,2006.71,3.38,1.18", MetricFrame, null, groundUnit: LinearUnit.Metre).Failure,
                "header",
                "a headerless file is refused");

            run.Contains(
                TreePointsReader.Parse("a,b,c\n1,2,3", MetricFrame, null, groundUnit: LinearUnit.Metre).Failure,
                "header",
                "an unrecognised header is refused");

            run.Contains(
                TreePointsReader.Parse(string.Empty, MetricFrame, null, groundUnit: LinearUnit.Metre).Failure,
                "empty",
                "an empty file says so");
        });

        run.Case("column ORDER comes from the header, not from position", () =>
        {
            TreePointsParse parse = TreePointsReader.Parse(
                """
                crown_radius_m,height_m,ground_z,y,x
                1.18,3.38,2006.71,4257585.00,472195.00
                """,
                MetricFrame,
                foliageVocabulary: null, groundUnit: LinearUnit.Metre);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points.Count, 1, "one tree");
            run.Within(parse.Points[0].EastM, 600.0, 1e-6, "east offset");
            run.Within(parse.Points[0].HeightM, 3.38, 1e-9, "height, not crown radius");
            run.Within(parse.Points[0].CrownRadiusM, 1.18, 1e-9, "crown radius, not height");
        });

        run.Case("a column the reader does not know is still ignored, not a drifted contract", () =>
        {
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m,foliage_type,canopy_cover
                472195.00,4257585.00,2006.71,3.38,1.18,shrub,0.62
                471835.00,4257485.00,1985.45,3.10,1.08,tree,0.44
                """,
                MetricFrame,
                FoliageTypes.KnownVocabulary, groundUnit: LinearUnit.Metre);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points.Count, 2, "both points, the unknown column notwithstanding");
            run.Within(parse.Points[0].HeightM, 3.38, 1e-9, "height still read by name");
            run.Equal(parse.Points[0].FoliageType, FoliageType.Shrub, "and the column beside it still maps");
            run.Equal(parse.UnknownFoliageValues, 0, "an unknown COLUMN is not an unknown VALUE");
        });

        run.Case("the published foliage type rides on the point, mapped and never inferred", () =>
        {
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m,foliage_type
                472195.00,4257585.00,2006.71,3.38,1.18,shrub
                471835.00,4257485.00,1985.45,3.10,1.08,tree
                """,
                MetricFrame,
                FoliageTypes.KnownVocabulary, groundUnit: LinearUnit.Metre);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points.Count, 2, "both points");
            run.Equal(parse.Points[0].FoliageType, FoliageType.Shrub, "the shrub is a shrub");
            run.Equal(parse.Points[1].FoliageType, FoliageType.Tree, "the tree is a tree");
            run.Within(parse.Points[0].HeightM, 3.38, 1e-9, "height still read by name");
            run.Within(parse.Points[1].CrownRadiusM, 1.08, 1e-9, "crown radius still read by name");
            run.Equal(parse.Notes.Count, 0, "a bundle that says what it means needs no note");
        });

        run.Case("a foliage type this plugin does not know reads as a tree, and is counted", () =>
        {
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m,foliage_type
                472195.00,4257585.00,2006.71,3.38,1.18,hedgerow
                471835.00,4257485.00,1985.45,3.10,1.08,shrub
                """,
                MetricFrame,
                FoliageTypes.KnownVocabulary, groundUnit: LinearUnit.Metre);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points.Count, 2, "an unknown value costs no point");
            run.Equal(parse.Points[0].FoliageType, FoliageType.Tree, "hedgerow reads as a tree");
            run.Equal(parse.Points[1].FoliageType, FoliageType.Shrub, "the value beside it keeps its meaning");
            run.Equal(parse.UnknownFoliageValues, 1, "counted, so the log can say so");
            run.Equal(parse.EmptyFoliageCells, 0, "an unknown value is not an empty cell");
        });

        run.Case("an empty foliage cell reads as a tree, counted apart from an unknown value", () =>
        {
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,foliage_type,height_m,crown_radius_m
                472195.00,4257585.00,2006.71,,3.38,1.18
                471835.00,4257485.00,1985.45,   ,3.10,1.08
                """,
                MetricFrame,
                FoliageTypes.KnownVocabulary, groundUnit: LinearUnit.Metre);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points.Count, 2, "an empty foliage cell costs no point, unlike an empty ground_z");
            run.Equal(parse.Points[0].FoliageType, FoliageType.Tree, "empty reads as a tree");
            run.Equal(parse.Points[1].FoliageType, FoliageType.Tree, "whitespace is empty");
            run.Equal(parse.EmptyFoliageCells, 2, "both counted");
            run.Equal(parse.UnknownFoliageValues, 0, "empty is not unknown");
        });

        run.Case("a row short of the foliage column is a tree, not a dropped row", () =>
        {
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m,foliage_type
                472195.00,4257585.00,2006.71,3.38,1.18
                """,
                MetricFrame,
                FoliageTypes.KnownVocabulary, groundUnit: LinearUnit.Metre);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points.Count, 1, "the row stands");
            run.Equal(parse.Points[0].FoliageType, FoliageType.Tree, "and it is a tree");
            run.Equal(parse.EmptyFoliageCells, 1, "counted as the missing classification it is");
        });
    }

    private static void RunFoliageVocabularyCases(TestRun run)
    {
        run.Case("no vocabulary and no column is the 1.1.0 shape: every point is a tree, silently", () =>
        {
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m
                472195.00,4257585.00,2006.71,3.38,1.18
                """,
                MetricFrame,
                foliageVocabulary: null, groundUnit: LinearUnit.Metre);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points[0].FoliageType, FoliageType.Tree, "every point is a tree");
            run.Equal(parse.Notes.Count, 0, "the ordinary older bundle says nothing");
        });

        run.Case("a vocabulary this plugin does not know still maps the values it knows", () =>
        {
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m,foliage_type
                472195.00,4257585.00,2006.71,3.38,1.18,shrub
                471835.00,4257485.00,1985.45,3.10,1.08,espalier
                """,
                MetricFrame,
                foliageVocabulary: "2", groundUnit: LinearUnit.Metre);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points[0].FoliageType, FoliageType.Shrub, "a known value keeps its meaning");
            run.Equal(parse.Points[1].FoliageType, FoliageType.Tree, "the vocabulary's new value reads as a tree");
            run.Equal(parse.UnknownFoliageValues, 1, "and is counted");
            run.Equal(parse.Notes.Count, 1, "said once, not once per row");
            run.Contains(parse.Notes[0], "newer", "the note says the vocabulary is newer than the plugin");
        });

        run.Case("a column with no vocabulary to read it by is ignored, and said once", () =>
        {
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m,foliage_type
                472195.00,4257585.00,2006.71,3.38,1.18,shrub
                """,
                MetricFrame,
                foliageVocabulary: null, groundUnit: LinearUnit.Metre);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points.Count, 1, "the point still lands: the five columns that matter parsed");
            run.Equal(parse.Points[0].FoliageType, FoliageType.Tree, "an uninterpretable value is not read");
            run.Equal(parse.UnknownFoliageValues, 0, "the column was not read, so nothing in it was unknown");
            run.Equal(parse.Notes.Count, 1, "one note");
            run.Contains(parse.Notes[0], "vocabulary", "which names what is missing");
        });

        run.Case("a vocabulary with no column to read is the same tree, said once", () =>
        {
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m
                472195.00,4257585.00,2006.71,3.38,1.18
                """,
                MetricFrame,
                FoliageTypes.KnownVocabulary, groundUnit: LinearUnit.Metre);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points[0].FoliageType, FoliageType.Tree, "every point is a tree");
            run.Equal(parse.Notes.Count, 1, "one note");
            run.Contains(parse.Notes[0], "column", "which names what is missing");
        });

        run.Case("the foliage type is read by header name, wherever the column sits", () =>
        {
            TreePointsParse parse = TreePointsReader.Parse(
                """
                foliage_type,crown_radius_m,height_m,ground_z,y,x
                shrub,1.18,3.38,2006.71,4257585.00,472195.00
                """,
                MetricFrame,
                FoliageTypes.KnownVocabulary, groundUnit: LinearUnit.Metre);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points[0].FoliageType, FoliageType.Shrub, "first column, still the foliage type");
            run.Within(parse.Points[0].HeightM, 3.38, 1e-9, "and the geometry columns are unmoved");
        });

        run.Case("the vocabulary's values are matched exactly, not by case or by prefix", () =>
        {
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m,foliage_type
                472195.00,4257585.00,2006.71,3.38,1.18,Shrub
                471835.00,4257485.00,1985.45,3.10,1.08,shrubland
                """,
                MetricFrame,
                FoliageTypes.KnownVocabulary, groundUnit: LinearUnit.Metre);

            run.Equal(parse.Points[0].FoliageType, FoliageType.Tree, "`Shrub` is not `shrub`");
            run.Equal(parse.Points[1].FoliageType, FoliageType.Tree, "`shrubland` is not `shrub`");
            run.Equal(parse.UnknownFoliageValues, 2, "both counted as unknown");
        });
    }
}
