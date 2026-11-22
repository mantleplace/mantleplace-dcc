namespace MantlePlace.Core;

public static class HostIdentity
{
    private static string? id;

    public static string Id => id ?? throw new InvalidOperationException(
        "No host plugin has set HostIdentity: the plugin's shell calls HostIdentity.Set before anything reaches the platform.");

    public static string? PluginVersion { get; private set; }

    private static int? newestImportedMajor;

    public static int NewestImportedMajor => newestImportedMajor ?? throw new InvalidOperationException(
        "No host plugin has set HostIdentity: the plugin's shell calls HostIdentity.Set before anything reaches the platform.");

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
