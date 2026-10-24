using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>One row of a GeoPackage feature table, as the file holds it.</summary>
/// <param name="Geometry">The geometry column's blob, or <c>null</c> for a row with none.</param>
/// <param name="Attributes">
/// Every other column of the row that is not <c>NULL</c> and not a blob, by name, as text: a <c>REAL</c> is
/// written the way <see cref="GeoPackageText.Real"/> writes it. A <c>NULL</c> is absent, not empty.
/// </param>
public sealed record GeoPackageRow(byte[]? Geometry, IReadOnlyDictionary<string, string> Attributes);

/// <summary>One feature table of a GeoPackage, with the CRS its file registers for it.</summary>
/// <param name="Name">The table's name.</param>
/// <param name="Organization">
/// <c>gpkg_spatial_ref_sys.organization</c> of the table's CRS, such as <c>EPSG</c>; <c>NONE</c> is the
/// file's own word for a CRS it does not name.
/// </param>
/// <param name="CrsCode"><c>organization_coordsys_id</c> of that CRS.</param>
/// <param name="Rows">The table's rows, in file order.</param>
public sealed record GeoPackageTable(string Name, string Organization, long CrsCode, IReadOnlyList<GeoPackageRow> Rows);

/// <summary>How a GeoPackage column is turned into the text the attribute readers take.</summary>
public static class GeoPackageText
{
    /// <summary>
    /// A <c>REAL</c> column's value as text: the shortest form that reads back as the same number, and
    /// always with a decimal point (<c>35</c> is <c>35.0</c>), the way the manifest and the GeoJSON files
    /// the same number came from wrote it. A label that quotes it — steep ground's threshold — then
    /// reads the same off the carrier as off the files it replaces.
    /// </summary>
    public static string Real(double value)
    {
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal)
            || text.Contains('E', StringComparison.Ordinal)
            || !double.IsFinite(value)
                ? text
                : text + ".0";
    }
}

/// <summary>
/// Reads one table of the shared vector carrier into the features the importer takes. Pure.
/// </summary>
/// <remarks>
/// <para>
/// The carrier is a GeoPackage in the order's own CRS, absolute (<c>revit/docs/vector-carrier.md</c>).
/// Its rows become exactly the <see cref="SiteFeature"/> list <see cref="SiteVectorReader"/> makes of the
/// GeoJSON file the carrier replaces, with the same rules: a ring's repeated closing position is
/// dropped, a line under two vertices or a ring under three is no feature, and a position the frame
/// cannot place is passed over. Placement is the frame's own <see cref="SiteFrame.TryPlace"/>: subtraction
/// and scaling, nothing derived here.
/// </para>
/// <para>
/// The file states its CRS, so a table in any other one is refused with the reason and never
/// reprojected (<c>HPS-53</c>).
/// </para>
/// <para>
/// The rows are the caller's to fetch: opening the file is I/O, and SQLite is Windows-only here
/// (<c>MantlePlace.Revit.Client</c>). This half runs everywhere, and is what the suite exercises on
/// every runner.
/// </para>
/// </remarks>
public static class SiteGeoPackageReader
{
    /// <param name="table">The table, as the file holds it.</param>
    /// <param name="frame">The frame to place vertices in.</param>
    /// <param name="layer">What the file's coordinates are, and the unit they and any Z are in.</param>
    /// <param name="accept">Which geometries to take.</param>
    /// <param name="label">What to call the layer in a failure message, in the user's words.</param>
    /// <param name="features">The features; empty on failure.</param>
    /// <param name="levelField">
    /// The column a fidelity level's cut names, read into each feature's <see cref="SiteFeature.LowestLevel"/>;
    /// <c>null</c> for a layer read whole.
    /// </param>
    /// <returns><c>null</c> on success, or a user-facing reason the table could not be read.</returns>
    public static string? TryRead(
        GeoPackageTable table,
        SiteFrame frame,
        LayerFrame layer,
        SiteGeometryKinds accept,
        string label,
        out IReadOnlyList<SiteFeature> features,
        string? levelField = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(frame);

        List<SiteFeature> parsed = [];
        features = parsed;

        if (!table.Organization.Equals("EPSG", StringComparison.OrdinalIgnoreCase) || table.CrsCode != frame.Epsg)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "The {0} are stored in {1}:{2}, not this project's EPSG:{3}, so they were left out rather than placed in the wrong frame.",
                label,
                table.Organization,
                table.CrsCode,
                frame.Epsg);
        }

        int polygons = 0;
        foreach (GeoPackageRow row in table.Rows)
        {
            if (row.Geometry is not { } blob)
            {
                continue;
            }

            foreach (GeoPackagePart part in GeoPackageGeometry.Decode(blob, ref polygons))
            {
                Append(parsed, part, row.Attributes, frame, layer, accept, levelField);
            }
        }

        return null;
    }

    private static void Append(
        List<SiteFeature> into,
        GeoPackagePart part,
        IReadOnlyDictionary<string, string> attributes,
        SiteFrame frame,
        LayerFrame layer,
        SiteGeometryKinds accept,
        string? levelField)
    {
        SiteGeometryKinds kind = part.Closed ? SiteGeometryKinds.Areas : SiteGeometryKinds.Lines;
        if ((accept & kind) == 0)
        {
            return;
        }

        double metresPerUnit = LinearUnits.MetresPerUnit(layer.Unit);
        List<SiteVertex> vertices = new(part.Positions.Count);
        foreach (GeoPackagePosition position in part.Positions)
        {
            if (frame.TryPlace(layer, position.X, position.Y, out double east, out double north))
            {
                vertices.Add(new SiteVertex(east, north, position.Z * metresPerUnit));
            }
        }

        if (part.Closed)
        {
            // WKB, like GeoJSON, repeats a ring's first position at its end; a CurveLoop closes
            // itself and rejects the zero-length segment the repetition would make.
            if (vertices.Count > 1
                && Math.Abs(vertices[0].EastM - vertices[^1].EastM) < 1e-6
                && Math.Abs(vertices[0].NorthM - vertices[^1].NorthM) < 1e-6)
            {
                vertices.RemoveAt(vertices.Count - 1);
            }

            if (vertices.Count < SiteVectorReader.MinimumRingVertices)
            {
                return;
            }
        }
        else if (vertices.Count < SiteVectorReader.MinimumLineVertices)
        {
            return;
        }

        into.Add(new SiteFeature
        {
            Vertices = vertices,
            IsClosed = part.Closed,
            IsHole = part.IsHole,
            PolygonOrdinal = part.Polygon,
            Name = Get(attributes, "name"),
            Classification = Get(attributes, "class"),
            WidthM = double.TryParse(Get(attributes, "width_m_estimated"), NumberStyles.Float, CultureInfo.InvariantCulture, out double width)
                ? width
                : null,
            Subtype = Get(attributes, "subtype"),
            FloodZone = Get(attributes, "fld_zone"),
            FloodZoneSubtype = Get(attributes, "zone_subty"),
            Threshold = Get(attributes, "threshold_deg"),
            LowestLevel = levelField is not null && attributes.TryGetValue(levelField, out string? level) ? level : null,
        });
    }

    private static string Get(IReadOnlyDictionary<string, string> attributes, string key)
        => attributes.TryGetValue(key, out string? value) ? value : string.Empty;
}
