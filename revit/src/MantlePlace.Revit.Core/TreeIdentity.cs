using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>What the tree step does about the trees this project already has.</summary>
public enum TreeDisposition
{
    /// <summary>Create every row in <see cref="TreeDecision.RowsToCreate"/>, which may be none.</summary>
    Create,

    /// <summary>
    /// This order's trees are here from a DIFFERENT build. Create nothing, and delete nothing.
    /// </summary>
    RefuseStale,
}

/// <summary>The tree step's decision, and the sentence a curator gets when it refuses.</summary>
public sealed class TreeDecision
{
    public required TreeDisposition Disposition { get; init; }

    /// <summary>Zero-based positions in the parsed tree list still to be created, in order.</summary>
    public IReadOnlyList<int> RowsToCreate { get; init; } = [];

    /// <summary>How many rows an earlier import of this same build already created.</summary>
    public int AlreadyPresent { get; init; }

    /// <summary>One line for the log on the refuse arm; empty otherwise.</summary>
    public string Explanation { get; init; } = string.Empty;
}

/// <summary>
/// Decides which published trees an import still has to create, and the stamp each one carries.
/// Pure.
/// </summary>
/// <remarks>
/// <para>
/// The stamp is <c>Mantle Place Tree {stem}/{build}/{row}</c>, in the tree's instance Comments. The
/// stem is whose trees these are, the build is which tree-points file they came from — the same
/// twelve-character token the terrain's stamp carries — and the row is the one-based position in
/// that file. <c>docs/adr/0004-revit-terrain-identity.md</c>'s table then applies per row: this build's
/// rows that are present are reused, the rest are created, and trees from an earlier build of the
/// same order refuse the step rather than stacking a second forest on the first.
/// </para>
/// <para>
/// <b>Per row, because the tree step is chunked.</b> A cancel between two chunks leaves the committed
/// chunks in the project, stamped; a re-import of the same build finds them and creates the rest. A
/// step-level stamp could only have said "some of this bundle's trees are here", which cannot tell a
/// finished import from a cancelled one.
/// </para>
/// <para>
/// ⛔ Nothing here deletes, for the terrain's reason: an element a curator may have moved, hidden or
/// scheduled is theirs to remove. The stale arm names what to delete and stops.
/// </para>
/// </remarks>
public static class TreeIdentity
{
    private const string Prefix = "Mantle Place Tree ";

    /// <summary>The stamp for one tree.</summary>
    public static string Stamp(string cacheKeyStem, string? artifactSha256, int oneBasedRow)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        return Prefix + cacheKeyStem + "/" + TerrainIdentity.BuildToken(artifactSha256) + "/"
            + oneBasedRow.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// What the tree step does, given the Comments of every element that might be a tree.
    /// </summary>
    /// <param name="existingComments">
    /// Comments of the candidate elements. Anything that is not this bundle's stamp is ignored, so a
    /// caller may pass more than it needs to.
    /// </param>
    /// <param name="rowCount">How many trees the tree-points file publishes.</param>
    public static TreeDecision Decide(
        IEnumerable<string?> existingComments,
        string cacheKeyStem,
        string? artifactSha256,
        int rowCount)
    {
        ArgumentNullException.ThrowIfNull(existingComments);
        ArgumentNullException.ThrowIfNull(cacheKeyStem);

        // Ordinal over the whole prefix INCLUDING the separator, for SiteBoundaryIdentity's reason:
        // cache-key stems are truncated hashes, where one being a prefix of another is a collision
        // waiting rather than a hypothetical.
        string ours = Prefix + cacheKeyStem + "/";
        string thisBuild = ours + TerrainIdentity.BuildToken(artifactSha256) + "/";

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

        return new TreeDecision
        {
            Disposition = TreeDisposition.Create,
            RowsToCreate = rows,
            AlreadyPresent = alreadyPresent,
        };
    }
}
