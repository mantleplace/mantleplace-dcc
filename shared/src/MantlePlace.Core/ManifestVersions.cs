namespace MantlePlace.Core;

public static class ManifestVersions
{
    public const string MinSupportedManifestVersion = "1.0.0";

    public const int NewestSupportedMajor = 2;

    public const int RevitNewestImportedMajor = 3;

    public const int AutoCadNewestImportedMajor = 3;

    public const int NewestReadMajor = 3;

    internal static string NewerMajorMessage(string? version, int newestMajor) =>
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "Bundle manifest version {0} is newer than this plugin understands (it reads {1}.x to {2}.x). "
            + "Update the Mantle Place plugin to import this bundle.",
            version,
            ManifestVersion.Parse(MinSupportedManifestVersion).Major,
            newestMajor);
}

public readonly record struct ManifestVersion(bool IsValid, int Major, int Minor, int Patch)
    : IComparable<ManifestVersion>
{
    public static ManifestVersion Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return default;
        }

        string[] parts = text.Split('.');
        if (parts.Length != 3)
        {
            return default;
        }

        var components = new int[3];
        for (int i = 0; i < 3; i++)
        {
            string part = parts[i];
            if (part.Length == 0 || !part.All(char.IsAsciiDigit)
                || (part.Length > 1 && part[0] == '0')
                || !int.TryParse(part, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out components[i]))
            {
                return default;
            }
        }

        return new ManifestVersion(true, components[0], components[1], components[2]);
    }

    public static bool IsBelowFloor(string? version, string floor)
    {
        ManifestVersion floorParsed = Parse(floor);
        if (!floorParsed.IsValid)
        {
            throw new ArgumentException($"The version floor \"{floor}\" is not a MAJOR.MINOR.PATCH version.", nameof(floor));
        }

        ManifestVersion parsed = Parse(version);
        return !parsed.IsValid || parsed.CompareTo(floorParsed) < 0;
    }

    public static string Describe(string? version) =>
        string.IsNullOrEmpty(version) ? "unknown"
        : Parse(version).IsValid ? version
        : $"v{version}";

    public int CompareTo(ManifestVersion other)
    {
        int major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        int minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => IsValid ? $"{Major}.{Minor}.{Patch}" : "(none)";
}
