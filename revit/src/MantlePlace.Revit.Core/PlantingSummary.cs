using System.Globalization;
using System.Text;

namespace MantlePlace.Revit.Core;

/// <summary>What the planting step did, as its closing sentence needs to see it.</summary>
public sealed record PlantingTally
{
    /// <summary>The tree-points entry, as the step names it.</summary>
    public required string EntryName { get; init; }

    /// <summary>How many tree points the file published and the parse read.</summary>
    public int PointCount { get; init; }

    /// <summary>
    /// Whether the manifest named a foliage-type vocabulary. Without one every point is a tree, and
    /// the sentence reads exactly as it did before shrubs existed.
    /// </summary>
    public bool HasVocabulary { get; init; }

    public int TreesCreated { get; init; }

    public int ShrubsCreated { get; init; }

    /// <summary>Whether the trees were family instances rather than DirectShapes.</summary>
    public bool TreesAsFamily { get; init; }

    /// <summary>Whether the shrubs were family instances rather than DirectShapes.</summary>
    public bool ShrubsAsFamily { get; init; }

    /// <summary>Rows the parse left out for an empty <c>ground_z</c> (<see cref="TreePointsParse.RowsWithoutGround"/>).</summary>
    public int RowsWithoutGround { get; init; }

    /// <summary>Rows the parse left out as unreadable (<see cref="TreePointsParse.UnreadableRows"/>).</summary>
    public int UnreadableRows { get; init; }

    public int AlreadyPresent { get; init; }

    public int Unbuildable { get; init; }

    public int Unsized { get; init; }

    public int Unstamped { get; init; }

    /// <summary>Cells that were empty or absent (<see cref="TreePointsParse.EmptyFoliageCells"/>).</summary>
    public int EmptyFoliageCells { get; init; }

    /// <summary>Cells this build does not know (<see cref="TreePointsParse.UnknownFoliageValues"/>).</summary>
    public int UnknownFoliageValues { get; init; }
}

/// <summary>
/// The planting step's closing sentence. Pure.
/// </summary>
/// <remarks>
/// One count of tree points, then — only where the manifest publishes a vocabulary — a breakdown by
/// foliage type with each family's own path, then the suffixes the step has always had, as single
/// totals. A manifest without a vocabulary gets the sentence it got before shrubs existed, word for
/// word, so an order that cannot carry a shrub does not read as though it might have.
/// </remarks>
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

        // Rows the parse left out are said first: they never reached the "of" count, so without
        // these clauses a half-blank ground_z column reads as a whole layer.
        Suffix(sentence, tally.RowsWithoutGround, "had no ground elevation in the file and were left out");
        Suffix(sentence, tally.UnreadableRows, "could not be read and were left out");
        Suffix(sentence, tally.AlreadyPresent, "from an earlier import of this build were already present and left alone");
        Suffix(sentence, tally.Unbuildable, "had a height or crown too small for Revit to build and were left out");
        Suffix(sentence, tally.Unsized, "would not take their published size or elevation and stand at the family's default");
        Suffix(sentence, tally.Unstamped, "could not be stamped and will not be recognised by a re-import");
        Suffix(sentence, tally.EmptyFoliageCells, "had no foliage type and were read as trees");
        Suffix(sentence, tally.UnknownFoliageValues, "had a foliage type this add-in does not know and were read as trees");
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
