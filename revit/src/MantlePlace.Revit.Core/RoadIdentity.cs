using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>The road step's decision: which rows to draw, and how many were already there.</summary>
public sealed class RoadDecision
{
    /// <summary>Zero-based positions in the parsed road list still to be drawn, in order.</summary>
    public IReadOnlyList<int> RowsToCreate { get; init; } = [];

    /// <summary>How many rows an earlier import of this bundle already drew.</summary>
    public int AlreadyPresent { get; init; }
}

/// <summary>
/// Decides which road centrelines an import still has to draw, and the stamp each one carries. Pure.
/// </summary>
/// <remarks>
/// <para>
/// The stamp is <c>Mantle Place Road {stem}/{row}</c>, in the centreline's instance Comments, with
/// the row the one-based position in the parsed layer. Before it existed a re-import drew every road
/// a second time on top of the first, because a centreline carried nothing a later import could
/// recognise it by.
/// </para>
/// <para>
/// Position rather than name, because road names repeat — a street is many centrelines — and many
/// centrelines have none. The stamp carries no build token, unlike a tree's: nothing about a road
/// refuses a re-import, so there is no stale arm for a build token to decide.
/// </para>
/// <para>
/// ⛔ Nothing here deletes, for the terrain's reason (ADR 0004): an element a curator may have moved,
/// hidden or scheduled is theirs to remove.
/// </para>
/// </remarks>
public static class RoadIdentity
{
    private const string Prefix = "Mantle Place Road ";

    /// <summary>The stamp for one road.</summary>
    public static string Stamp(string cacheKeyStem, int oneBasedRow)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        return Prefix + cacheKeyStem + "/" + oneBasedRow.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// What the road step draws, given the Comments of every element that might be a road.
    /// </summary>
    /// <param name="existingComments">
    /// Comments of the candidate elements. Anything that is not one of this layer's stamps is
    /// ignored, so a caller may pass more than it needs to.
    /// </param>
    /// <param name="cacheKeyStem">Whose roads these are.</param>
    /// <param name="rowCount">How many centrelines the parsed layer carries.</param>
    public static RoadDecision Decide(IEnumerable<string?> existingComments, string cacheKeyStem, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(existingComments);
        ArgumentNullException.ThrowIfNull(cacheKeyStem);

        // Whole-stamp comparison, so the separator is in it: a stem this one is a prefix of never
        // reads as this order's.
        HashSet<string> present = new(existingComments.OfType<string>(), StringComparer.Ordinal);

        List<int> rows = [];
        int alreadyPresent = 0;
        for (int row = 0; row < rowCount; row++)
        {
            if (present.Contains(Stamp(cacheKeyStem, row + 1)))
            {
                alreadyPresent++;
            }
            else
            {
                rows.Add(row);
            }
        }

        return new RoadDecision { RowsToCreate = rows, AlreadyPresent = alreadyPresent };
    }
}
