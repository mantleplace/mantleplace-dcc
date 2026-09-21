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
        RunFoliageVocabularyCases(run);

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

        run.Case("the published subtype rides along on every ring, and a hole says it is one", () =>
        {
            // land_cover's shape: a MultiPolygon carrying `subtype` and no name. The first polygon has
            // a clearing in it; the hole is where the forest is NOT, so it must be told apart.
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

            // Which rings belong together, for the layers that cut a polygon whole (GroundCuts).
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
                foliageVocabulary: null);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            IReadOnlyList<SiteTreePoint> trees = parse.Points;
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
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m
                472195.00,4257585.00,,3.38,1.18
                471835.00,4257485.00,1985.45,3.10,1.08
                """,
                MetricFrame,
                foliageVocabulary: null);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points.Count, 1, "the row with no ground elevation is dropped");
            run.Within(parse.Points[0].GroundElevationM, 1985.45, 1e-9, "the row that did have one survives");
        });

        run.Case("a missing or unrecognised header is a read failure, not an empty layer", () =>
        {
            run.Contains(
                TreePointsReader.Parse("472195.00,4257585.00,2006.71,3.38,1.18", MetricFrame, null).Failure,
                "header",
                "a headerless file is refused");

            run.Contains(
                TreePointsReader.Parse("a,b,c\n1,2,3", MetricFrame, null).Failure,
                "header",
                "an unrecognised header is refused");

            run.Contains(
                TreePointsReader.Parse(string.Empty, MetricFrame, null).Failure,
                "empty",
                "an empty file says so");
        });

        run.Case("column ORDER comes from the header, not from position", () =>
        {
            // The manifest publishes `columns`, so the order is contract rather than convention. A
            // reader that indexed positionally would silently swap height for crown radius the day
            // the ETL reorders them, and every tree would still be a tree.
            TreePointsParse parse = TreePointsReader.Parse(
                """
                crown_radius_m,height_m,ground_z,y,x
                1.18,3.38,2006.71,4257585.00,472195.00
                """,
                MetricFrame,
                foliageVocabulary: null);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points.Count, 1, "one tree");
            run.Within(parse.Points[0].EastM, 600.0, 1e-6, "east offset");
            run.Within(parse.Points[0].HeightM, 3.38, 1e-9, "height, not crown radius");
            run.Within(parse.Points[0].CrownRadiusM, 1.18, 1e-9, "crown radius, not height");
        });

        run.Case("a column the reader does not know is still ignored, not a drifted contract", () =>
        {
            // §4.4: a reader MUST ignore a column it does not know. `manifest.treePointsRowCount`
            // pins it for the host whose block it reads, and this host's own suite pins it here.
            // `foliage_type` used to be the unknown column in this case; it is known now, so the
            // rule needs a column that is still unknown or nothing tests it at all.
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m,foliage_type,canopy_cover
                472195.00,4257585.00,2006.71,3.38,1.18,shrub,0.62
                471835.00,4257485.00,1985.45,3.10,1.08,tree,0.44
                """,
                MetricFrame,
                FoliageTypes.KnownVocabulary);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points.Count, 2, "both points, the unknown column notwithstanding");
            run.Within(parse.Points[0].HeightM, 3.38, 1e-9, "height still read by name");
            run.Equal(parse.Points[0].FoliageType, FoliageType.Shrub, "and the column beside it still maps");
            run.Equal(parse.UnknownFoliageValues, 0, "an unknown COLUMN is not an unknown VALUE");
        });

        run.Case("the published foliage type rides on the point, mapped and never inferred", () =>
        {
            // spec/format.md §4.4: vocabulary "1" is `tree` and `shrub`, and the platform owns the
            // classification. The shrub here is the TALLER of the two on purpose — a reader that
            // guessed from height_m would get this case backwards.
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m,foliage_type
                472195.00,4257585.00,2006.71,3.38,1.18,shrub
                471835.00,4257485.00,1985.45,3.10,1.08,tree
                """,
                MetricFrame,
                FoliageTypes.KnownVocabulary);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points.Count, 2, "both points");
            run.Equal(parse.Points[0].FoliageType, FoliageType.Shrub, "the shrub is a shrub");
            run.Equal(parse.Points[1].FoliageType, FoliageType.Tree, "the tree is a tree");
            run.Within(parse.Points[0].HeightM, 3.38, 1e-9, "height still read by name");
            run.Within(parse.Points[1].CrownRadiusM, 1.08, 1e-9, "crown radius still read by name");
            run.Equal(parse.Notes.Count, 0, "a bundle that says what it means needs no note");
        });

        run.Case("a foliage type this add-in does not know reads as a tree, and is counted", () =>
        {
            // ⛔ spec/format.md §4.4: a host MUST read a value it does not know as `tree`. The
            // vocabulary grows by adding values, never by changing what an existing one means.
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m,foliage_type
                472195.00,4257585.00,2006.71,3.38,1.18,hedgerow
                471835.00,4257485.00,1985.45,3.10,1.08,shrub
                """,
                MetricFrame,
                FoliageTypes.KnownVocabulary);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points.Count, 2, "an unknown value costs no point");
            run.Equal(parse.Points[0].FoliageType, FoliageType.Tree, "hedgerow reads as a tree");
            run.Equal(parse.Points[1].FoliageType, FoliageType.Shrub, "the value beside it keeps its meaning");
            run.Equal(parse.UnknownFoliageValues, 1, "counted, so the log can say so");
            run.Equal(parse.EmptyFoliageCells, 0, "an unknown value is not an empty cell");
        });

        run.Case("an empty foliage cell reads as a tree, counted apart from an unknown value", () =>
        {
            // The two are different failures: an empty cell is an ETL that did not classify the
            // point, an unknown value is a vocabulary this build has not caught up with. Rolling
            // them into one number would hide whichever is rarer.
            // The foliage column sits in the MIDDLE here on purpose: the second row's cell is
            // whitespace, and as a trailing cell it would be stripped by the whole-line trim before
            // the cell reader ever saw it — the case would pass without exercising anything.
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,foliage_type,height_m,crown_radius_m
                472195.00,4257585.00,2006.71,,3.38,1.18
                471835.00,4257485.00,1985.45,   ,3.10,1.08
                """,
                MetricFrame,
                FoliageTypes.KnownVocabulary);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points.Count, 2, "an empty foliage cell costs no point, unlike an empty ground_z");
            run.Equal(parse.Points[0].FoliageType, FoliageType.Tree, "empty reads as a tree");
            run.Equal(parse.Points[1].FoliageType, FoliageType.Tree, "whitespace is empty");
            run.Equal(parse.EmptyFoliageCells, 2, "both counted");
            run.Equal(parse.UnknownFoliageValues, 0, "empty is not unknown");
        });

        run.Case("a row short of the foliage column is a tree, not a dropped row", () =>
        {
            // The five columns that make geometry are required; the sixth is not. A row that stops
            // before it is a 1.1.0-shaped row inside a 1.2.0 file, which is a tree.
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m,foliage_type
                472195.00,4257585.00,2006.71,3.38,1.18
                """,
                MetricFrame,
                FoliageTypes.KnownVocabulary);

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
            // spec/format.md §4.4: "A manifest without `foliage_type_vocabulary` has no
            // `foliage_type` column; read every point as a tree." Nothing is wrong, so nothing is said.
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m
                472195.00,4257585.00,2006.71,3.38,1.18
                """,
                MetricFrame,
                foliageVocabulary: null);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points[0].FoliageType, FoliageType.Tree, "every point is a tree");
            run.Equal(parse.Notes.Count, 0, "the ordinary older bundle says nothing");
        });

        run.Case("a vocabulary this add-in does not know still maps the values it knows", () =>
        {
            // ⛔ spec/format.md §4.4: a vocabulary the host does not know is NO reason to ignore the
            // column. A new value is a new vocabulary version, never a change of meaning for an
            // existing one — so `shrub` is still a shrub under vocabulary "2".
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m,foliage_type
                472195.00,4257585.00,2006.71,3.38,1.18,shrub
                471835.00,4257485.00,1985.45,3.10,1.08,espalier
                """,
                MetricFrame,
                foliageVocabulary: "2");

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points[0].FoliageType, FoliageType.Shrub, "a known value keeps its meaning");
            run.Equal(parse.Points[1].FoliageType, FoliageType.Tree, "the vocabulary's new value reads as a tree");
            run.Equal(parse.UnknownFoliageValues, 1, "and is counted");
            run.Equal(parse.Notes.Count, 1, "said once, not once per row");
            run.Contains(parse.Notes[0], "newer", "the note says the vocabulary is newer than the add-in");
        });

        run.Case("a column with no vocabulary to read it by is ignored, and said once", () =>
        {
            // The manifest is the authority on what the values mean. Without it they are
            // uninterpretable — but a silent drop would hide the publisher's mistake.
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m,foliage_type
                472195.00,4257585.00,2006.71,3.38,1.18,shrub
                """,
                MetricFrame,
                foliageVocabulary: null);

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
                FoliageTypes.KnownVocabulary);

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
                FoliageTypes.KnownVocabulary);

            run.True(parse.Failure is null, $"parsed: {parse.Failure}");
            run.Equal(parse.Points[0].FoliageType, FoliageType.Shrub, "first column, still the foliage type");
            run.Within(parse.Points[0].HeightM, 3.38, 1e-9, "and the geometry columns are unmoved");
        });

        run.Case("the vocabulary's values are matched exactly, not by case or by prefix", () =>
        {
            // The vocabulary is closed and the platform owns it. Accepting `Shrub` or `shrubland`
            // would be this add-in inventing a value the platform never published.
            TreePointsParse parse = TreePointsReader.Parse(
                """
                x,y,ground_z,height_m,crown_radius_m,foliage_type
                472195.00,4257585.00,2006.71,3.38,1.18,Shrub
                471835.00,4257485.00,1985.45,3.10,1.08,shrubland
                """,
                MetricFrame,
                FoliageTypes.KnownVocabulary);

            run.Equal(parse.Points[0].FoliageType, FoliageType.Tree, "`Shrub` is not `shrub`");
            run.Equal(parse.Points[1].FoliageType, FoliageType.Tree, "`shrubland` is not `shrub`");
            run.Equal(parse.UnknownFoliageValues, 2, "both counted as unknown");
        });
    }
}
