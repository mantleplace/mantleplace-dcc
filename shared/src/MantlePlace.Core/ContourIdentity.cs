using System.Globalization;

namespace MantlePlace.Core;

public enum ContourDisposition
{
    Create,

    Reuse,

    RefuseStale,
}

public sealed class ContourDecision
{
    public required ContourDisposition Disposition { get; init; }

    public int AlreadyPresent { get; init; }

    public string Explanation { get; init; } = string.Empty;
}

public static class ContourIdentity
{
    private const string Prefix = "Mantle Place Contours ";

    public static string Stamp(string cacheKeyStem, string? contoursSha256)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        return Prefix + cacheKeyStem + "/" + TerrainIdentity.BuildToken(contoursSha256);
    }

    public static ContourDecision Decide(IEnumerable<string?> existingComments, string cacheKeyStem, string? contoursSha256)
    {
        ArgumentNullException.ThrowIfNull(existingComments);
        ArgumentNullException.ThrowIfNull(cacheKeyStem);

        string ours = Prefix + cacheKeyStem + "/";
        string thisBuild = Stamp(cacheKeyStem, contoursSha256);

        int present = 0;
        int stale = 0;
        foreach (string? comments in existingComments)
        {
            if (comments is null || !comments.StartsWith(ours, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(comments, thisBuild, StringComparison.Ordinal))
            {
                present++;
            }
            else
            {
                stale++;
            }
        }

        if (stale > 0)
        {
            return new ContourDecision
            {
                Disposition = ContourDisposition.RefuseStale,
                Explanation = string.Format(
                    CultureInfo.InvariantCulture,
                    "The published contours in this project came from an EARLIER build of this bundle ({0:N0} "
                        + "of them), so no second set was drawn over them — and they were not deleted. To take "
                        + "the new contours, delete the elements whose Comments begin \"{1}\" and import again.",
                    stale,
                    ours),
            };
        }

        return new ContourDecision
        {
            Disposition = present > 0 ? ContourDisposition.Reuse : ContourDisposition.Create,
            AlreadyPresent = present,
        };
    }
}
