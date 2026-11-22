namespace MantlePlace.Core;

public static class OdblContent
{
    public const string ContainerPrefix = "Mantle Place ODbL";

    public const string TerrainGroup = "terrain";

    public static OdblRole ParseRole(string? stated) => stated switch
    {
        "produced-work" => OdblRole.ProducedWork,
        "none" => OdblRole.None,
        _ => OdblRole.Database,
    };

    public static string RoleName(OdblRole role) => role switch
    {
        OdblRole.ProducedWork => "produced-work",
        OdblRole.None => "none",
        _ => "database",
    };

    public static OdblRole RoleOf(IReadOnlyDictionary<string, FileLicensing> entries, string path)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(path);

        return entries.TryGetValue(path, out FileLicensing? entry) ? entry.Role : OdblRole.Database;
    }

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

    public static bool IsContained(OdblRole role) => role == OdblRole.Database;

    public static string GroupOf(ImportStepKind kind) => kind switch
    {
        ImportStepKind.ToposurfaceFromPointsFile
            or ImportStepKind.ToposurfaceFromSurfaceTin
            or ImportStepKind.ToposurfaceFromSurfaceDxf => TerrainGroup,
        _ => string.Empty,
    };

    public static string ContainerName(string identity, string group)
    {
        ArgumentException.ThrowIfNullOrEmpty(identity);
        ArgumentNullException.ThrowIfNull(group);

        return group.Length == 0
            ? $"{ContainerPrefix} {identity}"
            : $"{ContainerPrefix} {identity} ({group})";
    }

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

public readonly record struct ContainerKey(string Identity, string Group);

public sealed record HandoverItem(string Identity, bool IsOdbl, string Group);

public enum HandoverDecision
{
    Keep,

    Remove,

    Swap,
}
