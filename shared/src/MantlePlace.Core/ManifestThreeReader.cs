using System.Globalization;
using System.Text.Json;

namespace MantlePlace.Core;

/// <summary>
/// Reads a 3.x <c>Manifest.json</c> into the <see cref="BundleManifest"/> every host already consumes.
/// Pure: text and the bundle's entry names in, facts out.
/// </summary>
/// <remarks>
/// <para>
/// A 3.x manifest has one file catalog and no block for any one host (<c>spec/format.md</c> §9.5,
/// §9.7). Each file is found by its name in the catalog, and a name the catalog lacks is a file this
/// download does not hold, not an error. Nothing here reads a per-host block, and a test holds this
/// source to that.
/// </para>
/// <para>
/// The members read are the ones the corpus's fixtures state until the 3.x schema is served
/// (<c>tools/manifest-conformance/corpus/README.md</c>, "The reader group"): the level, the catalog,
/// each entry's count and digest, and the not-held list. The schema's names replace them here.
/// </para>
/// </remarks>
internal static class ManifestThreeReader
{
    /// <summary>The download's one level (<c>spec/format.md</c> §9.3).</summary>
    private const string LevelMember = "level";

    /// <summary>The one file catalog, keyed by each file's path in the zip (§9.5).</summary>
    private const string CatalogMember = "file_entries";

    /// <summary>A catalog entry's count of rows, features, triangles or pixels (§9.5).</summary>
    private const string CountMember = "count";

    /// <summary>A catalog entry's digest (§9.8).</summary>
    private const string DigestMember = "sha256";

    /// <summary>The promised files the download does not hold, each with its reason (§9.6).</summary>
    private const string NotHeldMember = "not_held";

    private const string ReasonMember = "reason";

    /// <summary>The extensions of the georeferenced family (§4).</summary>
    private static readonly HashSet<string> Georeferenced =
        new(["tif", "gpkg", "dxf", "landxml", "ifc"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The extensions of the host-ready family (§4).</summary>
    private static readonly HashSet<string> HostReady =
        new(["glb", "usdz", "obj", "png", "csv"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads <paramref name="jsonText"/>, found at the bundle's root, against the entries
    /// <paramref name="entries"/> holds. Never throws; a refusal is <see cref="BundleManifest.IsValid"/>
    /// <c>false</c> with <see cref="BundleManifest.Error"/> saying why.
    /// </summary>
    internal static BundleManifest Read(string jsonText, BundleEntryIndex entries)
    {
        BundleManifest manifest = new();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(jsonText ?? string.Empty);
        }
        catch (JsonException)
        {
            return Refuse(manifest, 0, BundleRefusal.Unreadable, "Manifest.json is not valid JSON.");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Refuse(manifest, 0, BundleRefusal.Unreadable, "Manifest.json is not valid JSON.");
            }

            return ReadRoot(manifest, root, entries);
        }
    }

    private static BundleManifest ReadRoot(BundleManifest manifest, JsonElement root, BundleEntryIndex entries)
    {
        // The version first, and nothing else until it is known to be one this reader reads: a later
        // major may have changed what every other member means (spec/format.md §14).
        manifest.Version = root.Str("version");
        ManifestVersion version = ManifestVersion.Parse(manifest.Version);
        if (!version.IsValid)
        {
            return Refuse(
                manifest,
                0,
                BundleRefusal.Unreadable,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Manifest.json states the version {0}, which is not a version this plugin can read.",
                    string.IsNullOrEmpty(manifest.Version) ? "(absent)" : manifest.Version));
        }

        if (version.Major > ManifestVersions.NewestReadMajor)
        {
            return Refuse(
                manifest,
                version.Major,
                BundleRefusal.MajorAhead,
                ManifestVersions.NewerMajorMessage(manifest.Version, ManifestVersions.NewestReadMajor));
        }

        if (version.Major < 3)
        {
            return Refuse(
                manifest,
                version.Major,
                BundleRefusal.Unreadable,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Manifest.json at the bundle's root states version {0}, but a bundle before 3.0 keeps its "
                    + "manifest at Metadata/manifest.json. This zip is not a bundle this plugin can read.",
                    manifest.Version));
        }

        // Fail closed on a level this reader does not know, naming it (spec/format.md §4): the level
        // says what every host-ready file was built at.
        string? levelText = root.OptionalStr(LevelMember);
        if (ParseLevel(levelText) is not { } level)
        {
            return Refuse(
                manifest,
                version.Major,
                BundleRefusal.Unreadable,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Manifest.json states the level {0}, which is not Max, Med or Min.",
                    levelText ?? "(absent)"));
        }

        if (root.Object(CatalogMember) is not { } catalog)
        {
            return Refuse(
                manifest,
                version.Major,
                BundleRefusal.Unreadable,
                "Manifest.json has no file catalog, so it names no file to read.");
        }

        List<BundleFile> found = [];
        List<SkippedFile> skipped = [];
        HashSet<string> catalogued = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty entry in catalog.EnumerateObject())
        {
            if (!IsPlainPath(entry.Name))
            {
                return Refuse(
                    manifest,
                    version.Major,
                    BundleRefusal.Unreadable,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Manifest.json names the file {0}, which is not a path inside the bundle.",
                        entry.Name));
            }

            catalogued.Add(entry.Name);
            if (entries.Resolve(entry.Name) is null)
            {
                skipped.Add(new SkippedFile(entry.Name, SkipCause.FileMissing, null));
                continue;
            }

            FileFamily family = FamilyOf(entry.Name);
            found.Add(new BundleFile(
                entry.Name,
                family,
                entry.Value.ValueKind == JsonValueKind.Object ? Count(entry.Value) : null,
                entry.Value.ValueKind == JsonValueKind.Object ? entry.Value.OptionalStr(DigestMember) : null)
            {
                // The local site frame is in metres on every order; the order's units are the
                // georeferenced frame's, stated once for the family (spec/format.md §4).
                Unit = family == FileFamily.HostReady ? LinearUnit.Metre : LinearUnit.Unspecified,
            });
        }

        // A file listed as not held is shown with the reason as the manifest states it, never matched
        // on (§9.6): the reasons grow. A path the catalog also names is the catalog's.
        if (root.Object(NotHeldMember) is { } notHeld)
        {
            foreach (JsonProperty entry in notHeld.EnumerateObject())
            {
                if (!catalogued.Contains(entry.Name))
                {
                    string? reason = entry.Value.ValueKind == JsonValueKind.Object
                        ? entry.Value.OptionalStr(ReasonMember)
                        : null;
                    skipped.Add(new SkippedFile(entry.Name, SkipCause.NotHeld, reason));
                }
            }
        }

        // The terms of each file, read as a 2.x catalog's are: the members did not change (§9.8).
        BundleManifestReader.ReadFileEntries(manifest, root);

        manifest.Contents = new BundleContents
        {
            Major = version.Major,
            ManifestPath = BundleReader.ManifestPath,
            Level = level,
            Files = [.. found.OrderBy(file => file.Path, StringComparer.Ordinal)],
            Skipped = [.. skipped.OrderBy(file => file.Path, StringComparer.Ordinal)],
        };
        manifest.IsValid = true;
        return manifest;
    }

    /// <summary>The family a file's extension states (§4), matched regardless of case.</summary>
    internal static FileFamily FamilyOf(string path)
    {
        string name = path[(path.LastIndexOf('/') + 1)..];
        int dot = name.LastIndexOf('.');
        if (dot < 0)
        {
            return FileFamily.None;
        }

        string extension = name[(dot + 1)..];
        return Georeferenced.Contains(extension) ? FileFamily.Georeferenced
            : HostReady.Contains(extension) ? FileFamily.HostReady
            : FileFamily.None;
    }

    /// <summary>The level as a 3.x manifest spells it, exactly: <c>Max</c>, <c>Med</c> or <c>Min</c>.</summary>
    private static FidelityLevel? ParseLevel(string? text) => text switch
    {
        "Max" => FidelityLevel.Max,
        "Med" => FidelityLevel.Med,
        "Min" => FidelityLevel.Min,
        _ => null,
    };

    /// <summary>The count an entry states, as a non-negative integer; <c>null</c> when it states none.</summary>
    private static long? Count(JsonElement entry) =>
        entry.TryGetProperty(CountMember, out JsonElement count)
        && count.ValueKind == JsonValueKind.Number
        && count.TryGetInt64(out long value)
        && value >= 0
            ? value
            : null;

    /// <summary>
    /// A relative path that stays inside the bundle: no leading slash, no drive, no back-slash and no
    /// <c>..</c> segment (<c>spec/format.md</c> §10).
    /// </summary>
    private static bool IsPlainPath(string path) =>
        path.Length > 0
        && !path.StartsWith('/')
        && !path.Contains('\\', StringComparison.Ordinal)
        && !path.Contains(':', StringComparison.Ordinal)
        && !path.Split('/').Any(segment => segment is ".." or "." or "");

    private static BundleManifest Refuse(BundleManifest manifest, int major, BundleRefusal refusal, string error)
    {
        manifest.Contents = new BundleContents
        {
            Major = major,
            ManifestPath = BundleReader.ManifestPath,
            Refusal = refusal,
        };
        manifest.IsValid = false;
        manifest.Error = error;
        return manifest;
    }
}
