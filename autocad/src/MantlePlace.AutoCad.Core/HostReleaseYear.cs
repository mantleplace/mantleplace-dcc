namespace MantlePlace.AutoCad;

/// <summary>
/// The release year (2025, 2026, 2027) of the AutoCAD that is running, from the version it reports.
/// </summary>
/// <remarks>
/// AutoCAD's version does not count years: 2025 is R25.0, 2026 is R25.1 and 2027 is R26.0, the
/// same series <c>PackageContents.xml</c> names for each year. A version this plugin does not
/// support gives an empty label rather than a guessed year.
/// </remarks>
public static class HostReleaseYear
{
    /// <summary>The year for AutoCAD version <paramref name="major"/>.<paramref name="minor"/>; empty when it is not a supported release.</summary>
    public static string From(int major, int minor) => (major, minor) switch
    {
        (25, 0) => "2025",
        (25, 1) => "2026",
        (26, 0) => "2027",
        _ => string.Empty,
    };
}
