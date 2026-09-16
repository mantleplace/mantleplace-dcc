namespace MantlePlace.Revit.Core;

/// <summary>
/// Where the <c>.rvt</c> that Revit converts a site IFC into is written, and why it carries a Revit
/// version number.
/// </summary>
/// <remarks>
/// <para>
/// <c>RevitLinkType.CreateFromIFC</c> does not convert; it links an already-converted Revit file.
/// So linking a site IFC is two steps, and the first writes a <b>derived</b> file into the shared
/// per-order cache next to the IFC it came from.
/// </para>
/// <para>
/// ⛔ That derived file is the one thing in the cache that is <b>not</b> version-neutral. The zip
/// and its extracted entries are the same bytes for every Revit — which is what makes one download
/// per order right (<c>HPS-44</c>) — but a <c>.rvt</c> belongs to the Revit build that saved it, and
/// opening it in a newer Revit <b>upgrades it in place, one way, with no supported downgrade</b>.
/// Every version converting into one <c>Site.rvt</c> therefore meant the second Revit to import an
/// order silently destroyed the first one's companion: the older project's link stopped resolving,
/// as a broken link in Manage Links rather than as an import error, and re-importing did not repair
/// it because the file was still there. Naming the companion for its producer is what keeps each
/// version converting once and touching nothing else.
/// </para>
/// <para>
/// The companion stays <b>beside the IFC</b>, inside <c>extracted/</c>, rather than in a sibling
/// tree. Remove download deletes the per-order root recursively, so staying under it is what keeps
/// the sweep complete without a second place to remember.
/// </para>
/// <para>
/// A companion already sitting at the version-neutral path was produced by an unknown Revit
/// version, so nothing here rewrites or deletes it — <see cref="VersionNeutral"/> exists only so the
/// shim can still recognise a link an earlier build created and leave that project alone. The stale
/// file is dead weight the curator's Remove download clears.
/// </para>
/// <para>
/// This is path resolution, which is pure, so it lives here rather than in the shim: the shim reads
/// <c>Application.VersionNumber</c> and passes the string in.
/// </para>
/// </remarks>
public static class SiteCompanionPath
{
    private const string CompanionExtension = ".rvt";

    /// <summary>The companion the Revit identified by <paramref name="revitVersionNumber"/> converts into.</summary>
    /// <param name="ifcPath">The extracted site IFC, as the archive wrote it.</param>
    /// <param name="revitVersionNumber">
    /// <c>Autodesk.Revit.ApplicationServices.Application.VersionNumber</c> — the release year, as a
    /// string.
    /// </param>
    /// <remarks>
    /// The version goes through ⛔<see cref="CacheKeySanitiser"/> (<c>HPS-30</c>) rather than into
    /// the name raw. Revit returns <c>"2025"</c>, which the mapping passes through untouched; the
    /// sanitiser is there for the case where it does not, because this value becomes a file name and
    /// the alternatives — a traversal, or a throw that would abandon the step and every step after
    /// it — are both worse than a neutralised, still-collision-free token.
    /// </remarks>
    public static string ForVersion(string ifcPath, string revitVersionNumber)
    {
        ArgumentException.ThrowIfNullOrEmpty(ifcPath);
        ArgumentException.ThrowIfNullOrEmpty(revitVersionNumber);

        string token = CacheKeySanitiser.Sanitise(revitVersionNumber).DirectoryName;

        return Beside(ifcPath, "." + token + CompanionExtension);
    }

    /// <summary>
    /// The unqualified companion path that builds before per-version companions wrote, and that
    /// projects imported by one of those builds still link to.
    /// </summary>
    /// <remarks>
    /// Nothing writes this any more. It is still derived because the shim checks the project's
    /// existing links against it: a re-import that failed to recognise a link an older build created
    /// would call <c>CreateFromIFC</c> against an already-linked path, which throws.
    /// </remarks>
    public static string VersionNeutral(string ifcPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(ifcPath);

        return Beside(ifcPath, CompanionExtension);
    }

    /// <summary>
    /// Whether <paramref name="candidatePath"/> is a companion of <paramref name="ifcPath"/> — the
    /// version-neutral one, or the one any Revit version would convert into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked of a link a project already carries, which a DIFFERENT Revit version may have created:
    /// the answer has to cover companions this build would never write. So the rule is shape, not
    /// equality — a <c>.rvt</c> beside the IFC, named for it, with at most one extra dotted
    /// component. That component is the producing version's token, and it cannot itself contain a
    /// dot, which is what keeps <c>Site.v2.final.2026.rvt</c> from answering for <c>Site.v2.ifc</c>.
    /// </para>
    /// <para>
    /// One shape does slip through: two site IFCs in one directory whose stems differ by a single
    /// dotted component — <c>Site.ifc</c> and <c>Site.final.ifc</c> — would each claim the other's
    /// version-neutral companion. A bundle is one site model, so this is not a layout the format
    /// produces, and the cost of being wrong is a re-import declining to rebuild a link rather than
    /// anything destructive.
    /// </para>
    /// </remarks>
    public static bool IsCompanionOf(string ifcPath, string candidatePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(ifcPath);

        if (string.IsNullOrEmpty(candidatePath)
            || !CompanionExtension.Equals(Path.GetExtension(candidatePath), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetDirectoryName(candidatePath) ?? string.Empty,
                Path.GetDirectoryName(ifcPath) ?? string.Empty,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(ifcPath);
        string candidateStem = Path.GetFileNameWithoutExtension(candidatePath);

        if (string.Equals(candidateStem, stem, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = stem + ".";

        return candidateStem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !candidateStem[prefix.Length..].Contains('.', StringComparison.Ordinal);
    }

    /// <summary>
    /// <paramref name="ifcPath"/> with its extension replaced by <paramref name="suffix"/>, in the
    /// same directory.
    /// </summary>
    /// <remarks>
    /// Written out rather than layered on <see cref="Path.ChangeExtension(string, string)"/>, which
    /// replaces only the last extension: over <c>Site.2025.rvt</c> it would produce
    /// <c>Site.2025.2026.rvt</c>, and over a dotted entry name like <c>Site.v2.final.ifc</c> the
    /// stem is what must survive.
    /// </remarks>
    private static string Beside(string ifcPath, string suffix)
    {
        string directory = Path.GetDirectoryName(ifcPath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(ifcPath);

        return directory.Length == 0 ? stem + suffix : Path.Combine(directory, stem + suffix);
    }
}
