using System.Globalization;

namespace MantlePlace.Core;

public sealed record GeoPackageRow(byte[]? Geometry, IReadOnlyDictionary<string, string> Attributes);

public sealed record GeoPackageTable(string Name, string Organization, long CrsCode, IReadOnlyList<GeoPackageRow> Rows);

public static class GeoPackageText
{
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

public static class SiteGeoPackageReader
{
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

        List<SiteVertex> vertices = new(part.Positions.Count);
        foreach (GeoPackagePosition position in part.Positions)
        {
            if (SiteVectorReader.TryPlaceVertex(frame, layer, position.X, position.Y, position.Z, out SiteVertex vertex))
            {
                vertices.Add(vertex);
            }
        }

        if (!SiteVectorReader.TryFinishPath(vertices, part.Closed))
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
