using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>What the context-building step does about the buildings this project already has.</summary>
public enum BuildingDisposition
{
    /// <summary>Create every building in <see cref="BuildingDecision.GlobalIdsToCreate"/>, which may be none.</summary>
    Create,

    /// <summary>
    /// This order's buildings are here from a DIFFERENT build. Create nothing, and delete nothing.
    /// </summary>
    RefuseStale,
}

/// <summary>The context-building step's decision, and the sentence a curator gets when it refuses.</summary>
public sealed class BuildingDecision
{
    public required BuildingDisposition Disposition { get; init; }

    /// <summary>The GlobalIds still to be created, in the site model's order, each once.</summary>
    public IReadOnlyList<string> GlobalIdsToCreate { get; init; } = [];

    /// <summary>How many buildings an earlier import of this same build already created.</summary>
    public int AlreadyPresent { get; init; }

    /// <summary>One line for the log on the refuse arm; empty otherwise.</summary>
    public string Explanation { get; init; } = string.Empty;
}

/// <summary>
/// Decides which of the site model's buildings an import still has to create, and the stamp each
/// one carries. Pure.
/// </summary>
/// <remarks>
/// <para>
/// The stamp is <c>Mantle Place Building {stem}/{build}/{GlobalId}</c>, in the element's instance
/// Comments. The stem is whose buildings these are; the build is which site model they came from —
/// the same twelve-character token of its declared sha256 that the terrain's stamp carries; and the
/// GlobalId is the IFC's own identity for the building. <c>docs/adr/0004-revit-terrain-identity.md</c>'s
/// table then applies per building, exactly as <see cref="TreeIdentity"/> applies it per row.
/// </para>
/// <para>
/// ⛔ <b>The build half is not redundant with the GlobalId.</b> An emitter is free to keep a
/// building's GlobalId stable across builds — that is what a GlobalId is for — so a GlobalId match
/// alone cannot tell this build's extrusion from last month's. The build token is what refuses the
/// older set rather than reusing it.
/// </para>
/// <para>
/// ⛔ Nothing here deletes, for the terrain's reason: a context building a curator has moved, hidden,
/// scheduled or joined to their own design is theirs to remove. The stale arm names what to delete
/// and stops.
/// </para>
/// </remarks>
public static class BuildingIdentity
{
    private const string Prefix = "Mantle Place Building ";

    /// <summary>The stamp for one building.</summary>
    public static string Stamp(string cacheKeyStem, string? siteModelSha256, string globalId)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        ArgumentNullException.ThrowIfNull(globalId);
        return Prefix + cacheKeyStem + "/" + TerrainIdentity.BuildToken(siteModelSha256) + "/" + globalId;
    }

    /// <summary>
    /// What the context-building step does, given the Comments of every element that might be one.
    /// </summary>
    /// <param name="existingComments">
    /// Comments of the candidate elements. Anything that is not this bundle's building stamp is
    /// ignored, so a caller may pass more than it needs to.
    /// </param>
    /// <param name="globalIds">The site model's buildings, as <see cref="SiteModelReader"/> read them.</param>
    public static BuildingDecision Decide(
        IEnumerable<string?> existingComments,
        string cacheKeyStem,
        string? siteModelSha256,
        IReadOnlyList<string> globalIds)
    {
        ArgumentNullException.ThrowIfNull(existingComments);
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        ArgumentNullException.ThrowIfNull(globalIds);

        // Ordinal over the whole prefix INCLUDING the separator, for SiteBoundaryIdentity's reason:
        // cache-key stems are truncated hashes, where one being a prefix of another is a collision
        // waiting rather than a hypothetical.
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
