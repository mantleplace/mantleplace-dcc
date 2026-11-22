using System.Globalization;

namespace MantlePlace.Core;

public enum TreeDisposition
{
    Create,

    RefuseStale,

    RefuseOtherLevel,
}

public sealed class TreeDecision
{
    public required TreeDisposition Disposition { get; init; }

    public IReadOnlyList<int> RowsToCreate { get; init; } = [];

    public int AlreadyPresent { get; init; }

    public int KeptAboveLevel { get; init; }

    public string Explanation { get; init; } = string.Empty;
}

public readonly record struct ExistingTreePoint(string? Comments, string? FamilyName);

public static class TreeIdentity
{
    private const string Prefix = "Mantle Place Tree ";

    public static string Stamp(string cacheKeyStem, string? artifactSha256, int oneBasedRow)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        return BuildPrefix(cacheKeyStem, artifactSha256) + oneBasedRow.ToString(CultureInfo.InvariantCulture);
    }

    public static TreeDecision Decide(
        IEnumerable<string?> existingComments,
        string cacheKeyStem,
        string? artifactSha256,
        int rowCount,
        IReadOnlyCollection<string>? levelFiles = null)
    {
        ArgumentNullException.ThrowIfNull(existingComments);
        ArgumentNullException.ThrowIfNull(cacheKeyStem);

        string ours = Prefix + cacheKeyStem + "/";
        string thisBuild = BuildPrefix(cacheKeyStem, artifactSha256);

        HashSet<string> present = new(StringComparer.Ordinal);
        HashSet<string> otherLevels = [.. (levelFiles ?? []).Select(sha => BuildPrefix(cacheKeyStem, sha))];
        otherLevels.Remove(thisBuild);
        int stale = 0;
        int otherLevel = 0;
        foreach (string? comments in existingComments)
        {
            if (comments is null || !comments.StartsWith(ours, StringComparison.Ordinal))
            {
                continue;
            }

            if (comments.StartsWith(thisBuild, StringComparison.Ordinal))
            {
                present.Add(comments);
            }
            else if (otherLevels.Any(prefix => comments.StartsWith(prefix, StringComparison.Ordinal)))
            {
                otherLevel++;
            }
            else
            {
                stale++;
            }
        }

        if (otherLevel > 0 && stale == 0)
        {
            return new TreeDecision
            {
                Disposition = TreeDisposition.RefuseOtherLevel,
                Explanation = string.Format(
                    CultureInfo.InvariantCulture,
                    "The trees in this project are this bundle's at another fidelity level ({0:N0} of them), "
                        + "published as a file of its own, and its rows cannot be matched to this level's — so no "
                        + "second set was created on top of them, and they were not deleted. To change level, "
                        + "delete the elements whose Comments begin \"{1}\" and import again.",
                    otherLevel,
                    ours),
            };
        }

        if (stale > 0)
        {
            return new TreeDecision
            {
                Disposition = TreeDisposition.RefuseStale,
                Explanation = string.Format(
                    CultureInfo.InvariantCulture,
                    "The trees in this project came from an EARLIER build of this bundle ({0:N0} of them), "
                        + "so no second set was created on top of them — and they were not deleted. To take "
                        + "the new trees, delete the elements whose Comments begin \"{1}\" and import again.",
                    stale,
                    ours),
            };
        }

        List<int> rows = [];
        int alreadyPresent = 0;
        for (int row = 0; row < rowCount; row++)
        {
            if (present.Contains(Stamp(cacheKeyStem, artifactSha256, row + 1)))
            {
                alreadyPresent++;
            }
            else
            {
                rows.Add(row);
            }
        }

        int keptAbove = present.Count(stamp =>
            int.TryParse(stamp.AsSpan(thisBuild.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int row)
            && row > rowCount);

        return new TreeDecision
        {
            Disposition = TreeDisposition.Create,
            RowsToCreate = rows,
            AlreadyPresent = alreadyPresent,
            KeptAboveLevel = keptAbove,
        };
    }

    public static int FoliageMismatches(
        IEnumerable<ExistingTreePoint> existing,
        string cacheKeyStem,
        string? artifactSha256,
        IReadOnlyList<SiteTreePoint> points)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        ArgumentNullException.ThrowIfNull(points);

        string thisBuild = BuildPrefix(cacheKeyStem, artifactSha256);
        int mismatches = 0;
        foreach (ExistingTreePoint element in existing)
        {
            if (PlantingFamilies.FoliageTypeOf(element.FamilyName) is not { } family
                || element.Comments is not { } comments
                || !comments.StartsWith(thisBuild, StringComparison.Ordinal)
                || !int.TryParse(comments.AsSpan(thisBuild.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int row)
                || row < 1
                || row > points.Count)
            {
                continue;
            }

            mismatches += points[row - 1].FoliageType == family ? 0 : 1;
        }

        return mismatches;
    }

    public static string FoliageMismatchNote(int mismatches, string cacheKeyStem, string? artifactSha256)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        return mismatches <= 0
            ? string.Empty
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0:N0} tree point(s) from an earlier import of this build stand as the family of a different "
                    + "foliage type than the one now published — they were placed by a plugin that did not read "
                    + "it, and were left as they are. To rebuild them, delete the elements whose Comments begin "
                    + "\"{1}\" and import again.",
                mismatches,
                BuildPrefix(cacheKeyStem, artifactSha256));
    }

    private static string BuildPrefix(string cacheKeyStem, string? artifactSha256)
        => Prefix + cacheKeyStem + "/" + TerrainIdentity.BuildToken(artifactSha256) + "/";
}
