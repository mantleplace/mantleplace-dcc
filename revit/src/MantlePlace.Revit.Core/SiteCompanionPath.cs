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
/// version, so nothing here rewrites or deletes it; <see cref="IsCompanionOf"/> still answers for
/// it, so a project an earlier build linked is recognised and left alone. The stale file is dead
/// weight the curator's Remove download clears.
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
    /// Revit returns <c>"2025"</c>, which <see cref="TokenFor"/> passes through untouched. The
    /// sanitisation behind it is for the case where it does not.
    /// </remarks>
    public static string ForVersion(string ifcPath, string revitVersionNumber)
    {
        ArgumentException.ThrowIfNullOrEmpty(ifcPath);
        ArgumentException.ThrowIfNullOrEmpty(revitVersionNumber);

        return Beside(ifcPath, "." + TokenFor(revitVersionNumber) + CompanionExtension);
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

    /// <summary>One Revit version string, as the single dot-free file-name component it becomes.</summary>
    /// <remarks>
    /// <para>
    /// Two properties are needed and ⛔<see cref="CacheKeySanitiser"/> (<c>HPS-30</c>) already
    /// guarantees both: the result is filesystem-safe, and two different inputs never map to one
    /// output. The rule's own wording is about an order id becoming a directory, and this is a
    /// version becoming a file-name component — the same mapping over a different string, reused
    /// rather than reinvented, because a second sanitiser in this host is a second thing to get
    /// wrong.
    /// </para>
    /// <para>
    /// The dots are mapped to <c>/</c> <b>before</b> sanitising rather than removed after. A token
    /// with a dot in it would be more than one component, which is exactly what
    /// <see cref="IsCompanionOf"/> refuses — so <c>ForVersion</c> would write a companion that
    /// <c>IsCompanionOf</c> then failed to recognise, and the re-import would call
    /// <c>CreateFromIFC</c> against an already-linked path and throw. Pre-mapping to a character the
    /// sanitiser already neutralises means the collision suffix fires by the ordinary rule and
    /// <c>"2025.1"</c> and <c>"2025-1"</c> stay distinct, which stripping the dot afterwards would
    /// not.
    /// </para>
    /// </remarks>
    private static string TokenFor(string revitVersionNumber)
        => CacheKeySanitiser.Sanitise(revitVersionNumber.Replace('.', '/')).DirectoryName;

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
