using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// One tree, placed in the bundle's local frame and dimensioned well enough to be real geometry.
/// </summary>
/// <param name="GroundElevationM">Absolute orthometric height of the ground beneath it.</param>
/// <param name="HeightM">Total height, ground to apex.</param>
/// <param name="CrownRadiusM">Crown radius at its widest.</param>
public readonly record struct SiteTree(
    double EastM,
    double NorthM,
    double GroundElevationM,
    double HeightM,
    double CrownRadiusM);

/// <summary>
/// Parses <c>Landcover/TreePoints.csv</c> into placed trees. Pure.
/// </summary>
/// <remarks>
/// <para>
/// The file is absolute AOI-UTM — the DEM's own CRS, whatever the delivery tier — so unlike the
/// toposurface points it is not already local, and unlike the vector layers it is not geographic.
/// <see cref="SiteFrame"/> owns both the subtraction and the refusal when the bundle's origin is in
/// a different CRS entirely.
/// </para>
/// <para>
/// Columns are resolved from the HEADER, not by position. The manifest publishes
/// <c>landcover.tree_points.columns</c>, which makes the order contract rather than convention; a
/// positional reader would swap height for crown radius the day the ETL reorders them and every
/// tree would still look like a tree.
/// </para>
/// </remarks>
public static class TreePointsReader
{
    private const string EastingColumn = "x";
    private const string NorthingColumn = "y";
    private const string GroundColumn = "ground_z";
    private const string HeightColumn = "height_m";
    private const string CrownColumn = "crown_radius_m";

    private static readonly string[] RequiredColumns =
        [EastingColumn, NorthingColumn, GroundColumn, HeightColumn, CrownColumn];

    /// <summary>
    /// Parses the whole file.
    /// </summary>
    /// <remarks>
    /// A row that does not parse is DROPPED, where a bad row in the toposurface points file fails
    /// the read. The difference is what a bad row costs: a hole in the terrain is invisible and
    /// wrong, one missing tree out of forty-four is neither. The ETL leaves <c>ground_z</c> empty
    /// where the DEM had no data, and that row is dropped rather than placed at elevation zero —
    /// unknown is not zero (<c>HPS-20</c>), and zero here is two kilometres below the site.
    /// </remarks>
    /// <returns><c>null</c> on success, or a user-facing reason the file could not be read.</returns>
    public static string? TryParse(string csvText, SiteFrame frame, out IReadOnlyList<SiteTree> trees)
    {
        ArgumentNullException.ThrowIfNull(frame);

        List<SiteTree> parsed = [];
        trees = parsed;

        if (string.IsNullOrWhiteSpace(csvText))
        {
            return "The tree-points file is empty.";
        }

        string[] lines = csvText.Split('\n');
        Dictionary<string, int>? columns = null;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] fields = line.Split(',');

            if (columns is null)
            {
                columns = ReadHeader(fields);
                if (columns is null)
                {
                    return "The tree-points file has no recognisable header row (expected "
                        + string.Join(", ", RequiredColumns) + "). Re-download this bundle from your "
                        + "vault at mantle.place/vault.";
                }

                continue;
            }

            if (TryReadRow(fields, columns, frame, out SiteTree tree))
            {
                parsed.Add(tree);
            }
        }

        return columns is null ? "The tree-points file is empty." : null;
    }

    /// <summary>The column index of each required name, or <c>null</c> when one is missing.</summary>
    private static Dictionary<string, int>? ReadHeader(string[] fields)
    {
        Dictionary<string, int> columns = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < fields.Length; index++)
        {
            columns[fields[index].Trim()] = index;
        }

        foreach (string required in RequiredColumns)
        {
            if (!columns.ContainsKey(required))
            {
                return null;
            }
        }

        return columns;
    }

    private static bool TryReadRow(
        string[] fields,
        Dictionary<string, int> columns,
        SiteFrame frame,
        out SiteTree tree)
    {
        tree = default;

        if (!TryNumber(fields, columns, EastingColumn, out double easting)
            || !TryNumber(fields, columns, NorthingColumn, out double northing)
            || !TryNumber(fields, columns, GroundColumn, out double ground)
            || !TryNumber(fields, columns, HeightColumn, out double height)
            || !TryNumber(fields, columns, CrownColumn, out double crown)
            || !frame.TryToLocalMetres(easting, northing, out double east, out double north))
        {
            return false;
        }

        tree = new SiteTree(east, north, ground, height, crown);
        return true;
    }

    private static bool TryNumber(string[] fields, Dictionary<string, int> columns, string column, out double value)
    {
        value = 0.0;
        int index = columns[column];
        return index < fields.Length
            && double.TryParse(
                fields[index].Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
    }
}
