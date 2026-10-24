using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The SQLite half of the carrier read, on a real GeoPackage file written by the test, read through the
/// <c>winsqlite3.dll</c> Windows ships and checked against the GeoJSON reader for the same layer.
/// </summary>
/// <remarks>
/// Windows only, because the library is: the cases are skipped elsewhere (the pure half, in
/// <see cref="GeoPackageReaderTests"/>, runs everywhere). No file is checked in; each case writes its
/// carrier to a temp path and removes it.
/// </remarks>
internal static class GeoPackageFileTests
{
    private const int Srs = 6605;

    private static readonly SiteFrame Frame = new()
    {
        Origin = new GeoOrigin { Epsg = Srs, Easting = 1000.0, Northing = 2000.0, LinearUnit = LinearUnit.UsSurveyFoot },
    };

    private static readonly LayerFrame Layer = new(LayerCoordinates.AbsoluteProjected, LinearUnit.UsSurveyFoot);

    private const SiteGeometryKinds Both = SiteGeometryKinds.Lines | SiteGeometryKinds.Areas;

    internal static int Run()
    {
        TestRun run = new();

        if (!OperatingSystem.IsWindows())
        {
            // The SQLite this host reads a GeoPackage with is Windows'; nothing to run elsewhere.
            return run.Report("GeoPackage file (skipped: not Windows)");
        }

        RunWindowsCases(run);
        return run.Report("GeoPackage file");
    }

    [SupportedOSPlatform("windows")]
    private static void RunWindowsCases(TestRun run)
    {
        run.Case("a table of a real GeoPackage reads as the GeoJSON layer does, every attribute the importer reads included", () =>
        {
            byte[] road = GeoPackageFixture.LineStringZ(1010, 2010, 600.5, 1020, 2030, 601.25, 1040, 2035, 602);
            byte[] park = GeoPackageFixture.Polygon(
                GeoPackageFixture.Square(1100, 2100, 100),
                [1120, 2120, 1140, 2120, 1140, 2140, 1120, 2120]);

            WithCarrier(
                Srs,
                "land_use",
                [
                    Insert(road, "'Main St'", "'primary'", "7.5", "'road'", "NULL", "'MED'"),
                    Insert(park, "NULL", "'park'", "NULL", "'recreation'", "30.0", "NULL", "'AE'", "'FLOODWAY'"),
                ],
                path =>
                {
                    string? opened = GeoPackageFile.TryReadTable(path, "land_use", "land use", out GeoPackageTable? table);
                    run.True(opened is null && table is not null, $"opened: {opened}");
                    run.Equal(table!.Organization, "EPSG", "the CRS the file registers");
                    run.True(table.CrsCode == Srs, "the CRS code");
                    run.Equal(table.Rows.Count, 2, "both rows");
                    run.False(table.Rows[0].Attributes.ContainsKey("threshold_deg"), "a NULL is absent, not empty");
                    run.Equal(table.Rows[1].Attributes["threshold_deg"], "30.0", "a REAL reads as the manifest would write the number");
                    run.False(table.Rows[0].Attributes.ContainsKey("geom"), "the geometry column is the geometry, not an attribute");

                    string? error = SiteGeoPackageReader.TryRead(table, Frame, Layer, Both, "land use", out IReadOnlyList<SiteFeature> actual, "lowest_level");
                    run.True(error is null, $"read: {error}");

                    string? baselineError = SiteVectorReader.TryParse(GeoJson, Frame, Layer, Both, "land use", out IReadOnlyList<SiteFeature> expected, "lowest_level");
                    run.True(baselineError is null, $"baseline: {baselineError}");

                    run.Equal(actual.Count, expected.Count, "feature count");
                    for (int index = 0; index < Math.Min(actual.Count, expected.Count); index++)
                    {
                        SiteFeature a = actual[index];
                        SiteFeature e = expected[index];
                        run.Equal(a.Name, e.Name, $"feature {index} name");
                        run.Equal(a.Classification, e.Classification, $"feature {index} class");
                        run.True(a.WidthM == e.WidthM, $"feature {index} width_m_estimated");
                        run.Equal(a.Subtype, e.Subtype, $"feature {index} subtype");
                        run.Equal(a.FloodZone, e.FloodZone, $"feature {index} fld_zone");
                        run.Equal(a.FloodZoneSubtype, e.FloodZoneSubtype, $"feature {index} zone_subty");
                        run.Equal(a.Threshold, e.Threshold, $"feature {index} threshold_deg");
                        run.Equal(a.LowestLevel, e.LowestLevel, $"feature {index} lowest_level");
                        run.Equal(a.IsHole, e.IsHole, $"feature {index} IsHole");
                        run.Equal(a.PolygonOrdinal, e.PolygonOrdinal, $"feature {index} PolygonOrdinal");
                        run.Equal(a.Vertices.Count, e.Vertices.Count, $"feature {index} vertex count");
                        for (int vertex = 0; vertex < Math.Min(a.Vertices.Count, e.Vertices.Count); vertex++)
                        {
                            run.Within(a.Vertices[vertex].EastM, e.Vertices[vertex].EastM, 1e-9, $"feature {index} vertex {vertex} east");
                            run.Within(a.Vertices[vertex].NorthM, e.Vertices[vertex].NorthM, 1e-9, $"feature {index} vertex {vertex} north");
                            run.Within(a.Vertices[vertex].ElevationM ?? -1e9, e.Vertices[vertex].ElevationM ?? -1e9, 1e-9, $"feature {index} vertex {vertex} Z");
                        }
                    }
                });
        });

        run.Case("a table is found without regard to case", () =>
        {
            WithCarrier(
                Srs,
                "Road_Splines",
                [Insert(GeoPackageFixture.LineStringZ(1010, 2010, 600, 1020, 2030, 601), "NULL", "NULL", "NULL", "NULL", "NULL", "NULL")],
                path =>
                {
                    string? opened = GeoPackageFile.TryReadTable(path, "road_splines", "road centrelines", out GeoPackageTable? table);
                    run.True(opened is null && table is { Rows.Count: 1 }, $"opened: {opened}");
                });
        });

        run.Case("a file of one table is read whatever the table is named, where the row lists no tables", () =>
        {
            WithCarrier(
                Srs,
                "RoadSplines",
                [Insert(GeoPackageFixture.LineStringZ(1010, 2010, 600, 1020, 2030, 601), "'Main St'", "NULL", "NULL", "NULL", "NULL", "NULL")],
                path =>
                {
                    string? opened = GeoPackageFile.TryReadTable(path, "road_splines", "road centrelines", out GeoPackageTable? table, soleTable: true);
                    run.True(opened is null && table is { Rows.Count: 1 }, $"opened: {opened}");
                    run.Equal(table?.Name, "RoadSplines", "the table the file names");

                    string? refused = GeoPackageFile.TryReadTable(path, "road_splines", "road centrelines", out GeoPackageTable? none);
                    run.Contains(refused, "road_splines", "a row that lists tables is still read by name: the reason names the table");
                    run.True(none is null, "and no table is read in its place");
                });
        });

        run.Case("a file of several tables is never read as its only one", () =>
        {
            WithCarrier(
                Srs,
                "RoadSplines",
                [Insert(GeoPackageFixture.LineStringZ(1010, 2010, 600, 1020, 2030, 601), "NULL", "NULL", "NULL", "NULL", "NULL", "NULL")],
                path =>
                {
                    FixtureSqlite.Execute(path, $"INSERT INTO gpkg_geometry_columns VALUES ('Water', 'geom', 'GEOMETRY', {Srs}, 1, 0);");
                    string? reason = GeoPackageFile.TryReadTable(path, "road_splines", "road centrelines", out GeoPackageTable? table, soleTable: true);
                    run.Contains(reason, "road_splines", "the reason names the table");
                    run.True(table is null, "no table is guessed");
                });
        });

        run.Case("a file whose table is in another CRS is read as that CRS, so the reader can refuse it by name", () =>
        {
            WithCarrier(
                4326,
                "water",
                [Insert(GeoPackageFixture.LineStringZ(1010, 2010, 600, 1020, 2030, 601), "NULL", "NULL", "NULL", "NULL", "NULL", "NULL")],
                path =>
                {
                    run.True(GeoPackageFile.TryReadTable(path, "water", "water bodies", out GeoPackageTable? table) is null && table is not null, "opened");
                    string? refused = SiteGeoPackageReader.TryRead(table!, Frame, Layer, Both, "water bodies", out IReadOnlyList<SiteFeature> features);
                    run.Contains(refused, "EPSG:4326", "the reason names the CRS the file holds");
                    run.Equal(features.Count, 0, "nothing placed");
                });
        });

        run.Case("a table the file lacks is a stated reason, not zero features", () =>
        {
            WithCarrier(
                Srs,
                "water",
                [Insert(GeoPackageFixture.LineStringZ(1010, 2010, 600, 1020, 2030, 601), "NULL", "NULL", "NULL", "NULL", "NULL", "NULL")],
                path =>
                {
                    string? reason = GeoPackageFile.TryReadTable(path, "flood_zones", "flood zones", out GeoPackageTable? table);
                    run.Contains(reason, "flood_zones", "the reason names the table");
                    run.Contains(reason, "flood zones", "and the layer in the curator's words");
                    run.True(table is null, "no table");
                });
        });

        run.Case("a file that is not a GeoPackage is a stated reason, and a missing file too", () =>
        {
            string notDatabase = Path.Combine(Path.GetTempPath(), $"mantleplace-carrier-{Guid.NewGuid():N}.gpkg");
            try
            {
                File.WriteAllText(notDatabase, "this is GeoJSON, or something, but it is not a database " + new string('x', 200));
                string? reason = GeoPackageFile.TryReadTable(notDatabase, "water", "water bodies", out GeoPackageTable? table);
                run.Contains(reason, "water bodies", "the layer is named");
                run.True(table is null, "no table");

                string? missing = GeoPackageFile.TryReadTable(notDatabase + ".absent", "water", "water bodies", out GeoPackageTable? none);
                run.True(missing is not null && none is null, "an absent file is refused, not created");
                run.False(File.Exists(notDatabase + ".absent"), "and not created by being opened");
            }
            finally
            {
                File.Delete(notDatabase);
            }
        });
    }

    private const string GeoJson = """
        {"type":"FeatureCollection","features":[
         {"type":"Feature","properties":{"name":"Main St","class":"primary","width_m_estimated":7.5,"subtype":"road","lowest_level":"MED"},
          "geometry":{"type":"LineString","coordinates":[[1010,2010,600.5],[1020,2030,601.25],[1040,2035,602]]}},
         {"type":"Feature","properties":{"class":"park","subtype":"recreation","threshold_deg":30.0,"fld_zone":"AE","zone_subty":"FLOODWAY"},
          "geometry":{"type":"Polygon","coordinates":[
            [[1100,2100],[1200,2100],[1200,2200],[1100,2200],[1100,2100]],
            [[1120,2120],[1140,2120],[1140,2140],[1120,2120]]]}}
        ]}
        """;

    /// <summary>
    /// One row's <c>VALUES</c> tuple, in the fixture table's column order after the geometry: name, class,
    /// width, subtype, threshold, lowest level, flood zone, zone subtype. Each value is SQL (<c>NULL</c>, a
    /// number or a quoted string), and a column left off is <c>NULL</c>.
    /// </summary>
    private static string Insert(byte[] wkb, params string[] values)
    {
        string[] columns = [.. values, .. Enumerable.Repeat("NULL", Math.Max(0, 8 - values.Length))];
        return $"({GeoPackageFixture.Hex(GeoPackageFixture.Blob(Srs, wkb))}, {string.Join(", ", columns)})";
    }

    [SupportedOSPlatform("windows")]
    private static void WithCarrier(int srs, string table, string[] rows, Action<string> body)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mantleplace-carrier-{Guid.NewGuid():N}.gpkg");
        try
        {
            string sql = $"""
                CREATE TABLE gpkg_spatial_ref_sys (srs_name TEXT NOT NULL, srs_id INTEGER NOT NULL PRIMARY KEY, organization TEXT NOT NULL, organization_coordsys_id INTEGER NOT NULL, definition TEXT NOT NULL, description TEXT);
                INSERT INTO gpkg_spatial_ref_sys VALUES ('fixture', {srs.ToString(CultureInfo.InvariantCulture)}, 'EPSG', {srs.ToString(CultureInfo.InvariantCulture)}, 'undefined', NULL);
                CREATE TABLE gpkg_geometry_columns (table_name TEXT NOT NULL, column_name TEXT NOT NULL, geometry_type_name TEXT NOT NULL, srs_id INTEGER NOT NULL, z TINYINT NOT NULL, m TINYINT NOT NULL);
                INSERT INTO gpkg_geometry_columns VALUES ('{table}', 'geom', 'GEOMETRY', {srs.ToString(CultureInfo.InvariantCulture)}, 1, 0);
                CREATE TABLE "{table}" (fid INTEGER PRIMARY KEY AUTOINCREMENT, geom BLOB, name TEXT, class TEXT, width_m_estimated REAL, subtype TEXT, threshold_deg REAL, lowest_level TEXT, fld_zone TEXT, zone_subty TEXT);
                INSERT INTO "{table}" (geom, name, class, width_m_estimated, subtype, threshold_deg, lowest_level, fld_zone, zone_subty) VALUES {string.Join(", ", rows)};
                """;
            FixtureSqlite.Execute(path, sql);
            body(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The three calls a test needs to write a database; the product only reads one.</summary>
    [SupportedOSPlatform("windows")]
    private static class FixtureSqlite
    {
        private const string Library = "winsqlite3.dll";

        internal static void Execute(string path, string sql)
        {
            byte[] file = Utf8(path);
            int code = sqlite3_open_v2(file, out IntPtr db, 0x6, IntPtr.Zero); // read-write | create
            try
            {
                if (code != 0)
                {
                    throw new InvalidOperationException($"fixture: SQLite could not create {path} (code {code})");
                }

                int result = sqlite3_exec(db, Utf8(sql), IntPtr.Zero, IntPtr.Zero, out IntPtr error);
                if (result != 0)
                {
                    string message = error == IntPtr.Zero ? "unknown" : Marshal.PtrToStringUTF8(error) ?? "unknown";
                    throw new InvalidOperationException($"fixture: SQLite refused the script: {message}");
                }
            }
            finally
            {
                _ = sqlite3_close_v2(db);
            }
        }

        private static byte[] Utf8(string value) => [.. Encoding.UTF8.GetBytes(value), 0];

        [DllImport(Library, ExactSpelling = true)]
        private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

        [DllImport(Library, ExactSpelling = true)]
        private static extern int sqlite3_close_v2(IntPtr db);

        [DllImport(Library, ExactSpelling = true)]
        private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr argument, out IntPtr error);
    }
}
