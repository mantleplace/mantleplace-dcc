using System.Globalization;

namespace MantlePlace.Core;

public sealed class RoadDecision
{
    public IReadOnlyList<int> RowsToCreate { get; init; } = [];

    public int AlreadyPresent { get; init; }
}

public static class RoadIdentity
{
    private const string Prefix = "Mantle Place Road ";

    public static string Stamp(string cacheKeyStem, int oneBasedRow)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        return Prefix + cacheKeyStem + "/" + oneBasedRow.ToString(CultureInfo.InvariantCulture);
    }

    public static RoadDecision Decide(IEnumerable<string?> existingComments, string cacheKeyStem, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(existingComments);
        ArgumentNullException.ThrowIfNull(cacheKeyStem);

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
