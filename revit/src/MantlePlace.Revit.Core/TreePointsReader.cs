using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// One tree point, placed in the bundle's local frame and dimensioned well enough to be real
/// geometry.
/// </summary>
/// <remarks>
/// A <em>tree point</em> is any published point of the tree layer, whatever its foliage type — the
/// layer is named for what it publishes, not for what grows there.
/// </remarks>
/// <param name="GroundElevationM">Absolute orthometric height of the ground beneath it.</param>
/// <param name="HeightM">Total height, ground to apex.</param>
/// <param name="CrownRadiusM">Crown radius at its widest.</param>
/// <param name="FoliageType">As the platform classified it; never derived from the dimensions.</param>
public readonly record struct SiteTreePoint(
    double EastM,
    double NorthM,
    double GroundElevationM,
    double HeightM,
    double CrownRadiusM,
    FoliageType FoliageType);

/// <summary>
/// One read of <c>Landcover/TreePoints.csv</c>: the points, and what the log has to say about how
/// they were read.
/// </summary>
/// <remarks>
/// A record rather than a handful of <c>out</c> parameters. The counts are not incidental — a
/// bundle whose foliage column this build could not interpret is a bundle whose planting is all
/// trees for a reason the curator deserves to be told.
/// </remarks>
public sealed record TreePointsParse
{
    /// <summary>A user-facing reason the file could not be read, or <c>null</c> on success.</summary>
    public string? Failure { get; init; }

    /// <summary>The points that parsed. Empty when <see cref="Failure"/> is set.</summary>
    public IReadOnlyList<SiteTreePoint> Points { get; init; } = [];

    /// <summary>Cells carrying a value this build does not know, each read as a tree.</summary>
    public int UnknownFoliageValues { get; init; }

    /// <summary>Cells that were empty or absent, each read as a tree.</summary>
    public int EmptyFoliageCells { get; init; }

    /// <summary>
    /// What the log says once about the file as a whole — never once per row.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    internal static TreePointsParse Failed(string reason) => new() { Failure = reason };
}

/// <summary>
/// Parses <c>Landcover/TreePoints.csv</c> into placed tree points. Pure.
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
/// <para>
/// Five columns are required and make geometry. <c>foliage_type</c> is the sixth, added by MPB
/// 1.2.0, and it is optional in both directions: a file without it parses, and a row that stops
/// before it parses. The manifest's <c>foliage_type_vocabulary</c> is the authority on what its
/// values mean — without one they are uninterpretable, so the column is ignored and the log says so
/// (<c>spec/format.md</c> §4.4, <see cref="FoliageTypes"/>).
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
    /// <param name="foliageVocabulary">
    /// <c>landcover.tree_points.foliage_type_vocabulary</c> as published, or <c>null</c> where the
    /// manifest carried none.
    /// </param>
    public static TreePointsParse Parse(string csvText, SiteFrame frame, string? foliageVocabulary)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (string.IsNullOrWhiteSpace(csvText))
        {
            return TreePointsParse.Failed("The tree-points file is empty.");
        }

        List<SiteTreePoint> parsed = [];
        List<string> notes = [];
        string[] lines = csvText.Split('\n');
        Dictionary<string, int>? columns = null;
        int foliageColumn = -1;
        bool readFoliage = false;
        int unknownValues = 0;
        int emptyCells = 0;

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
                    return TreePointsParse.Failed(
                        "The tree-points file has no recognisable header row (expected "
                        + string.Join(", ", RequiredColumns) + "). Re-download this bundle from your "
                        + "vault at mantle.place/vault.");
                }

                foliageColumn = columns.TryGetValue(FoliageTypes.Column, out int index) ? index : -1;
                readFoliage = foliageColumn >= 0 && foliageVocabulary is not null;
                notes.AddRange(VocabularyNotes(foliageVocabulary, foliageColumn >= 0));
                continue;
            }

            if (!TryReadRow(fields, columns, frame, out SiteTreePoint point))
            {
                continue;
            }

            if (readFoliage)
            {
                string? cell = foliageColumn < fields.Length ? fields[foliageColumn] : null;
                point = point with { FoliageType = FoliageTypes.Read(cell, out FoliageReading reading) };
                unknownValues += reading == FoliageReading.Unknown ? 1 : 0;
                emptyCells += reading == FoliageReading.Empty ? 1 : 0;
            }

            parsed.Add(point);
        }

        return columns is null
            ? TreePointsParse.Failed("The tree-points file is empty.")
            : new TreePointsParse
            {
                Points = parsed,
                UnknownFoliageValues = unknownValues,
                EmptyFoliageCells = emptyCells,
                Notes = notes,
            };
    }

    /// <summary>
    /// What the log says once about the manifest and the header disagreeing, or about a vocabulary
    /// this build has not caught up with.
    /// </summary>
    /// <remarks>
    /// Silence is right for the ordinary older bundle — no vocabulary and no column is the 1.1.0
    /// shape, and every point being a tree is exactly what the spec says it is. The two
    /// disagreements are worth one line each: neither is the curator's doing, and a silent drop
    /// would hide a publisher's mistake behind a plausible-looking import.
    /// </remarks>
    private static IEnumerable<string> VocabularyNotes(string? vocabulary, bool hasColumn)
    {
        if (vocabulary is null)
        {
            if (hasColumn)
            {
                yield return "The tree points carry a " + FoliageTypes.Column + " column, but this "
                    + "bundle's manifest does not name the vocabulary its values come from, so every "
                    + "point was read as a tree.";
            }

            yield break;
        }

        if (!hasColumn)
        {
            yield return "This bundle's manifest names a " + FoliageTypes.Column + " vocabulary, but "
                + "the tree-points file has no such column, so every point was read as a tree.";
            yield break;
        }

        if (!FoliageTypes.IsKnownVocabulary(vocabulary))
        {
            yield return "This bundle's " + FoliageTypes.Column + " vocabulary (\"" + vocabulary
                + "\") is newer than this add-in: the values it knows were mapped, and the rest were "
                + "read as trees.";
        }
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
        out SiteTreePoint point)
    {
        point = default;

        if (!TryNumber(fields, columns, EastingColumn, out double easting)
            || !TryNumber(fields, columns, NorthingColumn, out double northing)
            || !TryNumber(fields, columns, GroundColumn, out double ground)
            || !TryNumber(fields, columns, HeightColumn, out double height)
            || !TryNumber(fields, columns, CrownColumn, out double crown)
            || !frame.TryToLocalMetres(easting, northing, out double east, out double north))
        {
            return false;
        }

        point = new SiteTreePoint(east, north, ground, height, crown, FoliageType.Tree);
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
