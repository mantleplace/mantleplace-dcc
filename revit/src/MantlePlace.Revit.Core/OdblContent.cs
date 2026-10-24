namespace MantlePlace.Revit.Core;

/// <summary>
/// The ODbL decisions, as pure functions: which imported content is a Derivative Database, which
/// container it is filed in, and what Prepare for handover does to each piece. Pure.
/// </summary>
/// <remarks>
/// <para>
/// This host makes no licensing decision of its own. It reads the role the manifest states and
/// fails closed the way <c>spec/format.md</c> §6.8 says: a missing role reads as <c>database</c>, and
/// so does a role this reader does not know. <see cref="RevitBundleImporter"/> files what a
/// <c>database</c> step made in a container; the handover command works from that container alone, so
/// it needs no bundle and works on a project a colleague saved.
/// </para>
/// <para>
/// The container is a Revit selection filter named <see cref="ContainerName"/>. It is the one thing
/// in Revit that holds elements of any kind (a toposolid, a direct shape, a family instance, a filled
/// region) under a name a person reads, shows in Visibility/Graphics, and carries through a save. The
/// elements' own Comments stamps are not touched: ADR 0004 and its kin find an earlier import's
/// elements by exact stamp, and a marker added to one would make every re-import draw them again.
/// </para>
/// </remarks>
public static class OdblContent
{
    /// <summary>What every container's name begins with.</summary>
    public const string ContainerPrefix = "Mantle Place ODbL";

    /// <summary>The artifacts that stand for the same thing as the terrain, so a Produced Work one replaces an ODbL one.</summary>
    public const string TerrainGroup = "terrain";

    /// <summary>The role a stated value names. A missing value and any unrecognised one read as <see cref="OdblRole.Database"/>.</summary>
    public static OdblRole ParseRole(string? stated) => stated switch
    {
        "produced-work" => OdblRole.ProducedWork,
        "none" => OdblRole.None,
        _ => OdblRole.Database,
    };

    /// <summary>The role's published word: <c>database</c>, <c>produced-work</c> or <c>none</c>.</summary>
    public static string RoleName(OdblRole role) => role switch
    {
        OdblRole.ProducedWork => "produced-work",
        OdblRole.None => "none",
        _ => "database",
    };

    /// <summary>The role of one file. A file with no entry has no stated role, so it is <see cref="OdblRole.Database"/>.</summary>
    public static OdblRole RoleOf(IReadOnlyDictionary<string, FileLicensing> entries, string path)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(path);

        return entries.TryGetValue(path, out FileLicensing? entry) ? entry.Role : OdblRole.Database;
    }

    /// <summary>
    /// The role of everything made from these files together: <see cref="OdblRole.Database"/> when any
    /// of them is, else <see cref="OdblRole.ProducedWork"/> when any of them is, else
    /// <see cref="OdblRole.None"/>. No files at all is <see cref="OdblRole.None"/>: there is no file
    /// whose role could be missing.
    /// </summary>
    public static OdblRole RoleOf(IReadOnlyDictionary<string, FileLicensing> entries, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(paths);

        bool producedWork = false;
        foreach (string path in paths)
        {
            switch (RoleOf(entries, path))
            {
                case OdblRole.Database:
                    return OdblRole.Database;
                case OdblRole.ProducedWork:
                    producedWork = true;
                    break;
            }
        }

        return producedWork ? OdblRole.ProducedWork : OdblRole.None;
    }

    /// <summary>
    /// The role of what a step makes: the stricter of the file it reads and the file it falls back to,
    /// because the importer reads the fallback when the first cannot be used and the container has to
    /// be right for either. A step that reads no file of the bundle makes no content, so it is
    /// <see cref="OdblRole.None"/>.
    /// </summary>
    public static OdblRole RoleOf(IReadOnlyDictionary<string, FileLicensing> entries, ImportStep step)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(step);

        if (!ImportStepKinds.ImportsContent(step.Kind) || step.EntryName.Length == 0)
        {
            return OdblRole.None;
        }

        List<string> paths = [step.EntryName];
        if (step.GeoJsonFallback is { EntryName.Length: > 0 } fallback)
        {
            paths.Add(fallback.EntryName);
        }

        return RoleOf(entries, paths);
    }

    /// <summary>Whether content of this role is filed in the ODbL container.</summary>
    public static bool IsContained(OdblRole role) => role == OdblRole.Database;

    /// <summary>
    /// The equivalence group of what a step makes, or empty for none: steps whose output stands for the
    /// same thing share one, so a Produced Work one already in the project can replace an ODbL one.
    /// </summary>
    public static string GroupOf(ImportStepKind kind) => kind switch
    {
        ImportStepKind.ToposurfaceFromPointsFile
            or ImportStepKind.ToposurfaceFromSurfaceTin
            or ImportStepKind.ToposurfaceFromSurfaceDxf => TerrainGroup,
        _ => string.Empty,
    };

    // --- The container's name ---------------------------------------------------------------

    /// <summary>The name of the container for one order's ODbL content of one group (<paramref name="group"/> may be empty).</summary>
    public static string ContainerName(string identity, string group)
    {
        ArgumentException.ThrowIfNullOrEmpty(identity);
        ArgumentNullException.ThrowIfNull(group);

        return group.Length == 0
            ? $"{ContainerPrefix} {identity}"
            : $"{ContainerPrefix} {identity} ({group})";
    }

    /// <summary>
    /// Reads a container's name back into the order and group it was made for, or <c>null</c> for a
    /// name that is not one of ours. The identity is the order's cache-key stem, which holds no space.
    /// </summary>
    public static ContainerKey? ParseContainerName(string? name)
    {
        string head = ContainerPrefix + " ";
        if (name is null || !name.StartsWith(head, StringComparison.Ordinal))
        {
            return null;
        }

        string rest = name[head.Length..];
        string group = string.Empty;
        int open = rest.IndexOf(" (", StringComparison.Ordinal);
        if (open >= 0)
        {
            if (!rest.EndsWith(')'))
            {
                return null;
            }

            group = rest[(open + 2)..^1];
            rest = rest[..open];
        }

        return rest.Length == 0 || rest.Contains(' ') ? null : new ContainerKey(rest, group);
    }

    // --- Prepare for handover ---------------------------------------------------------------

    /// <summary>
    /// What to do with each item. An ODbL item is <see cref="HandoverDecision.Swap"/> when another
    /// item of the same order and the same non-empty equivalence group is not ODbL (the Produced Work
    /// already in the project stands in for it), and <see cref="HandoverDecision.Remove"/> otherwise.
    /// An item that is not ODbL is <see cref="HandoverDecision.Keep"/>: left exactly as it is.
    /// </summary>
    public static IReadOnlyList<HandoverDecision> PlanHandover(IReadOnlyList<HandoverItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        List<HandoverDecision> decisions = [];
        foreach (HandoverItem item in items)
        {
            if (!item.IsOdbl)
            {
                decisions.Add(HandoverDecision.Keep);
                continue;
            }

            bool standIn = item.Group.Length > 0
                && items.Any(other => !other.IsOdbl
                    && string.Equals(other.Identity, item.Identity, StringComparison.Ordinal)
                    && string.Equals(other.Group, item.Group, StringComparison.Ordinal));
            decisions.Add(standIn ? HandoverDecision.Swap : HandoverDecision.Remove);
        }

        return decisions;
    }
}

/// <summary>The order and equivalence group a container was made for.</summary>
public readonly record struct ContainerKey(string Identity, string Group);

/// <summary>One thing in the project the handover looks at: a piece of content, or a container of it.</summary>
/// <param name="Identity">The order it belongs to (its cache-key stem).</param>
/// <param name="IsOdbl">True for content filed in an ODbL container.</param>
/// <param name="Group">The equivalence group it belongs to, or empty.</param>
public sealed record HandoverItem(string Identity, bool IsOdbl, string Group);

/// <summary>What Prepare for handover does with one item.</summary>
public enum HandoverDecision
{
    /// <summary>Not ODbL content: left exactly as it is.</summary>
    Keep,

    /// <summary>ODbL content with no Produced Work equivalent in the project: removed.</summary>
    Remove,

    /// <summary>ODbL content whose group holds a Produced Work of the same order: removed, and that one stays.</summary>
    Swap,
}
