using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>One toposolid the project already holds as GROUND — not as another toposolid's subdivision.</summary>
/// <param name="ElementId">Revit's <c>ElementId</c> value, carried as a number so the core stays Revit-free.</param>
/// <param name="Comments">Its instance Comments parameter, or <c>null</c> when it carries none.</param>
public readonly record struct ExistingTerrain(long ElementId, string? Comments);

/// <summary>What the terrain step does about the ground this project already has.</summary>
public enum TerrainDisposition
{
    /// <summary>Nothing of this bundle's is here. Build the terrain and stamp it.</summary>
    Create,

    /// <summary>This bundle's terrain, from this bundle's surface, is already here. Leave it alone.</summary>
    Reuse,

    /// <summary>
    /// This bundle's terrain is here, but built from a DIFFERENT surface — an earlier build of the
    /// same order. Do not create a second one, and do not delete the first.
    /// </summary>
    RefuseStale,
}

/// <summary>The terrain step's decision, and the sentence a curator gets for it.</summary>
public sealed class TerrainDecision
{
    public required TerrainDisposition Disposition { get; init; }

    /// <summary>The ground this bundle already owns, or <c>0</c> on <see cref="TerrainDisposition.Create"/>.</summary>
    public long ExistingElementId { get; init; }

    /// <summary>The stamp a created terrain must carry. Set on every arm, so a caller cannot forget it.</summary>
    public string Stamp { get; init; } = string.Empty;

    /// <summary>
    /// One line for the import log. Empty ONLY for a first import into a project with no ground at
    /// all, where there is nothing a curator needs told.
    /// </summary>
    public string Explanation { get; init; } = string.Empty;
}

/// <summary>
/// Decides whether the terrain step builds a toposolid, reuses the one an earlier import built, or
/// refuses — and what identity a new one is stamped with.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ This exists because the terrain step had no "already here" check at all while every other
/// repeatable step did, so a second import of one bundle left <em>two</em> ground toposolids, one on
/// top of the other, each with its own subdivisions. The subdivision guard could not save it: that
/// guard reads the stamps on <em>the terrain it is working on</em>, and the terrain step handed it a
/// brand-new toposolid every run, so it found nothing and rebuilt the whole set. Giving the ground
/// an identity is what makes the guard above it true.
/// </para>
/// <para>
/// <b>The stamp has two halves and they answer different questions.</b> It is
/// <c>Mantle Place Terrain {stem}/{build}</c>: the stem is the cache key — the sanitised order id,
/// or the zip's path for a bundle that declares no order — and says <em>whose ground this is</em>;
/// the build token is the first twelve hex characters of the surface artifact's declared sha256 and
/// says <em>which build of it</em>. That is the distinction
/// <c>docs/adr/0002-import-identity.md</c> drew for the other host, and the one
/// <see cref="BundleManifest.JobId"/> and <see cref="BundleManifest.OrderId"/> draw in the manifest:
/// an identity for a thing the user owns is not an identity for a build, and using one where the
/// other belongs is what makes a re-import non-idempotent.
/// </para>
/// <para>
/// Which half matched decides the arm. Both halves match and it is <em>the same ground</em>, so it
/// is reused and nothing is rebuilt. The stem matches and the build token does not, and the project
/// holds this order's terrain from an earlier build: that is refused rather than stacked, and
/// refused rather than replaced. Neither half matches and the bundle is a stranger to this project,
/// so its terrain is built.
/// </para>
/// <para>
/// ⛔ <b>Nothing here deletes.</b> The other host answers this same bug by replacing prior content,
/// which is right where the imported content is generated and disposable. In Revit it is not: a
/// curator hosts buildings on the ground, draws their own subdivisions on it and sets up views
/// against it, and deleting a toposolid takes its subdivisions — including the ones this plugin's
/// own drape repairs on a re-import — with it. So the stale arm says the terrain is there and how to
/// remove it, and leaves the removing to the person who knows what is standing on it. That is the
/// line <see cref="SiteBoundaryIdentity"/> already draws: touch what this import owns, and nothing
/// else.
/// </para>
/// <para>
/// An <em>unstamped</em> ground is never claimed. It is a curator's own toposolid, or one from an
/// import made before terrain stamping existed, and neither is this bundle's to reuse or refuse. The
/// terrain is built alongside it and the log says so — the complaint in the bug this fixes was not
/// only that a second ground appeared, but that it appeared <em>silently</em>.
/// </para>
/// <para>
/// Pure, and here rather than in the shim for the reason <see cref="SiteBoundaryIdentity"/> and
/// <see cref="ImportStepKinds.LifetimeOf"/> are: the create/reuse/refuse decision is policy, and
/// policy in the shim is covered by nothing but review (<c>HPS-02</c>).
/// </para>
/// </remarks>
public static class TerrainIdentity
{
    private const string Prefix = "Mantle Place Terrain ";

    /// <summary>
    /// The build token for a bundle that declares no sha256 for its surface.
    /// </summary>
    /// <remarks>
    /// ⛔ A null sha is <em>unknown</em>, never "verified" and never "absent" (<c>HPS-27</c>,
    /// ⛔<c>HPS-28</c>), and this token keeps that true through the identity too. Two imports of the
    /// same digest-less bundle both stamp <c>unknown</c>, match, and reuse — the honest answer, since
    /// nothing distinguishes them. An <c>unknown</c> ground meeting a bundle that DOES declare a
    /// digest does not match, and takes the stale arm rather than claiming a sameness nobody can
    /// demonstrate. It is not hex, so it can never collide with a real token.
    /// </remarks>
    public const string UnknownBuild = "unknown";

    /// <summary>
    /// How much of the sha256 the stamp carries. Forty-eight bits is far past what telling one build
    /// of one order from another needs, and a whole digest in a Comments box a curator can see is
    /// noise.
    /// </summary>
    private const int BuildTokenLength = 12;

    /// <summary>The stamp a terrain built from <paramref name="artifactSha256"/> carries.</summary>
    public static string Stamp(string cacheKeyStem, string? artifactSha256)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        return Prefix + cacheKeyStem + "/" + BuildToken(artifactSha256);
    }

    /// <summary>Whether a Comments string is a terrain stamp THIS plugin wrote for THIS bundle.</summary>
    /// <remarks>
    /// Ordinal over the whole prefix INCLUDING the separator, for
    /// <see cref="SiteBoundaryIdentity.IsStampFor"/>'s reason: cache-key stems are truncated hashes,
    /// where one being a prefix of another is a collision waiting rather than a hypothetical.
    /// </remarks>
    public static bool IsStampFor(string? comments, string cacheKeyStem)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);

        string prefix = Prefix + cacheKeyStem + "/";
        return comments is not null
            && comments.StartsWith(prefix, StringComparison.Ordinal)
            && comments.Length > prefix.Length;
    }

    /// <summary>
    /// Whether a Comments string is a terrain stamp this plugin wrote, <em>whichever</em> bundle
    /// wrote it.
    /// </summary>
    /// <remarks>
    /// The stem-blind form, for a reader — the terrain probe — that is describing a project rather
    /// than deciding about one, and that has no bundle in hand to ask about. ⛔ Decisions use
    /// <see cref="IsStampFor"/> instead: another order's ground is not this import's to reuse or to
    /// refuse, and a check that cannot tell the two apart would break the trespass rule.
    /// </remarks>
    public static bool IsStamp(string? comments)
        => comments is not null
            && comments.StartsWith(Prefix, StringComparison.Ordinal)
            && comments.Length > Prefix.Length;

    /// <summary>
    /// The build half of a stamp this bundle owns — the part after the stem — or <c>null</c> for any
    /// other Comments.
    /// </summary>
    public static string? BuildTokenOf(string? comments, string cacheKeyStem)
        => IsStampFor(comments, cacheKeyStem) ? comments![(Prefix + cacheKeyStem + "/").Length..] : null;

    /// <summary>
    /// What the terrain step does, given every ground toposolid the project holds.
    /// </summary>
    /// <param name="grounds">
    /// Ground toposolids only — a subdivision is itself a toposolid, and one listed in another's
    /// subdivision ids is not a candidate. Order is the caller's; the first ground stamped for this
    /// bundle wins.
    /// </param>
    public static TerrainDecision Decide(
        IReadOnlyList<ExistingTerrain> grounds,
        string cacheKeyStem,
        string? artifactSha256)
    {
        ArgumentNullException.ThrowIfNull(grounds);
        ArgumentNullException.ThrowIfNull(cacheKeyStem);

        string wanted = BuildToken(artifactSha256);
        string stamp = Prefix + cacheKeyStem + "/" + wanted;

        int ours = 0;
        ExistingTerrain? first = null;
        foreach (ExistingTerrain ground in grounds)
        {
            if (IsStampFor(ground.Comments, cacheKeyStem))
            {
                ours++;
                first ??= ground;
            }
        }

        if (first is not { } mine)
        {
            return new TerrainDecision
            {
                Disposition = TerrainDisposition.Create,
                Stamp = stamp,

                // Silence here is correct: a first import into a project with no ground has nothing
                // to report, and a line every time would bury the case that matters.
                Explanation = grounds.Count == 0
                    ? string.Empty
                    : $"This project already has {Strangers(grounds.Count)} — a curator's own, another "
                        + "order's, or one from an import made before terrain stamping. Nothing was "
                        + "touched, and this bundle's terrain was built alongside it.",
            };
        }

        string found = BuildTokenOf(mine.Comments, cacheKeyStem)!;
        string duplicates = ours > 1
            ? $" ({Number(ours)} ground toposolids here carry this bundle's stamp, which an import "
                + "before this version could produce; the first is the one used.)"
            : string.Empty;

        if (!string.Equals(found, wanted, StringComparison.Ordinal))
        {
            return new TerrainDecision
            {
                Disposition = TerrainDisposition.RefuseStale,
                ExistingElementId = mine.ElementId,
                Stamp = stamp,
                Explanation =
                    "The terrain in this project came from an EARLIER build of this bundle, so no second "
                    + "ground was created on top of it — and it was not deleted, because a curator's "
                    + "buildings and views may be standing on it. To take the new surface, delete "
                    + $"toposolid {Number(mine.ElementId)} and import again."
                    + duplicates,
            };
        }

        return new TerrainDecision
        {
            Disposition = TerrainDisposition.Reuse,
            ExistingElementId = mine.ElementId,
            Stamp = stamp,
            Explanation =
                "This bundle's terrain is already in this project from an earlier import, so it was left "
                + "alone and no second ground was built."
                + (string.Equals(wanted, UnknownBuild, StringComparison.Ordinal)
                    ? " This bundle declares no digest for its surface, so a rebuilt surface would not "
                        + "have been noticed here."
                    : string.Empty)
                + duplicates,
        };
    }

    /// <summary>The build half of a stamp: a short, lower-case sha256 prefix, or <see cref="UnknownBuild"/>.</summary>
    private static string BuildToken(string? artifactSha256)
    {
        if (string.IsNullOrWhiteSpace(artifactSha256))
        {
            return UnknownBuild;
        }

        string trimmed = artifactSha256.Trim().ToLowerInvariant();
        return trimmed.Length <= BuildTokenLength ? trimmed : trimmed[..BuildTokenLength];
    }

    private static string Strangers(int howMany)
        => howMany == 1
            ? "a ground toposolid that is not this bundle's"
            : $"{Number(howMany)} ground toposolids that are not this bundle's";

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
