namespace MantlePlace.Revit.Core;

/// <summary>
/// The Revit host's bundle-manifest version floor — and the single place it is written down.
/// </summary>
/// <remarks>
/// <para>
/// Clean break (HPS-31): this host supports exactly one manifest version and refuses everything
/// below it. There is no fallback ladder and no dual-parsing; an old bundle is re-procured from the
/// vault, not re-interpreted here.
/// </para>
/// <para>
/// MPB 1.0.0 re-baselined the contract onto semantic versioning: <c>version</c> is the STRING
/// "1.0.0" where it used to be the integer 19, keys went snake_case, and everything host-specific
/// moved under <c>hosts.&lt;hostId&gt;</c>. The integer era (v7–v19) is pre-history — still
/// published, never read here. Crossing an era makes the clean break stricter rather than looser:
/// an integer-versioned bundle is not merely old, it is written in a dialect this reader does not
/// speak at all.
/// </para>
/// <para>
/// ONE HOME FOR THE NUMBER. The manifest reader's version gate, the conformance suite's
/// cross-check, and <c>tools/manifest-conformance/verified-against.json</c> all resolve to this
/// constant, and the supported MAJOR line is derived from it rather than written twice. The gate
/// reads it by regexing this file — the <c>revit.floorSource</c> entry declares the path and the
/// pattern (HPS-39), so renaming the constant or moving this file means editing that entry in the
/// same commit. The Unreal counterpart is <c>MantlePlaceMinSupportedManifestVersion</c> in
/// <c>unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceVaultTypes.h</c>.
/// </para>
/// <para>
/// Raising it is a three-move change: bump the version here, teach the corpus the new accept shape,
/// and refresh the <c>revit</c> entry's <c>evidence</c> prose in <c>verified-against.json</c>.
/// </para>
/// </remarks>
public static class ManifestVersions
{
    /// <summary>The oldest bundle-manifest version this plugin will import.</summary>
    public const string MinSupportedManifestVersion = "1.0.0";
}

/// <summary>
/// A parsed MPB manifest version.
/// </summary>
/// <remarks>
/// <see cref="IsValid"/> is false for everything that is not <c>MAJOR.MINOR.PATCH</c> — an absent
/// version, an integer from the pre-history, a partial "1.0", a pre-release tag. Those are all
/// refusals, and the reader must never coerce them into a number: the integer era read an absent
/// version as 0 and refused it, and letting a string fall to 0 the same way would be an accident
/// that happens to work rather than a decision.
/// </remarks>
public readonly record struct ManifestVersion(bool IsValid, int Major, int Minor, int Patch)
    : IComparable<ManifestVersion>
{
    /// <summary>Parse <c>MAJOR.MINOR.PATCH</c>. Never throws; anything else is <c>IsValid</c> false.</summary>
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
            // Digits only, and parsed with the invariant culture. `int.TryParse` would otherwise
            // accept a leading sign, surrounding whitespace, and culture-specific group separators,
            // none of which belong in a semver component — "1.-0.0" parsing as valid would be a
            // silent wrong answer rather than a refusal.
            //
            // A leading zero is rejected too ("01.0.0"), because semver forbids it and the
            // platform's own publisher and gate do. A component that parsed here but not there
            // would be a version this host imports and the contract does not admit exists.
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

    /// <summary>
    /// Is <paramref name="version"/> below <paramref name="floor"/>, across BOTH version families?
    /// </summary>
    /// <remarks>
    /// The whole integer pre-history sorts below the whole semver era, so anything that is not a
    /// semver string — an integer-era "19", an absent value, a malformed one — is below a semver
    /// floor. That single rule is why callers never have to know which era a stored version came
    /// from, which matters most at the cache, where a sidecar written before the re-baseline sits
    /// on disk beside one written after it.
    /// </remarks>
    public static bool IsBelowFloor(string? version, string floor)
    {
        ManifestVersion floorParsed = Parse(floor);
        if (!floorParsed.IsValid)
        {
            // An unparseable floor is a programming error in our own constant, not bundle data.
            // Refuse nothing rather than refuse everything: a cache that invalidated every entry
            // over a typo would be a far worse failure than one that stopped enforcing a rule it
            // can no longer read.
            return false;
        }

        ManifestVersion parsed = Parse(version);
        return !parsed.IsValid || parsed.CompareTo(floorParsed) < 0;
    }

    /// <summary>
    /// A manifest version as it should READ to a user: "1.0.0" for the MPB era, "v19" for the
    /// pre-history. Display only — nothing gates on this.
    /// </summary>
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
