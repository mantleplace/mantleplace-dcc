namespace MantlePlace.PlatformApi;

/// <summary>
/// The <c>Mantle-Place-Client</c> header: which client is calling, and which version of it.
/// </summary>
/// <remarks>
/// The spec reads it as <c>&lt;client&gt;/&lt;semver&gt;</c> and accepts a call without it, or with one it
/// cannot read, as an unknown client. So a version this cannot read is sent as <c>unknown</c>: the
/// header is never omitted and never carries free text.
/// </remarks>
public static class PlatformClientHeader
{
    /// <summary>The header's name.</summary>
    public const string Name = "Mantle-Place-Client";

    /// <summary>The version sent when the build's own cannot be read.</summary>
    public const string UnknownVersion = "unknown";

    /// <summary>
    /// The header value for a client at a version. Build metadata (<c>+sha</c>, a dirty marker) is
    /// dropped: it names a commit, and the platform compares versions.
    /// </summary>
    /// <param name="client">The client's name in the spec's list (<c>revit</c>, <c>unreal</c>).</param>
    /// <param name="version">The build's version, as its assembly states it; may be <c>null</c>.</param>
    public static string For(string client, string? version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(client);
        return client + "/" + (Semver(version) ?? UnknownVersion);
    }

    /// <summary>The <c>major.minor.patch</c> (with any pre-release) of a version, or <c>null</c>.</summary>
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
