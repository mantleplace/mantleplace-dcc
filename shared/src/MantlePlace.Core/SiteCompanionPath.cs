namespace MantlePlace.Core;

public static class SiteCompanionPath
{
    private const string CompanionExtension = ".rvt";

    private const int DigestHexLength = 8;

    public static string ForVersion(string ifcPath, string revitVersionNumber)
    {
        ArgumentException.ThrowIfNullOrEmpty(ifcPath);
        ArgumentException.ThrowIfNullOrEmpty(revitVersionNumber);

        return Beside(ifcPath, "." + TokenFor(revitVersionNumber) + CompanionExtension);
    }

    public static bool IsCompanionOf(string ifcPath, string candidatePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(ifcPath);

        if (string.IsNullOrEmpty(candidatePath)
            || !CompanionExtension.Equals(Path.GetExtension(candidatePath), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetDirectoryName(candidatePath) ?? string.Empty,
                Path.GetDirectoryName(ifcPath) ?? string.Empty,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(ifcPath);
        string candidateStem = Path.GetFileNameWithoutExtension(candidatePath);

        if (string.Equals(candidateStem, stem, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = stem + ".";

        return candidateStem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !candidateStem[prefix.Length..].Contains('.', StringComparison.Ordinal);
    }

    private static string TokenFor(string revitVersionNumber)
    {
        string sanitised = CacheKeySanitiser.Sanitise(revitVersionNumber).DirectoryName;

        if (!sanitised.Contains('.', StringComparison.Ordinal))
        {
            return sanitised;
        }

        return sanitised.Replace('.', '_') + "_" + Sha256Digest.OfUtf8(revitVersionNumber)[..DigestHexLength];
    }

    private static string Beside(string ifcPath, string suffix)
    {
        string directory = Path.GetDirectoryName(ifcPath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(ifcPath);

        return directory.Length == 0 ? stem + suffix : Path.Combine(directory, stem + suffix);
    }
}
