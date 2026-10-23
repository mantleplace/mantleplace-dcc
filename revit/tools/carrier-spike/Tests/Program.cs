// Headless tests of the two prototype carrier readers: each must hand the importer exactly the
// SiteFeature list today's GeoJSON reader hands it for the same layer, attributes included.
// Run from this folder: dotnet run -f net8.0 (then net10.0).
using System.Globalization;
using System.Text;
using MantlePlace.Revit.CarrierSpike;
using MantlePlace.Revit.Core;

// "--compare <extracted bundle> <carrier folder>": the headless comparison table over a real bundle,
// the same table the in-Revit probe writes (README.md).
if (args.Length == 3 && args[0] == "--compare")
{
    Console.Write(new CarrierComparison(args[1], args[2]).Table());
    return 0;
}

int failed = 0;
int passed = 0;

void Test(string name, Action body)
{
    try
    {
        body();
        passed++;
        Console.WriteLine($"  pass  {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"  FAIL  {name}: {ex.Message}");
    }
}

static void Equal<T>(T expected, T actual, string what)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{what}: expected <{expected}>, got <{actual}>");
    }
}

// A State Plane foot order, the frame the carrier must survive: the origin is in ftUS and so are the files.
SiteFrame frame = new()
{
    Origin = new GeoOrigin { Epsg = 6605, Easting = 1000.0, Northing = 2000.0, LinearUnit = LinearUnit.UsSurveyFoot },
};
LayerFrame layer = new(LayerCoordinates.AbsoluteProjected, LinearUnit.UsSurveyFoot);

// The same three features, written three ways: a 3-D road, and a land-use polygon with a hole.
const string GeoJson = """
{"type":"FeatureCollection","features":[
 {"type":"Feature","properties":{"name":"Main St","class":"primary","width_m_estimated":7.5,"subtype":"road","lowest_level":"MED"},
  "geometry":{"type":"LineString","coordinates":[[1010,2010,600.5],[1020,2030,601.25],[1040,2035,602]]}},
 {"type":"Feature","properties":{"class":"park","subtype":"recreation","threshold_deg":30,"fld_zone":"AE","zone_subty":"FLOODWAY"},
  "geometry":{"type":"Polygon","coordinates":[
    [[1100,2100],[1200,2100],[1200,2200],[1100,2200],[1100,2100]],
    [[1120,2120],[1140,2120],[1140,2140],[1120,2120]]]}}
]}
""";

IReadOnlyList<SiteFeature> Baseline(SiteGeometryKinds accept)
{
    string? why = SiteVectorReader.TryParse(GeoJson, frame, layer, accept, "test", out IReadOnlyList<SiteFeature> features, "lowest_level");
    Equal(null, why, "baseline read");
    return features;
}

static void SameFeatures(IReadOnlyList<SiteFeature> expected, IReadOnlyList<SiteFeature> actual)
{
    Equal(expected.Count, actual.Count, "feature count");
    for (int i = 0; i < expected.Count; i++)
    {
        SiteFeature e = expected[i];
        SiteFeature a = actual[i];
        string at = $"feature {i}";
        Equal(e.IsClosed, a.IsClosed, at + " IsClosed");
        Equal(e.IsHole, a.IsHole, at + " IsHole");
        Equal(e.PolygonOrdinal, a.PolygonOrdinal, at + " PolygonOrdinal");
        Equal(e.Name, a.Name, at + " name");
        Equal(e.Classification, a.Classification, at + " class");
        Equal(e.WidthM, a.WidthM, at + " width_m_estimated");
        Equal(e.Subtype, a.Subtype, at + " subtype");
        Equal(e.FloodZone, a.FloodZone, at + " fld_zone");
        Equal(e.FloodZoneSubtype, a.FloodZoneSubtype, at + " zone_subty");
        Equal(e.Threshold, a.Threshold, at + " threshold_deg");
        Equal(e.LowestLevel, a.LowestLevel, at + " lowest_level");
        Equal(e.Vertices.Count, a.Vertices.Count, at + " vertex count");
        for (int v = 0; v < e.Vertices.Count; v++)
        {
            if (Math.Abs(e.Vertices[v].EastM - a.Vertices[v].EastM) > 1e-9
                || Math.Abs(e.Vertices[v].NorthM - a.Vertices[v].NorthM) > 1e-9
                || e.Vertices[v].ElevationM.HasValue != a.Vertices[v].ElevationM.HasValue
                || Math.Abs((e.Vertices[v].ElevationM ?? 0) - (a.Vertices[v].ElevationM ?? 0)) > 1e-9)
            {
                throw new InvalidOperationException($"{at} vertex {v}: expected {e.Vertices[v]}, got {a.Vertices[v]}");
            }
        }
    }
}

// ---- GeoPackage ------------------------------------------------------------------------------

static byte[] Wkb(Action<BinaryWriter> body)
{
    using MemoryStream stream = new();
    using BinaryWriter writer = new(stream);
    body(writer);
    writer.Flush();
    return stream.ToArray();
}

static byte[] GpkgBlob(int srsId, byte[] wkb, int envelope = 1)
{
    using MemoryStream stream = new();
    using BinaryWriter writer = new(stream);
    writer.Write((byte)'G');
    writer.Write((byte)'P');
    writer.Write((byte)0);
    writer.Write((byte)(1 | (envelope << 1)));
    writer.Write(srsId);
    int doubles = envelope switch { 0 => 0, 1 => 4, 4 => 8, _ => 6 };
    for (int i = 0; i < doubles; i++)
    {
        writer.Write(0.0);
    }

    writer.Write(wkb);
    writer.Flush();
    return stream.ToArray();
}

static void Ring(BinaryWriter w, params double[] xy)
{
    w.Write(xy.Length / 2);
    for (int i = 0; i < xy.Length; i++)
    {
        w.Write(xy[i]);
    }
}

byte[] road = Wkb(w =>
{
    w.Write((byte)1);
    w.Write(1002u); // LineString Z, ISO
    w.Write(3);
    foreach (double d in new[] { 1010, 2010, 600.5, 1020, 2030, 601.25, 1040, 2035, 602.0 })
    {
        w.Write(d);
    }
});
byte[] park = Wkb(w =>
{
    w.Write((byte)1);
    w.Write(3u); // Polygon
    w.Write(2);
    Ring(w, 1100, 2100, 1200, 2100, 1200, 2200, 1100, 2200, 1100, 2100);
    Ring(w, 1120, 2120, 1140, 2120, 1140, 2140, 1120, 2120);
});

Test("a GeoPackage blob with an envelope decodes to its WKB parts", () =>
{
    IReadOnlyList<CarrierPart> parts = GeoPackageGeometry.Decode(GpkgBlob(6605, park, envelope: 1));
    Equal(2, parts.Count, "rings");
    Equal(true, parts[1].IsHole, "second ring is a hole");
    Equal(5, parts[0].Positions.Count, "outer ring positions, closing one kept for the shared builder");
    Equal(1, parts[0].Polygon, "polygon ordinal");
});

Test("a 3-D line keeps its Z through the blob", () =>
{
    IReadOnlyList<CarrierPart> parts = GeoPackageGeometry.Decode(GpkgBlob(6605, road, envelope: 2));
    Equal(1, parts.Count, "lines");
    Equal(601.25, parts[0].Positions[1].Z, "second Z");
});

Test("a GeoPackage table reads as the GeoJSON layer does", () =>
{
    string path = Path.Combine(Path.GetTempPath(), $"carrier-{Guid.NewGuid():N}.gpkg");
    try
    {
        WriteGeoPackage(path, "land_use", [
            (road, "Main St", "primary", (double?)7.5, "road", null, null, null, "MED"),
            (park, null, "park", null, "recreation", "AE", "FLOODWAY", (double?)30.0, null),
        ]);
        string? why = GeoPackageLayerReader.TryRead(path, "land_use", frame, layer, SiteGeometryKinds.Lines | SiteGeometryKinds.Areas, "test", "lowest_level", out IReadOnlyList<SiteFeature> features);
        Equal(null, why, "read");
        SameFeatures(Baseline(SiteGeometryKinds.Lines | SiteGeometryKinds.Areas), features);
    }
    finally
    {
        File.Delete(path);
    }
});

Test("a GeoPackage table in another CRS is refused, never reprojected", () =>
{
    string path = Path.Combine(Path.GetTempPath(), $"carrier-{Guid.NewGuid():N}.gpkg");
    try
    {
        WriteGeoPackage(path, "water", [(road, null, null, null, null, null, null, null, null)], epsg: 4326);
        string? why = GeoPackageLayerReader.TryRead(path, "water", frame, layer, SiteGeometryKinds.Lines, "water bodies", null, out IReadOnlyList<SiteFeature> features);
        Equal(true, why is not null && why.Contains("EPSG:4326"), "refusal names the CRS");
        Equal(0, features.Count, "features");
    }
    finally
    {
        File.Delete(path);
    }
});

Test("a missing GeoPackage table is a stated reason, not zero features", () =>
{
    string path = Path.Combine(Path.GetTempPath(), $"carrier-{Guid.NewGuid():N}.gpkg");
    try
    {
        WriteGeoPackage(path, "water", [(road, null, null, null, null, null, null, null, null)]);
        string? why = GeoPackageLayerReader.TryRead(path, "flood_zones", frame, layer, SiteGeometryKinds.Areas, "flood zones", null, out _);
        Equal(true, why is not null && why.Contains("flood_zones"), "reason names the table");
    }
    finally
    {
        File.Delete(path);
    }
});

// ---- DXF -------------------------------------------------------------------------------------

string Dxf = DxfText();

Test("a DXF layer with MANTLEPLACE xdata reads as the GeoJSON layer does", () =>
{
    string? why = DxfLayerReader.TryParse(Dxf, "land_use", frame, layer, SiteGeometryKinds.Lines | SiteGeometryKinds.Areas, "test", "lowest_level", out IReadOnlyList<SiteFeature> features);
    Equal(null, why, "read");
    SameFeatures(Baseline(SiteGeometryKinds.Lines | SiteGeometryKinds.Areas), features);
});

Test("DXF layer names match case-insensitively, as AutoCAD's do", () =>
{
    DxfLayerReader.TryParse(Dxf, "LAND_USE", frame, layer, SiteGeometryKinds.Areas, "test", null, out IReadOnlyList<SiteFeature> features);
    Equal(2, features.Count, "rings");
});

Test("a DXF with no ENTITIES section is refused", () =>
{
    string? why = DxfLayerReader.TryParse("0\nSECTION\n2\nHEADER\n0\nENDSEC\n0\nEOF\n", "water", frame, layer, SiteGeometryKinds.Areas, "water bodies", null, out _);
    Equal(true, why is not null, "refused");
});

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

static string DxfText()
{
    StringBuilder b = new();
    void G(int code, string value) => b.Append(code.ToString(CultureInfo.InvariantCulture)).Append('\n').Append(value).Append('\n');
    void Xdata(params (string Key, string Value)[] pairs)
    {
        G(1001, "MANTLEPLACE");
        foreach ((string key, string value) in pairs)
        {
            G(1000, key + "=" + value);
        }
    }

    G(0, "SECTION");
    G(2, "ENTITIES");
    // The road: a 3-D POLYLINE (flag 8), its vertices, SEQEND.
    G(0, "POLYLINE");
    G(8, "land_use");
    G(66, "1");
    G(70, "8");
    Xdata(("name", "Main St"), ("class", "primary"), ("width_m_estimated", "7.5"), ("subtype", "road"), ("lowest_level", "MED"));
    foreach ((double x, double y, double z) in new[] { (1010.0, 2010.0, 600.5), (1020.0, 2030.0, 601.25), (1040.0, 2035.0, 602.0) })
    {
        G(0, "VERTEX");
        G(8, "land_use");
        G(10, x.ToString("R", CultureInfo.InvariantCulture));
        G(20, y.ToString("R", CultureInfo.InvariantCulture));
        G(30, z.ToString("R", CultureInfo.InvariantCulture));
        G(70, "32");
    }

    G(0, "SEQEND");
    G(8, "land_use");
    // The park: two closed LWPOLYLINEs, the hole naming its polygon.
    foreach ((string role, double[] xy) in new[]
    {
        ("outer", new double[] { 1100, 2100, 1200, 2100, 1200, 2200, 1100, 2200 }),
        ("hole", new double[] { 1120, 2120, 1140, 2120, 1140, 2140 }),
    })
    {
        G(0, "LWPOLYLINE");
        G(8, "LAND_USE");
        G(90, (xy.Length / 2).ToString(CultureInfo.InvariantCulture));
        G(70, "1");
        for (int i = 0; i < xy.Length; i += 2)
        {
            G(10, xy[i].ToString("R", CultureInfo.InvariantCulture));
            G(20, xy[i + 1].ToString("R", CultureInfo.InvariantCulture));
        }

        Xdata(("class", "park"), ("subtype", "recreation"), ("fld_zone", "AE"), ("zone_subty", "FLOODWAY"), ("threshold_deg", "30"), ("polygon", "1"), ("ring", role));
    }

    G(0, "ENDSEC");
    G(0, "EOF");
    return b.ToString();
}

static void WriteGeoPackage(
    string path,
    string table,
    (byte[] Wkb, string? Name, string? Class, double? Width, string? Subtype, string? Zone, string? ZoneSub, double? Threshold, string? Level)[] rows,
    int epsg = 6605)
{
    static string Text(string? value) => value is null ? "NULL" : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    static string Real(double? value) => value is null ? "NULL" : value.Value.ToString("R", CultureInfo.InvariantCulture);

    StringBuilder sql = new();
    sql.Append("CREATE TABLE gpkg_spatial_ref_sys (srs_name TEXT, srs_id INTEGER PRIMARY KEY, organization TEXT, organization_coordsys_id INTEGER, definition TEXT, description TEXT);");
    sql.Append($"INSERT INTO gpkg_spatial_ref_sys VALUES ('order', 100, 'EPSG', {epsg}, 'undefined', NULL);");
    sql.Append("CREATE TABLE gpkg_contents (table_name TEXT PRIMARY KEY, data_type TEXT, identifier TEXT, srs_id INTEGER);");
    sql.Append($"INSERT INTO gpkg_contents VALUES ('{table}', 'features', '{table}', 100);");
    sql.Append("CREATE TABLE gpkg_geometry_columns (table_name TEXT, column_name TEXT, geometry_type_name TEXT, srs_id INTEGER, z TINYINT, m TINYINT);");
    sql.Append($"INSERT INTO gpkg_geometry_columns VALUES ('{table}', 'geom', 'GEOMETRY', 100, 2, 0);");
    sql.Append($"CREATE TABLE \"{table}\" (fid INTEGER PRIMARY KEY, geom BLOB, name TEXT, class TEXT, width_m_estimated REAL, subtype TEXT, fld_zone TEXT, zone_subty TEXT, threshold_deg REAL, lowest_level TEXT);");
    foreach (var row in rows)
    {
        sql.Append($"INSERT INTO \"{table}\" (geom, name, class, width_m_estimated, subtype, fld_zone, zone_subty, threshold_deg, lowest_level) VALUES ")
            .Append($"(X'{Convert.ToHexString(GpkgBlob(100, row.Wkb))}', {Text(row.Name)}, {Text(row.Class)}, {Real(row.Width)}, {Text(row.Subtype)}, ")
            .Append($"{Text(row.Zone)}, {Text(row.ZoneSub)}, {Real(row.Threshold)}, {Text(row.Level)});");
    }

    WinSqlite.ExecuteScript(path, sql.ToString());
}
