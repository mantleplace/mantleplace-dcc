namespace MantlePlace.Core;

/// <summary>
/// The host id this plugin is to the platform, the name its sign-in, its client header and its
/// connected-host presence carry, and the plugin version the client header reports.
/// </summary>
/// <remarks>
/// <para>
/// Every .NET host plugin loads this same assembly, and the platform tells them apart by this one
/// value (the platform auth and export contracts name <c>revit</c> and <c>autocad</c>). The host's
/// shell sets it once, from a module initializer, before any of its code runs, so nothing here is
/// compiled differently for one host. Only the connection's identity moves with it: the manifest
/// reader's host block is a separate fact.
/// </para>
/// <para>
/// Read before a host has set it, it throws rather than guessing: a plugin that signed in as the
/// wrong host would be a second copy of that host to the platform.
/// </para>
/// </remarks>
public static class HostIdentity
{
    private static string? id;

    /// <summary>The host id of the plugin that set it.</summary>
    /// <exception cref="InvalidOperationException">No host has called <see cref="Set"/>.</exception>
    public static string Id => id ?? throw new InvalidOperationException(
        "No host plugin has set HostIdentity: the plugin's shell calls HostIdentity.Set before anything reaches the platform.");

    /// <summary>
    /// The plugin's informational version, as its own assembly carries it (<c>1.1.0+&lt;sha&gt;</c>);
    /// <c>null</c> when the host has none to give.
    /// </summary>
    public static string? PluginVersion { get; private set; }

    private static int? newestImportedMajor;

    /// <summary>
    /// The newest bundle-manifest MAJOR the plugin that set it imports: what its presence announces as
    /// <c>reads.maxMajor</c>, and what a bundle sent from the web vault is held to
    /// (<see cref="ExportBundles.CanRead"/>), so the web greys out exactly what the host would refuse.
    /// </summary>
    /// <exception cref="InvalidOperationException">No host has called <see cref="Set"/>.</exception>
    public static int NewestImportedMajor => newestImportedMajor ?? throw new InvalidOperationException(
        "No host plugin has set HostIdentity: the plugin's shell calls HostIdentity.Set before anything reaches the platform.");

    /// <summary>Names the plugin this code is loaded by.</summary>
    /// <param name="hostId">The host id the platform contracts name, <c>revit</c> or <c>autocad</c>.</param>
    /// <param name="pluginVersion">The plugin's informational version, or <c>null</c>.</param>
    /// <param name="newestImportedMajor">
    /// The newest manifest major the host imports (<see cref="ManifestVersions.RevitNewestImportedMajor"/>,
    /// <see cref="ManifestVersions.AutoCadNewestImportedMajor"/>); never below the floor's major.
    /// </param>
    public static void Set(string hostId, string? pluginVersion, int newestImportedMajor)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostId);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            newestImportedMajor, ManifestVersion.Parse(ManifestVersions.MinSupportedManifestVersion).Major);
        id = hostId;
        PluginVersion = pluginVersion;
        HostIdentity.newestImportedMajor = newestImportedMajor;
    }
}
