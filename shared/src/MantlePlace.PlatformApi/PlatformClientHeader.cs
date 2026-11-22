namespace MantlePlace.PlatformApi;

public static class PlatformClientHeader
{
    public const string Name = "Mantle-Place-Client";

    public const string UnknownVersion = "unknown";

    public static string For(string client, string? version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(client);
        return client + "/" + (Semver(version) ?? UnknownVersion);
    }

    private static string? Semver(string? version)
    {
        string core = (version ?? string.Empty).Trim();
        int metadata = core.IndexOf('+', StringComparison.Ordinal);
        if (metadata >= 0)
        {
            core = core[..metadata];
        }

        int prerelease = core.IndexOf('-', StringComparison.Ordinal);
        string numbers = prerelease >= 0 ? core[..prerelease] : core;
        string[] parts = numbers.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || !part.All(char.IsAsciiDigit)))
        {
            return null;
        }

        string suffix = prerelease >= 0 ? core[prerelease..] : string.Empty;
        return suffix.Length == 1 || suffix.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))
            ? null
            : core;
    }
}
