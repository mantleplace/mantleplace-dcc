using System.Globalization;

namespace MantlePlace.Core;

public enum BuildingDisposition
{
    Create,

    RefuseStale,
}

public sealed class BuildingDecision
{
    public required BuildingDisposition Disposition { get; init; }

    public IReadOnlyList<string> GlobalIdsToCreate { get; init; } = [];

    public int AlreadyPresent { get; init; }

    public string Explanation { get; init; } = string.Empty;
}

public static class BuildingIdentity
{
    private const string Prefix = "Mantle Place Building ";

    public static string Stamp(string cacheKeyStem, string? siteModelSha256, string globalId)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        ArgumentNullException.ThrowIfNull(globalId);
        return Prefix + cacheKeyStem + "/" + TerrainIdentity.BuildToken(siteModelSha256) + "/" + globalId;
    }

    public static BuildingDecision Decide(
        IEnumerable<string?> existingComments,
        string cacheKeyStem,
        string? siteModelSha256,
        IReadOnlyList<string> globalIds)
    {
        ArgumentNullException.ThrowIfNull(existingComments);
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        ArgumentNullException.ThrowIfNull(globalIds);

        string ours = Prefix + cacheKeyStem + "/";
        string thisBuild = ours + TerrainIdentity.BuildToken(siteModelSha256) + "/";

        HashSet<string> present = new(StringComparer.Ordinal);
        int stale = 0;
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
            else
            {
                stale++;
            }
        }

        if (stale > 0)
        {
            return new BuildingDecision
            {
                Disposition = BuildingDisposition.RefuseStale,
                Explanation = string.Format(
                    CultureInfo.InvariantCulture,
                    "The context buildings in this project came from an EARLIER build of this bundle ({0:N0} "
                        + "of them), so no second set was created on top of them — and they were not deleted. "
                        + "To take the new buildings, delete the elements whose Comments begin \"{1}\" and "
                        + "import again.",
                    stale,
                    ours),
            };
        }

        List<string> toCreate = [];
        HashSet<string> queued = new(StringComparer.Ordinal);
        int alreadyPresent = 0;
        foreach (string globalId in globalIds)
        {
            if (!queued.Add(globalId))
            {
                continue;
            }

            if (present.Contains(thisBuild + globalId))
            {
                alreadyPresent++;
            }
            else
            {
                toCreate.Add(globalId);
            }
        }

        return new BuildingDecision
        {
            Disposition = BuildingDisposition.Create,
            GlobalIdsToCreate = toCreate,
            AlreadyPresent = alreadyPresent,
        };
    }
}
