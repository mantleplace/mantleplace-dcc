using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The shared vector carrier's decoder and reader, on a synthetic carrier built in the test. The pure
/// half of the carrier read: it runs on every runner, because the SQLite half that fetches the rows is
/// Windows-only (<c>GeoPackageFileTests</c>).
/// </summary>
/// <remarks>
/// The bar is parity: the carrier replaces a GeoJSON file, so the features it yields must be the ones
/// <see cref="SiteVectorReader"/> yields for the same layer, every attribute the importer reads
/// included (<c>revit/docs/vector-carrier.md</c>).
/// </remarks>
internal static class GeoPackageReaderTests
{
    private const int Srs = 6605;

    private static readonly SiteFrame Frame = new()
    {
        Origin = new GeoOrigin { Epsg = Srs, Easting = 1000.0, Northing = 2000.0, LinearUnit = LinearUnit.UsSurveyFoot },
    };

    private static readonly LayerFrame Layer = new(LayerCoordinates.AbsoluteProjected, LinearUnit.UsSurveyFoot);

    private const double UsFootM = 1200.0 / 3937.0;

    private const SiteGeometryKinds Both = SiteGeometryKinds.Lines | SiteGeometryKinds.Areas;

    internal static int Run()
    {
        TestRun run = new();

        RunDecoderCases(run);
        RunReaderCases(run);

        return run.Report("GeoPackage carrier reader");
    }

    private static void RunDecoderCases(TestRun run)
    {
        run.Case("a blob with an envelope decodes to the rings of its WKB, the hole marked", () =>
        {
            byte[] park = GeoPackageFixture.Polygon(
                GeoPackageFixture.Square(1100, 2100, 100),
                [1120, 2120, 1140, 2120, 1140, 2140, 1120, 2120]);

            IReadOnlyList<GeoPackagePart> parts = GeoPackageGeometry.Decode(GeoPackageFixture.Blob(Srs, park, envelope: 1));

            run.Equal(parts.Count, 2, "two rings");
            run.True(parts[0].Closed && !parts[0].IsHole, "the exterior ring is not a hole");
            run.True(parts[1].Closed && parts[1].IsHole, "the second ring is a hole");
            run.Equal(parts[0].Positions.Count, 5, "WKB repeats the closing position, which the reader drops later");
            run.Equal(parts[0].Polygon, 1, "the polygon ordinal");
            run.Equal(parts[1].Polygon, 1, "a hole belongs to its polygon");
        });

        run.Case("every envelope kind the header can state is skipped", () =>
        {
            byte[] line = GeoPackageFixture.LineStringZ(1010, 2010, 600.5, 1020, 2030, 601.25);
            foreach (int envelope in new[] { 0, 1, 2, 3, 4 })
            {
                IReadOnlyList<GeoPackagePart> parts = GeoPackageGeometry.Decode(GeoPackageFixture.Blob(Srs, line, envelope));
                run.Equal(parts.Count, 1, $"envelope code {envelope}: one line");
            }
        });

        run.Case("a 3-D line keeps its Z, and an M ordinate is stepped over rather than read as Z", () =>
        {
            IReadOnlyList<GeoPackagePart> z = GeoPackageGeometry.Decode(
                GeoPackageFixture.Blob(Srs, GeoPackageFixture.LineStringZ(1010, 2010, 600.5, 1020, 2030, 601.25)));
            run.Within(z[0].Positions[1].Z ?? double.NaN, 601.25, 0.0, "the second Z");
            run.Within(z[0].Positions[1].Y, 2030.0, 0.0, "the second Y");

            IReadOnlyList<GeoPackagePart> zm = GeoPackageGeometry.Decode(
                GeoPackageFixture.Blob(Srs, GeoPackageFixture.LineStringZm(1010, 2010, 600.5, 9999, 1020, 2030, 601.25, 9999)));
            run.Equal(zm[0].Positions.Count, 2, "both positions read");
            run.Within(zm[0].Positions[1].Z ?? double.NaN, 601.25, 0.0, "Z is the third ordinate, not the M");
            run.Within(zm[0].Positions[1].X, 1020.0, 0.0, "X is unmoved by the M before it");
        });

        run.Case("a planar geometry has no Z, unknown rather than zero", () =>
        {
            IReadOnlyList<GeoPackagePart> parts = GeoPackageGeometry.Decode(
                GeoPackageFixture.Blob(Srs, GeoPackageFixture.MultiLineString([1010, 2010, 1020, 2030])));
            run.True(parts[0].Positions[0].Z is null, "no third ordinate, no height");
        });

        run.Case("polygons are numbered across the rows of a layer, so a hole knows which polygon it is in", () =>
        {
            byte[] two = GeoPackageFixture.MultiPolygon(
                [GeoPackageFixture.Square(0, 0, 10)],
                [GeoPackageFixture.Square(100, 100, 10), [102, 102, 104, 102, 104, 104, 102, 102]]);
            byte[] one = GeoPackageFixture.Polygon(GeoPackageFixture.Square(200, 200, 10));

            int polygons = 0;
            IReadOnlyList<GeoPackagePart> first = GeoPackageGeometry.Decode(GeoPackageFixture.Blob(Srs, two), ref polygons);
            IReadOnlyList<GeoPackagePart> second = GeoPackageGeometry.Decode(GeoPackageFixture.Blob(Srs, one), ref polygons);

            run.Equal(first.Count, 3, "two rings of the second polygon and one of the first");
            run.Equal(first[0].Polygon, 1, "the first polygon");
            run.Equal(first[1].Polygon, 2, "the second polygon's exterior");
            run.Equal(first[2].Polygon, 2, "the second polygon's hole");
            run.Equal(second[0].Polygon, 3, "the next row's polygon continues the count");
        });

        run.Case("what is not linework yields nothing, and what cannot be followed yields nothing without throwing", () =>
        {
            run.Equal(GeoPackageGeometry.Decode(GeoPackageFixture.Blob(Srs, GeoPackageFixture.Point(1, 2))).Count, 0, "a point");
            run.Equal(
                GeoPackageGeometry.Decode(GeoPackageFixture.Blob(Srs, GeoPackageFixture.Polygon(GeoPackageFixture.Square(0, 0, 1)), empty: true)).Count,
                0,
                "the empty-geometry flag");

            byte[] whole = GeoPackageFixture.Blob(Srs, GeoPackageFixture.LineStringZ(1, 2, 3, 4, 5, 6));
            run.Equal(GeoPackageGeometry.Decode(whole[..^10]).Count, 0, "a truncated blob");
            run.Equal(GeoPackageGeometry.Decode([]).Count, 0, "an empty blob");
            run.Equal(GeoPackageGeometry.Decode([(byte)'X', (byte)'X', 0, 1, 0, 0, 0, 0, 1, 2, 3]).Count, 0, "no GP magic");

            byte[] absurd = GeoPackageFixture.Blob(Srs, GeoPackageFixture.LineStringZ(1, 2, 3));
            absurd[8 + 32 + 5] = 0xFF; // the position count, far past the bytes there are
            absurd[8 + 32 + 6] = 0xFF;
            absurd[8 + 32 + 7] = 0xFF;
            run.Equal(GeoPackageGeometry.Decode(absurd).Count, 0, "a position count the blob cannot hold");
        });
    }

    private static void RunReaderCases(TestRun run)
    {
        run.Case("a carrier table reads as the GeoJSON layer does, every attribute the importer reads included", () =>
        {
            byte[] road = GeoPackageFixture.LineStringZ(1010, 2010, 600.5, 1020, 2030, 601.25, 1040, 2035, 602);
            byte[] park = GeoPackageFixture.Polygon(
                GeoPackageFixture.Square(1100, 2100, 100),
                [1120, 2120, 1140, 2120, 1140, 2140, 1120, 2120]);

            GeoPackageTable table = Table(
                Srs,
                Row(road, ("name", "Main St"), ("class", "primary"), ("width_m_estimated", "7.5"), ("subtype", "road"), ("lowest_level", "MED")),
                Row(park, ("class", "park"), ("subtype", "recreation"), ("fld_zone", "AE"), ("zone_subty", "FLOODWAY"), ("threshold_deg", GeoPackageText.Real(30.0))));

            string? error = SiteGeoPackageReader.TryRead(table, Frame, Layer, Both, "test", out IReadOnlyList<SiteFeature> actual, "lowest_level");
            run.True(error is null, $"read: {error}");

            const string GeoJson = """
                {"type":"FeatureCollection","features":[
                 {"type":"Feature","properties":{"name":"Main St","class":"primary","width_m_estimated":7.5,"subtype":"road","lowest_level":"MED"},
                  "geometry":{"type":"LineString","coordinates":[[1010,2010,600.5],[1020,2030,601.25],[1040,2035,602]]}},
                 {"type":"Feature","properties":{"class":"park","subtype":"recreation","threshold_deg":30.0,"fld_zone":"AE","zone_subty":"FLOODWAY"},
                  "geometry":{"type":"Polygon","coordinates":[
                    [[1100,2100],[1200,2100],[1200,2200],[1100,2200],[1100,2100]],
                    [[1120,2120],[1140,2120],[1140,2140],[1120,2120]]]}}
                ]}
                """;
            string? baselineError = SiteVectorReader.TryParse(GeoJson, Frame, Layer, Both, "test", out IReadOnlyList<SiteFeature> expected, "lowest_level");
            run.True(baselineError is null, $"baseline: {baselineError}");

            SameFeatures(run, expected, actual);
            run.Equal(actual.Count, 3, "a road and the two rings of the park");
            run.Within(actual[0].Vertices[0].EastM, 10.0 * UsFootM, 1e-9, "the first vertex is subtracted from the origin in feet, then metres");
            run.Within(actual[0].Vertices[1].ElevationM ?? double.NaN, 601.25 * UsFootM, 1e-9, "Z is feet, stored as metres");
        });

        run.Case("a ring loses its repeated closing position, and a line or ring too short to draw is no feature", () =>
        {
            GeoPackageTable table = Table(
                Srs,
                Row(GeoPackageFixture.Polygon(GeoPackageFixture.Square(1100, 2100, 100))),
                Row(GeoPackageFixture.Polygon([1100, 2100, 1200, 2100, 1100, 2100])),
                Row(GeoPackageFixture.LineStringZ(1010, 2010, 600)));

            string? error = SiteGeoPackageReader.TryRead(table, Frame, Layer, Both, "test", out IReadOnlyList<SiteFeature> features);

            run.True(error is null, $"read: {error}");
            run.Equal(features.Count, 1, "only the square survives");
            run.Equal(features[0].Vertices.Count, 4, "the closing position is dropped, a CurveLoop closes itself");
        });

        run.Case("the caller says which geometry it wants, and gets no other", () =>
        {
            GeoPackageTable table = Table(
                Srs,
                Row(GeoPackageFixture.LineStringZ(1010, 2010, 600, 1020, 2030, 601)),
                Row(GeoPackageFixture.Polygon(GeoPackageFixture.Square(1100, 2100, 100))));

            SiteGeoPackageReader.TryRead(table, Frame, Layer, SiteGeometryKinds.Lines, "test", out IReadOnlyList<SiteFeature> lines);
            SiteGeoPackageReader.TryRead(table, Frame, Layer, SiteGeometryKinds.Areas, "test", out IReadOnlyList<SiteFeature> areas);

            run.Equal(lines.Count, 1, "lines");
            run.False(lines[0].IsClosed, "the line is open");
            run.Equal(areas.Count, 1, "areas");
            run.True(areas[0].IsClosed, "the ring is closed");
        });

        run.Case("a NULL or missing column reads as empty and a non-numeric width as none", () =>
        {
            GeoPackageTable table = Table(
                Srs,
                Row(GeoPackageFixture.LineStringZ(1010, 2010, 600, 1020, 2030, 601), ("name", "A"), ("width_m_estimated", "wide")),
                Row(GeoPackageFixture.LineStringZ(1010, 2010, 600, 1020, 2030, 601)));

            SiteGeoPackageReader.TryRead(table, Frame, Layer, SiteGeometryKinds.Lines, "test", out IReadOnlyList<SiteFeature> features, "lowest_level");

            run.Equal(features[0].Name, "A", "a stated name");
            run.True(features[0].WidthM is null, "a width that is not a number is unknown, not zero");
            run.Equal(features[1].Name, string.Empty, "a name the row does not carry");
            run.Equal(features[1].Subtype, string.Empty, "a subtype the row does not carry");
            run.Equal(features[1].FloodZone, string.Empty, "a flood zone the row does not carry");
            run.True(features[1].LowestLevel is null, "no level field on the row is no level, not an empty one");
        });

        run.Case("a REAL threshold reads the way the manifest wrote the number, so one label does not split in two", () =>
        {
            run.Equal(GeoPackageText.Real(35.0), "35.0", "an integral REAL keeps its point");
            run.Equal(GeoPackageText.Real(35.5), "35.5", "a fraction is itself");
            run.Equal(GeoPackageText.Real(-0.25), "-0.25", "a negative");

            IReadOnlyList<KeyRow> rows = ZoneKey.SteepRows(
                [
                    new SiteFeature { Vertices = [], IsClosed = true, Threshold = GeoPackageText.Real(35.0) },
                    new SiteFeature { Vertices = [], IsClosed = true },
                ],
                manifestThreshold: "35.0");
            run.Equal(rows.Count, 1, "the carrier's threshold and the manifest's name one row");
            run.Equal(rows[0].Text, "Steep ground — at or above 35.0°", "the published number");
        });

        run.Case("a table stored in another CRS is refused with the reason, never reprojected", () =>
        {
            byte[] line = GeoPackageFixture.LineStringZ(1010, 2010, 600, 1020, 2030, 601);

            string? degrees = SiteGeoPackageReader.TryRead(
                new GeoPackageTable("water", "EPSG", 4326, [Row(line)]), Frame, Layer, SiteGeometryKinds.Lines, "water bodies", out IReadOnlyList<SiteFeature> a);
            run.Contains(degrees, "EPSG:4326", "the file's CRS is named");
            run.Contains(degrees, $"EPSG:{Srs}", "so is this project's");
            run.Contains(degrees, "water bodies", "in the curator's words");
            run.Equal(a.Count, 0, "nothing placed");

            string? undefined = SiteGeoPackageReader.TryRead(
                new GeoPackageTable("water", "NONE", -1, [Row(line)]), Frame, Layer, SiteGeometryKinds.Lines, "water bodies", out IReadOnlyList<SiteFeature> b);
            run.Contains(undefined, "NONE:-1", "a file that names no CRS is not taken on trust");
            run.Equal(b.Count, 0, "nothing placed");

            string? sameCode = SiteGeoPackageReader.TryRead(
                new GeoPackageTable("water", "ESRI", Srs, [Row(line)]), Frame, Layer, SiteGeometryKinds.Lines, "water bodies", out IReadOnlyList<SiteFeature> c);
            run.True(sameCode is not null && c.Count == 0, "the same number under another authority is another CRS");
        });

        run.Case("rows with no geometry are passed over and the rest of the layer survives", () =>
        {
            byte[] good = GeoPackageFixture.LineStringZ(1010, 2010, 600, 1020, 2030, 601);
            byte[] torn = GeoPackageFixture.Blob(Srs, GeoPackageFixture.LineStringZ(1, 2, 3, 4, 5, 6))[..^6];

            GeoPackageTable table = new(
                "road_splines",
                "EPSG",
                Srs,
                [new GeoPackageRow(null, new Dictionary<string, string>()), new GeoPackageRow(torn, new Dictionary<string, string>()), Row(good)]);

            string? error = SiteGeoPackageReader.TryRead(table, Frame, Layer, SiteGeometryKinds.Lines, "test", out IReadOnlyList<SiteFeature> features);

            run.True(error is null, $"read: {error}");
            run.Equal(features.Count, 1, "one malformed row does not cost the others");
        });
    }

    private static GeoPackageRow Row(byte[] geometry, params (string Name, string Value)[] attributes)
        => new(
            WithHeader(geometry),
            attributes.ToDictionary(attribute => attribute.Name, attribute => attribute.Value, StringComparer.Ordinal));

    /// <summary>A row's geometry is a blob; a bare WKB from the fixture is wrapped in the header.</summary>
    private static byte[] WithHeader(byte[] geometry)
        => geometry.Length >= 2 && geometry[0] == (byte)'G' && geometry[1] == (byte)'P'
            ? geometry
            : GeoPackageFixture.Blob(Srs, geometry);

    private static GeoPackageTable Table(int srs, params GeoPackageRow[] rows) => new("layer", "EPSG", srs, rows);

    private static void SameFeatures(TestRun run, IReadOnlyList<SiteFeature> expected, IReadOnlyList<SiteFeature> actual)
    {
        run.Equal(actual.Count, expected.Count, "feature count");
        for (int index = 0; index < Math.Min(expected.Count, actual.Count); index++)
        {
            SiteFeature e = expected[index];
            SiteFeature a = actual[index];
            string at = $"feature {index}";
            run.Equal(a.IsClosed, e.IsClosed, at + " IsClosed");
            run.Equal(a.IsHole, e.IsHole, at + " IsHole");
            run.Equal(a.PolygonOrdinal, e.PolygonOrdinal, at + " PolygonOrdinal");
            run.Equal(a.Name, e.Name, at + " name");
            run.Equal(a.Classification, e.Classification, at + " class");
            run.True(a.WidthM == e.WidthM, at + " width_m_estimated");
            run.Equal(a.Subtype, e.Subtype, at + " subtype");
            run.Equal(a.FloodZone, e.FloodZone, at + " fld_zone");
            run.Equal(a.FloodZoneSubtype, e.FloodZoneSubtype, at + " zone_subty");
            run.Equal(a.Threshold, e.Threshold, at + " threshold_deg");
            run.Equal(a.LowestLevel, e.LowestLevel, at + " lowest_level");
            run.Equal(a.Vertices.Count, e.Vertices.Count, at + " vertex count");
            for (int vertex = 0; vertex < Math.Min(e.Vertices.Count, a.Vertices.Count); vertex++)
            {
                run.Within(a.Vertices[vertex].EastM, e.Vertices[vertex].EastM, 1e-9, $"{at} vertex {vertex} east");
                run.Within(a.Vertices[vertex].NorthM, e.Vertices[vertex].NorthM, 1e-9, $"{at} vertex {vertex} north");
                run.True(a.Vertices[vertex].ElevationM.HasValue == e.Vertices[vertex].ElevationM.HasValue, $"{at} vertex {vertex} has Z alike");
                run.Within(a.Vertices[vertex].ElevationM ?? 0.0, e.Vertices[vertex].ElevationM ?? 0.0, 1e-9, $"{at} vertex {vertex} Z");
            }
        }
    }
}
