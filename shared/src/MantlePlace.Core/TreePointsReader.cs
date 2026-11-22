using System.Globalization;

namespace MantlePlace.Core;

public readonly record struct SiteTreePoint(
    double EastM,
    double NorthM,
    double GroundElevationM,
    double HeightM,
    double CrownRadiusM,
    FoliageType FoliageType);

public sealed record TreePointsParse
{
    public string? Failure { get; init; }

    public IReadOnlyList<SiteTreePoint> Points { get; init; } = [];

    public int UnknownFoliageValues { get; init; }

    public int EmptyFoliageCells { get; init; }

    public int RowsWithoutGround { get; init; }

    public int UnreadableRows { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];

    internal static TreePointsParse Failed(string reason) => new() { Failure = reason };
}

public static class TreePointsReader
{
    private const string EastingColumn = "x";
    private const string NorthingColumn = "y";
    private const string GroundColumn = "ground_z";
    private const string HeightColumn = "height_m";
    private const string CrownColumn = "crown_radius_m";

    private static readonly string[] RequiredColumns =
        [EastingColumn, NorthingColumn, GroundColumn, HeightColumn, CrownColumn];

    public static TreePointsParse Parse(
        string csvText,
        SiteFrame frame,
        string? foliageVocabulary,
        LinearUnit groundUnit,
        int? rowLimit = null,
        LayerFrame? layer = null,
        double groundBaseM = 0.0)
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
        int withoutGround = 0;
        int unreadable = 0;
        int dataRows = 0;

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

            if (rowLimit is { } limit && dataRows >= limit)
            {
                break;
            }

            dataRows++;
            RowReading row = ReadRow(fields, columns, frame, groundUnit, layer, groundBaseM, out SiteTreePoint point);
            switch (row)
            {
                case RowReading.NoGround:
                    withoutGround++;
                    continue;
                case RowReading.Unreadable:
                    unreadable++;
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
                RowsWithoutGround = withoutGround,
                UnreadableRows = unreadable,
                Notes = notes,
            };
    }

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
                + "\") is newer than this plugin: the values it knows were mapped, and the rest were "
                + "read as trees.";
        }
    }

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

    private enum RowReading
    {
        Read,
        NoGround,
        Unreadable,
    }

    private static RowReading ReadRow(
        string[] fields,
        Dictionary<string, int> columns,
        SiteFrame frame,
        LinearUnit groundUnit,
        LayerFrame? layer,
        double groundBaseM,
        out SiteTreePoint point)
    {
        point = default;

        if (!TryNumber(fields, columns, EastingColumn, out double easting)
            || !TryNumber(fields, columns, NorthingColumn, out double northing)
            || !TryNumber(fields, columns, HeightColumn, out double height)
            || !TryNumber(fields, columns, CrownColumn, out double crown)
            || !TryPlace(frame, layer, easting, northing, out double east, out double north))
        {
            return RowReading.Unreadable;
        }

        if (!TryNumber(fields, columns, GroundColumn, out double ground))
        {
            return Cell(fields, columns, GroundColumn) is { } cell && string.IsNullOrWhiteSpace(cell)
                ? RowReading.NoGround
                : RowReading.Unreadable;
        }

        double groundM = (ground * LinearUnits.MetresPerUnit(groundUnit)) + groundBaseM;
        point = new SiteTreePoint(east, north, groundM, height, crown, FoliageType.Tree);
        return RowReading.Read;
    }

    private static bool TryPlace(
        SiteFrame frame,
        LayerFrame? layer,
        double x,
        double y,
        out double eastMetres,
        out double northMetres)
        => layer is { } stated
            ? frame.TryPlace(stated, x, y, out eastMetres, out northMetres)
            : frame.TryToLocalMetres(x, y, out eastMetres, out northMetres);

    private static bool TryNumber(string[] fields, Dictionary<string, int> columns, string column, out double value)
    {
        value = 0.0;
        return Cell(fields, columns, column) is { } cell
            && double.TryParse(
                cell.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
    }

    private static string? Cell(string[] fields, Dictionary<string, int> columns, string column)
    {
        int index = columns[column];
        return index < fields.Length ? fields[index] : null;
    }
}
