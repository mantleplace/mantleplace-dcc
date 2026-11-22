namespace MantlePlace.AutoCad;

public static class HostReleaseYear
{
    public static string From(int major, int minor) => (major, minor) switch
    {
        (25, 0) => "2025",
        (25, 1) => "2026",
        (26, 0) => "2027",
        _ => string.Empty,
    };
}
