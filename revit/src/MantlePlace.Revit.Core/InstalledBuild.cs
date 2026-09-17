using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>
/// The loaded assembly's informational version, split into the parts a human asks about: the
/// release-shaped version, the commit it was built from, and whether that tree had uncommitted
/// changes.
/// </summary>
/// <remarks>
/// <para>
/// The SDK appends the source commit to the informational version as semver build metadata, and
/// the build appends <c>-dirty</c> after it when <c>revit/</c> had uncommitted changes
/// (<c>Directory.Build.props</c>). So the string reads <c>0.1.0+&lt;sha&gt;</c> or
/// <c>0.1.0+&lt;sha&gt;-dirty</c>, and this is the one reader of that shape. The version alone is
/// what the release tag and asset name use; every dev build between two releases shares it, which
/// is exactly why the rest of the string exists.
/// </para>
/// <para>
/// Parsing never throws. A string with no metadata is a version with an unknown commit, and the
/// short form says <c>unknown</c> rather than slicing a sha that is not there.
/// </para>
/// </remarks>
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

    /// <summary>The version before any <c>+</c>: what the release tag and asset name carry.</summary>
    public string Version { get; }

    /// <summary>The full source commit, or <c>null</c> when the build carried none.</summary>
    public string? Sha { get; }

    /// <summary>Whether <c>revit/</c> had uncommitted changes when the assembly was built.</summary>
    public bool Dirty { get; }

    /// <summary>The first seven characters of <see cref="Sha"/>, or <c>unknown</c>.</summary>
    public string ShortSha => Sha is null ? "unknown" : Shorten(Sha);

    /// <summary>The seven-character form a human reads out; a shorter input is already that form.</summary>
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

/// <summary>
/// What the deploy script wrote beside the manifest when it installed the add-in:
/// <c>MantlePlace.install.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// The assembly knows which commit it was built from. It cannot know which branch or which
/// worktree that commit was checked out in, or when it was copied into Revit's add-ins folder —
/// and those are the questions a maintainer with several worktrees and one add-ins folder actually
/// has. The deploy script knows all of them at the moment it copies, so it writes them down.
/// </para>
/// <para>
/// Two sources. A <c>source-tree</c> stamp is a maintainer deploy and names the branch and tree. A
/// <c>release</c> stamp is a curator installing an extracted zip through the same script, and has
/// no branch to name: a release has no tree to drift from, which is the reason hand-copying a
/// release is fine (ADR 0005) and hand-copying a build is not.
/// </para>
/// <para>
/// Parsing never throws: a missing, empty or unreadable stamp is <c>null</c>, and About says so.
/// A stamp is a claim about the files beside it, and <see cref="InstalledBuild.Describe"/> checks
/// that claim against the assembly before repeating it.
/// </para>
/// </remarks>
public sealed record InstallStamp
{
    /// <summary>The one schema this build reads. Bumped when a field changes meaning, not when one is added.</summary>
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

    /// <summary><c>true</c> for a maintainer deploy from a checkout; <c>false</c> for a release zip.</summary>
    public bool FromSourceTree { get; }

    /// <summary>The commit the deploy script saw, for checking against the assembly's own.</summary>
    public string? Sha { get; }

    /// <summary>Whether <c>revit/</c> had uncommitted changes at deploy time.</summary>
    public bool Dirty { get; }

    /// <summary>The branch the source tree was on, or <c>null</c> for a release or a detached head.</summary>
    public string? Branch { get; }

    /// <summary>The worktree the deploy ran from, so two worktrees on one branch still tell apart.</summary>
    public string? SourceTree { get; }

    /// <summary><c>Debug</c> or <c>Release</c>, as built; <c>null</c> for a release zip.</summary>
    public string? Configuration { get; }

    /// <summary>When the copy happened, as the script wrote it. Kept as text: it is read out, not computed on.</summary>
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

/// <summary>
/// The sentences About shows about the loaded build: what the assembly says of itself, then what
/// the deploy stamp says about how it got there, checked against each other.
/// </summary>
/// <remarks>
/// The assembly is the authority, because it is the thing that is loaded. The stamp is a claim
/// about the files beside it, and those files can be replaced by hand after the stamp was written —
/// which is the one way the two disagree, and it is reported as exactly that rather than trusted
/// or hidden.
/// </remarks>
public static class InstalledBuild
{
    public static string Describe(BuildIdentity build, InstallStamp? stamp)
    {
        ArgumentNullException.ThrowIfNull(build);

        string first = build.Dirty
            ? $"Add-in version {build.Version}, commit {build.ShortSha} with uncommitted changes."
            : $"Add-in version {build.Version}, commit {build.ShortSha}.";

        return first + "\n" + Provenance(build, stamp);
    }

    private static string Provenance(BuildIdentity build, InstallStamp? stamp)
    {
        if (stamp is null)
        {
            return "No install stamp beside the add-in: installed by hand, or from a release zip.";
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
