using System.Globalization;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.CarrierSpike;

/// <summary>
/// Prototype: reads one table of a GeoPackage carrier into the features today's GeoJSON reader
/// yields, through <see cref="WinSqlite"/> and <see cref="GeoPackageGeometry"/>.
/// </summary>
/// <remarks>
/// The table's CRS is read from <c>gpkg_geometry_columns</c> → <c>gpkg_spatial_ref_sys</c> and must be
/// the frame's own EPSG code: a table in any other CRS is refused with the reason, never reprojected
/// (<c>HPS-53</c>). Every attribute is read as text exactly as SQLite holds it, except a REAL, which is
/// written the way <see cref="double.ToString(string, IFormatProvider)"/> round-trips it.
/// </remarks>
public static class GeoPackageLayerReader
{
    /// <returns><c>null</c> on success, or the reason the table could not be read.</returns>
    public static string? TryRead(
        string path,
        string table,
        SiteFrame frame,
        LayerFrame layer,
        SiteGeometryKinds accept,
        string label,
        string? levelField,
        out IReadOnlyList<SiteFeature> features)
    {
        ArgumentNullException.ThrowIfNull(frame);
        List<SiteFeature> parsed = [];
        features = parsed;

        string? geometryColumn = null;
        long organisationCode = 0;
        string organisation = string.Empty;
        try
        {
            WinSqlite.Query(
                path,
                "SELECT g.column_name, s.organization, s.organization_coordsys_id FROM gpkg_geometry_columns g "
                + $"JOIN gpkg_spatial_ref_sys s ON s.srs_id = g.srs_id WHERE lower(g.table_name) = lower('{Quote(table)}')",
                row =>
                {
                    geometryColumn = row.String(0);
                    organisation = row.String(1);
                    organisationCode = row.Int64(2);
                });
        }
        catch (IOException ex)
        {
            return $"The {label} carrier could not be opened as a GeoPackage: {ex.Message}";
        }

        if (geometryColumn is null)
        {
            return $"The {label} carrier has no \"{table}\" table, so it is not the layer the manifest names.";
        }

        if (!organisation.Equals("EPSG", StringComparison.OrdinalIgnoreCase) || organisationCode != frame.Epsg)
        {
            return $"The {label} table is in {organisation}:{organisationCode.ToString(CultureInfo.InvariantCulture)}, "
                + $"not this order's EPSG:{frame.Epsg.ToString(CultureInfo.InvariantCulture)}, so it was not placed.";
        }

        int polygons = 0;
        WinSqlite.Query(path, $"SELECT * FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"", row =>
        {
            Dictionary<string, string> attributes = new(StringComparer.Ordinal);
            byte[]? blob = null;
            for (int column = 0; column < row.ColumnCount; column++)
            {
                string name = row.ColumnName(column);
                if (name.Equals(geometryColumn, StringComparison.OrdinalIgnoreCase))
                {
                    blob = row.Bytes(column);
                    continue;
                }

                switch (row.Type(column))
                {
                    case WinSqlite.Null:
                    case WinSqlite.Blob:
                        break;
                    case WinSqlite.Float:
                        attributes[name] = row.Double(column).ToString("R", CultureInfo.InvariantCulture);
                        break;
                    default:
                        attributes[name] = row.String(column);
                        break;
                }
            }

            if (blob is null)
            {
                return;
            }

            foreach (CarrierPart part in GeoPackageGeometry.Decode(blob, ref polygons))
            {
                CarrierFeatureBuilder.Append(parsed, part, attributes, frame, layer, accept, levelField);
            }
        });

        return null;
    }

    private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
