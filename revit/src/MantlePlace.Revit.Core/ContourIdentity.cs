using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>What the published-contours step does about the contours this project already has.</summary>
public enum ContourDisposition
{
    /// <summary>No contours of this order are here: draw the layer.</summary>
    Create,

    /// <summary>This build's contours are here already. Draw nothing.</summary>
    Reuse,

    /// <summary>This order's contours are here from a DIFFERENT build. Draw nothing, and delete nothing.</summary>
    RefuseStale,
}

/// <summary>The published-contours step's decision, and the sentence a curator gets when it refuses.</summary>
public sealed class ContourDecision
{
    public required ContourDisposition Disposition { get; init; }

    /// <summary>How many elements of this build the project already holds.</summary>
    public int AlreadyPresent { get; init; }

    /// <summary>One line for the log on the refuse arm; empty otherwise.</summary>
    public string Explanation { get; init; } = string.Empty;
}

/// <summary>
/// Decides whether an import draws the published contours, and the stamp each one carries. Pure.
/// </summary>
/// <remarks>
/// <para>
/// The stamp is <c>Mantle Place Contours {stem}/{build}</c>, in every contour's instance Comments:
/// one stamp for the layer, because the build is the point. The build half is the twelve-character
/// token of the contours file's declared sha256, as the terrain's stamp carries.
/// <c>docs/adr/0004-revit-terrain-identity.md</c>'s table applies to the layer as a whole
/// (ADR 0013): this build's contours are reused, and an earlier build's refuse the step, because two
/// builds' contours in one project would overlap as two interleaved sets nothing tells apart.
/// </para>
/// <para>
/// A layer-wide stamp has no row to resume from, so the step that draws the layer commits it in one
/// transaction: any element of this build means all of them.
/// </para>
/// <para>
/// ⛔ Nothing here deletes, for the terrain's reason. The stale arm names what to delete and stops.
/// </para>
/// </remarks>
public static class ContourIdentity
{
    private const string Prefix = "Mantle Place Contours ";

    /// <summary>The stamp every contour drawn from <paramref name="contoursSha256"/> carries.</summary>
    public static string Stamp(string cacheKeyStem, string? contoursSha256)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        return Prefix + cacheKeyStem + "/" + TerrainIdentity.BuildToken(contoursSha256);
    }

    /// <summary>What the step does, given the Comments of every element that might be a contour.</summary>
    /// <param name="existingComments">
    /// Comments of the candidate elements. Anything that is not this bundle's contour stamp is
    /// ignored, so a caller may pass more than it needs to.
    /// </param>
    public static ContourDecision Decide(IEnumerable<string?> existingComments, string cacheKeyStem, string? contoursSha256)
    {
        ArgumentNullException.ThrowIfNull(existingComments);
        ArgumentNullException.ThrowIfNull(cacheKeyStem);

        // Ordinal over the whole prefix INCLUDING the separator: cache-key stems are truncated
        // hashes, where one being a prefix of another is a collision waiting.
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
