namespace MantlePlace.Revit.Core;

/// <summary>
/// What a bundle states about one file's ODbL duty (<c>file_entries[&lt;path&gt;].odbl_role</c>, MPB 1.14.0).
/// </summary>
public enum OdblRole
{
    /// <summary>An ODbL Derivative Database, licensed ODbL. Also what a missing or unknown role reads as.</summary>
    Database,

    /// <summary>Rasterized, meshed, extruded or baked output, which is not ODbL-licensed.</summary>
    ProducedWork,

    /// <summary>No ODbL source contributes.</summary>
    None,
}

/// <summary>One upstream source of a file (<c>file_entries[&lt;path&gt;].sources[]</c>): its name and its single licence id.</summary>
public sealed record FileSource(string Name, string License);

/// <summary>
/// What <c>file_entries[&lt;path&gt;]</c> says about a file's terms. The frame members are not read
/// here: this host places files from its own block (<c>HPS-33</c>).
/// </summary>
public sealed class FileLicensing
{
    /// <summary>The stated role, with a missing or unknown one already read as <see cref="OdblRole.Database"/>.</summary>
    public OdblRole Role { get; init; } = OdblRole.Database;

    /// <summary>The SPDX licence expression, verbatim, or empty when the entry states none.</summary>
    public string License { get; init; } = string.Empty;

    /// <summary>The upstream sources, each with a single licence id.</summary>
    public IReadOnlyList<FileSource> Sources { get; init; } = [];
}
