using System.Runtime.Versioning;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>
/// Opens a GeoPackage and hands back one of its feature tables as <see cref="GeoPackageTable"/>: the rows
/// and the CRS the file registers for them. Read-only.
/// </summary>
/// <remarks>
/// Everything that decides what a row means — the geometry decoder, the frame, the attributes — is
/// <see cref="SiteGeoPackageReader"/> in the pure core. This is only the SQLite half, so that half can be
/// tested on every runner and this one on Windows, where it runs.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class GeoPackageFile
{
    /// <summary>
    /// Reads <paramref name="tableName"/> of the GeoPackage at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The extracted file.</param>
    /// <param name="tableName">The table, matched without regard to case as SQLite matches names.</param>
    /// <param name="label">What to call the layer in a failure message, in the user's words.</param>
    /// <param name="table">The table; <c>null</c> on failure.</param>
    /// <param name="soleTable">
    /// True where the file holds this layer alone (<see cref="BundleArtifact.SoleTable"/>): a file that has
    /// no table of that name but exactly one table is read as that one, because the producer names it in
    /// CamelCase (<c>RoadSplines</c>) and not as the layer (<c>road_splines</c>). A file of several tables
    /// is never read as its only one.
    /// </param>
    /// <returns><c>null</c> on success, or a user-facing reason the table could not be read.</returns>
    public static string? TryReadTable(string path, string tableName, string label, out GeoPackageTable? table, bool soleTable = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(tableName);
        table = null;

        string? geometryColumn = null;
        string organisation = string.Empty;
        long code = 0;
        string name = tableName;

        try
        {
            WinSqlite.Query(
                path,
                "SELECT g.table_name, g.column_name, s.organization, s.organization_coordsys_id "
                + "FROM gpkg_geometry_columns g JOIN gpkg_spatial_ref_sys s ON s.srs_id = g.srs_id "
                + $"WHERE lower(g.table_name) = lower({Literal(tableName)})",
                row =>
                {
                    name = row.String(0);
                    geometryColumn = row.String(1);
                    organisation = row.String(2);
                    code = row.Int64(3);
                });

            if (geometryColumn is null && soleTable)
            {
                // The file has no table of that name: it is the layer's only table if it holds exactly one.
                int tables = 0;
                string only = string.Empty;
                string onlyColumn = string.Empty;
                string onlyOrganisation = string.Empty;
                long onlyCode = 0;
                WinSqlite.Query(
                    path,
                    "SELECT g.table_name, g.column_name, s.organization, s.organization_coordsys_id "
                    + "FROM gpkg_geometry_columns g JOIN gpkg_spatial_ref_sys s ON s.srs_id = g.srs_id",
                    row =>
                    {
                        tables++;
                        only = row.String(0);
                        onlyColumn = row.String(1);
                        onlyOrganisation = row.String(2);
                        onlyCode = row.Int64(3);
                    });

                if (tables == 1)
                {
                    name = only;
                    geometryColumn = onlyColumn;
                    organisation = onlyOrganisation;
                    code = onlyCode;
                }
            }

            if (geometryColumn is null)
            {
                return $"The {label} file has no \"{tableName}\" table, so it is not the layer the manifest names. "
                    + "Re-download this bundle from your vault at mantle.place/vault.";
            }

            string geometry = geometryColumn;
            List<GeoPackageRow> rows = [];
            WinSqlite.Query(path, $"SELECT * FROM {Identifier(name)}", row => rows.Add(ReadRow(row, geometry)));
            table = new GeoPackageTable(name, organisation, code, rows);
            return null;
        }
        catch (Exception ex) when (ex is IOException or DllNotFoundException or EntryPointNotFoundException)
        {
            return ex is IOException
                ? $"The {label} file could not be read as a GeoPackage ({ex.Message}). "
                    + "Re-download this bundle from your vault at mantle.place/vault."
                : $"The {label} are in a GeoPackage, and this Windows has no SQLite to read it with (winsqlite3.dll, "
                    + "part of Windows 10 version 1607 and later).";
        }
    }

    private static GeoPackageRow ReadRow(WinSqlite.RowReader row, string geometryColumn)
    {
        Dictionary<string, string> attributes = new(StringComparer.Ordinal);
        byte[]? geometry = null;

        for (int column = 0; column < row.ColumnCount; column++)
        {
            string name = row.ColumnName(column);
            int type = row.Type(column);

            if (name.Equals(geometryColumn, StringComparison.OrdinalIgnoreCase))
            {
                geometry = type == WinSqlite.Blob ? row.Bytes(column) : null;
                continue;
            }

            switch (type)
            {
                case WinSqlite.Null:
                case WinSqlite.Blob:
                    break;
                case WinSqlite.Float:
                    attributes[name] = GeoPackageText.Real(row.Double(column));
                    break;
                default:
                    attributes[name] = row.String(column);
                    break;
            }
        }

        return new GeoPackageRow(geometry, attributes);
    }

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Identifier(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
