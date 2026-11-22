using System.Text.Json;

namespace MantlePlace.Core;

public sealed record BuildIdentity
{
    private const string DirtyMarker = "-dirty";
    private const int ShortShaLength = 7;

    private BuildIdentity(string version, string? sha, bool dirty)
    {
        Version = version;
        Sha = sha;
        Dirty = dirty;
    }

    public string Version { get; }

    public string? Sha { get; }

    public bool Dirty { get; }

    public string ShortSha => Sha is null ? "unknown" : Shorten(Sha);

    public static string Shorten(string sha)
    {
        ArgumentNullException.ThrowIfNull(sha);
        return sha.Length <= ShortShaLength ? sha : sha[..ShortShaLength];
    }

    public static BuildIdentity Parse(string informationalVersion)
    {
        ArgumentNullException.ThrowIfNull(informationalVersion);

        int plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return new BuildIdentity(informationalVersion, null, dirty: false);
        }

        string version = informationalVersion[..plus];
        string metadata = informationalVersion[(plus + 1)..];

        bool dirty = metadata.EndsWith(DirtyMarker, StringComparison.Ordinal);
        if (dirty)
        {
            metadata = metadata[..^DirtyMarker.Length];
        }

        return new BuildIdentity(version, string.IsNullOrWhiteSpace(metadata) ? null : metadata, dirty);
    }
}

public sealed record InstallStamp
{
    public const int Schema = 1;

    private InstallStamp(
        bool fromSourceTree,
        string? sha,
        bool dirty,
        string? branch,
        string? sourceTree,
        string? configuration,
        string installedAt)
    {
        FromSourceTree = fromSourceTree;
        Sha = sha;
        Dirty = dirty;
        Branch = branch;
        SourceTree = sourceTree;
        Configuration = configuration;
        InstalledAt = installedAt;
    }

    public bool FromSourceTree { get; }

    public string? Sha { get; }

    public bool Dirty { get; }

    public string? Branch { get; }

    public string? SourceTree { get; }

    public string? Configuration { get; }

    public string InstalledAt { get; }

    public static InstallStamp? Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.OptionalInt("schema") != Schema)
            {
                return null;
            }

            bool fromSourceTree;
            switch (root.Str("source"))
            {
                case "source-tree":
                    fromSourceTree = true;
                    break;
                case "release":
                    fromSourceTree = false;
                    break;
                default:
                    return null;
            }

            return new InstallStamp(
                fromSourceTree,
                root.OptionalStr("sha"),
                root.Bool("dirty"),
                root.OptionalStr("branch"),
                root.OptionalStr("sourceTree"),
                root.OptionalStr("configuration"),
                root.Str("installedAt"));
        }
    }
}

public static class InstalledBuild
{
    public static string Describe(BuildIdentity build, InstallStamp? stamp)
    {
        ArgumentNullException.ThrowIfNull(build);

        string first = build.Dirty
            ? $"Plugin version {build.Version}, commit {build.ShortSha} with uncommitted changes."
            : $"Plugin version {build.Version}, commit {build.ShortSha}.";

        return first + "\n" + Provenance(build, stamp);
    }

    private static string Provenance(BuildIdentity build, InstallStamp? stamp)
    {
        if (stamp is null)
        {
            return "No install stamp beside the plugin: installed by hand, or from a release zip.";
        }

        if (!stamp.FromSourceTree)
        {
            return $"Installed {stamp.InstalledAt} from a release zip.";
        }

        if (stamp.Sha is not null && build.Sha is not null
            && !string.Equals(stamp.Sha, build.Sha, StringComparison.OrdinalIgnoreCase))
        {
            return $"The install stamp names commit {BuildIdentity.Shorten(stamp.Sha)}, not this one: the files beside it were "
                + "replaced by hand after that deploy.";
        }

        string branch = stamp.Branch ?? "a detached head";
        string configuration = stamp.Configuration is null ? string.Empty : $" ({stamp.Configuration})";
        string tree = stamp.SourceTree is null ? string.Empty : $" at {stamp.SourceTree}";
        return $"Installed {stamp.InstalledAt} from {branch}{configuration}{tree}.";
    }
}
