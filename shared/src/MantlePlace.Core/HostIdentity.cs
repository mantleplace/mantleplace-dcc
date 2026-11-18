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

    /// <summary>
    /// The newest bundle manifest major this host's import takes: what a send is checked against
    /// (<see cref="ExportBundles.CanRead"/>) and what its presence announces
    /// (<see cref="ConnectedHosts.Announce"/>). <see cref="ManifestVersions.NewestSupportedMajor"/>
    /// unless the host's shell names a newer one it imports.
    /// </summary>
    public static int NewestImportedMajor { get; private set; } = ManifestVersions.NewestSupportedMajor;

    /// <summary>Names the plugin this code is loaded by.</summary>
    /// <param name="hostId">The host id the platform contracts name, <c>revit</c> or <c>autocad</c>.</param>
    /// <param name="pluginVersion">The plugin's informational version, or <c>null</c>.</param>
    /// <param name="newestImportedMajor">
    /// The newest manifest major the host's import takes, from <see cref="ManifestVersions.NewestSupportedMajor"/>
    /// to <see cref="ManifestVersions.NewestReadMajor"/>; <c>null</c> for <see cref="ManifestVersions.NewestSupportedMajor"/>.
    /// </param>
    public static void Set(string hostId, string? pluginVersion, int? newestImportedMajor = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostId);
        int major = newestImportedMajor ?? ManifestVersions.NewestSupportedMajor;
        ArgumentOutOfRangeException.ThrowIfLessThan(major, ManifestVersions.NewestSupportedMajor, nameof(newestImportedMajor));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(major, ManifestVersions.NewestReadMajor, nameof(newestImportedMajor));
        id = hostId;
        PluginVersion = pluginVersion;
        NewestImportedMajor = major;
    }
}
