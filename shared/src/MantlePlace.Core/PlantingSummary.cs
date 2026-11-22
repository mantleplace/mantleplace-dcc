using System.Globalization;
using System.Text;

namespace MantlePlace.Core;

public sealed record PlantingTally
{
    public required string EntryName { get; init; }

    public int PointCount { get; init; }

    public bool HasVocabulary { get; init; }

    public int TreesCreated { get; init; }

    public int ShrubsCreated { get; init; }

    public bool TreesAsFamily { get; init; }

    public bool ShrubsAsFamily { get; init; }

    public int RowsWithoutGround { get; init; }

    public int UnreadableRows { get; init; }

    public int AlreadyPresent { get; init; }

    public int Unbuildable { get; init; }

    public int Unsized { get; init; }

    public int Unstamped { get; init; }

    public int EmptyFoliageCells { get; init; }

    public int UnknownFoliageValues { get; init; }
}

public static class PlantingSummary
{
    public static string Sentence(PlantingTally tally)
    {
        ArgumentNullException.ThrowIfNull(tally);

        int created = tally.TreesCreated + tally.ShrubsCreated;
        StringBuilder sentence = new();
        if (!tally.HasVocabulary)
        {
            sentence.Append(Invariant($"Imported {created:N0} tree(s) of {tally.PointCount:N0} from {tally.EntryName}"));
            if (created > 0)
            {
                sentence.Append(tally.TreesAsFamily ? $" as \"{TreeFamily.FamilyName}\" instances" : " as DirectShapes");
            }
        }
        else
        {
            sentence.Append(Invariant($"Imported {created:N0} tree point(s) of {tally.PointCount:N0} from {tally.EntryName}"));
            List<string> breakdown = [];
            if (tally.TreesCreated > 0)
            {
                breakdown.Add(Invariant($"{tally.TreesCreated:N0} tree(s) as {Path(TreeFamily.FamilyName, tally.TreesAsFamily)}"));
            }

            if (tally.ShrubsCreated > 0)
            {
                breakdown.Add(Invariant($"{tally.ShrubsCreated:N0} shrub(s) as {Path(ShrubFamily.FamilyName, tally.ShrubsAsFamily)}"));
            }

            if (breakdown.Count > 0)
            {
                sentence.Append(": ").AppendJoin(", ", breakdown);
            }
        }

        Suffix(sentence, tally.RowsWithoutGround, "had no ground elevation in the file and were left out");
        Suffix(sentence, tally.UnreadableRows, "could not be read and were left out");
        Suffix(sentence, tally.AlreadyPresent, "from an earlier import of this build were already present and left alone");
        Suffix(sentence, tally.Unbuildable, "had a height or crown too small for Revit to build and were left out");
        Suffix(sentence, tally.Unsized, "would not take their published size or elevation and stand at the family's default");
        Suffix(sentence, tally.Unstamped, "could not be stamped and will not be recognised by a re-import");
        Suffix(sentence, tally.EmptyFoliageCells, "had no foliage type and were read as trees");
        Suffix(sentence, tally.UnknownFoliageValues, "had a foliage type this plugin does not know and were read as trees");
        return sentence.Append('.').ToString();
    }

    private static string Path(string familyName, bool asFamily)
        => asFamily ? $"\"{familyName}\" instances" : "DirectShapes";

    private static void Suffix(StringBuilder sentence, int count, string what)
    {
        if (count > 0)
        {
            sentence.Append(Invariant($"; {count:N0} {what}"));
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
