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
/// The members read are the served 3.0.0 schema's: the level, the <c>catalog</c>, each entry's
/// <c>count.value</c> and digest, and the <c>not_held</c> list of <c>{path, reason}</c>. The two
/// frames (§9.4) are read once, by <see cref="ManifestFramesReader"/>, and each file's unit follows
/// the frame its family is in; no unit stands in for one the manifest states.
/// </para>
/// </remarks>
internal static class ManifestThreeReader
{
    /// <summary>The download's one level (<c>spec/format.md</c> §9.3).</summary>
    private const string LevelMember = "level";

    /// <summary>The bundle statement's order (§9.3), and its identity: the one id that joins a bundle to its vault entry.</summary>
    private const string OrderMember = "order";

    private const string OrderIdMember = "id";

    /// <summary>A GeoPackage entry's table names (§13, R20).</summary>
    private const string TablesMember = "tables";

    /// <summary>The bundle's credits, each a line of text and a link (§9.8), and the two members of one.</summary>
    private const string CreditsMember = "credits";

    private const string CreditTextMember = "text";

    private const string CreditLinkMember = "link";

    /// <summary>The one file catalog, keyed by each file's path in the zip (§9.5).</summary>
    private const string CatalogMember = "catalog";

    /// <summary>A catalog entry's count of rows, features, triangles or pixels (§9.5).</summary>
    private const string CountMember = "count";

    /// <summary>The number inside a count, <c>{"kind", "value"}</c>.</summary>
    private const string CountValueMember = "value";

    /// <summary>A catalog entry's digest (§9.8).</summary>
    private const string DigestMember = "sha256";

    /// <summary>The promised files the download does not hold, each with its reason (§9.6).</summary>
    private const string NotHeldMember = "not_held";

    private const string ReasonMember = "reason";

    /// <summary>A not-held entry's path.</summary>
    private const string PathMember = "path";

    /// <summary>A raster entry's facts (§9.5), and the four members of it this reader reads.</summary>
    private const string RasterMember = "raster";

    private const string WidthMember = "width";

    private const string HeightMember = "height";

    private const string RasterFrameMember = "frame";

    private const string RectangleMember = "rectangle";

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

        // The two frames, stated once for every file of their family (§9.4). A unit, datum or axis
        // order this reader does not understand refuses the manifest, naming the value (§4).
        if (ManifestFramesReader.Read(root, out string? framesError) is not { } frames)
        {
            return Refuse(manifest, version.Major, BundleRefusal.Unreadable, framesError ?? "Manifest.json states no frames.");
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
                // A file is in the unit of the frame its family is in, as that frame states it (§4, §9.4).
                Unit = family switch
                {
                    FileFamily.HostReady => frames.Local.Unit.Unit,
                    FileFamily.Georeferenced => frames.Georeferenced.HorizontalUnit.Unit,
                    _ => LinearUnit.Unspecified,
                },
                Raster = entry.Value.ValueKind == JsonValueKind.Object ? Raster(entry.Value) : null,
                Tables = entry.Value.ValueKind == JsonValueKind.Object ? entry.Value.StringArray(TablesMember) : [],
            });
        }

        // A file listed as not held is shown with the reason as the manifest states it, never matched
        // on (§9.6): the reasons grow. A path the catalog also names is the catalog's.
        if (root.TryGetProperty(NotHeldMember, out JsonElement notHeld) && notHeld.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in notHeld.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.OptionalStr(PathMember) is { } path
                    && !catalogued.Contains(path))
                {
                    skipped.Add(new SkippedFile(path, SkipCause.NotHeld, entry.OptionalStr(ReasonMember)));
                }
            }
        }

        // The terms of each file, read as a 2.x catalog's are: the members did not change (§9.8).
        BundleManifestReader.ReadFileEntries(manifest, root, CatalogMember);

        // The order's identity, verbatim (§9.3): what the bundle is filed under and what every element a
        // host places is stamped with, as a 2.x order_id is. So the same order is one cache entry and one
        // set of stamps from the vault or from a zip of any name, and a re-import finds what it placed.
        manifest.OrderId = root.Object(OrderMember)?.OptionalStr(OrderIdMember) ?? string.Empty;
        manifest.FilingKey = manifest.OrderId;

        // The credits are the attribution a 2.x manifest's attribution.sources carried: each line verbatim,
        // with its link. Never a refusal, as there: a malformed line costs that line alone.
        manifest.AttributionSources = Credits(root);

        manifest.Contents = new BundleContents
        {
            Major = version.Major,
            ManifestPath = BundleReader.ManifestPath,
            Level = level,
            Frames = frames,
            Files = [.. found.OrderBy(file => file.Path, StringComparer.Ordinal)],
            Skipped = [.. skipped.OrderBy(file => file.Path, StringComparer.Ordinal)],
        };
        manifest.IsValid = true;
        return manifest;
    }

    /// <summary>The <c>credits</c> lines as attribution, each verbatim; a line with neither text nor link is left out.</summary>
    private static List<AttributionSource> Credits(JsonElement root)
    {
        List<AttributionSource> read = [];
        if (!root.TryGetProperty(CreditsMember, out JsonElement credits) || credits.ValueKind != JsonValueKind.Array)
        {
            return read;
        }

        foreach (JsonElement credit in credits.EnumerateArray())
        {
            if (credit.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? text = credit.OptionalStr(CreditTextMember);
            string? link = credit.OptionalStr(CreditLinkMember);
            if (text is not null || link is not null)
            {
                read.Add(new AttributionSource(string.Empty, text, null, link));
            }
        }

        return read;
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
        entry.TryGetProperty(CountMember, out JsonElement counted)
        && counted.ValueKind == JsonValueKind.Object
        && counted.TryGetProperty(CountValueMember, out JsonElement count)
        && count.ValueKind == JsonValueKind.Number
        && count.TryGetInt64(out long value)
        && value >= 0
            ? value
            : null;

    /// <summary>
    /// What an entry's <c>raster</c> member states (§9.5): its width and height, the frame its
    /// rectangle is in and the rectangle as four numbers, west, south, east and north. <c>null</c> when
    /// the entry is not a raster; a value it does not state is left unstated, never filled in.
    /// </summary>
    private static RasterGrid? Raster(JsonElement entry)
    {
        if (entry.Object(RasterMember) is not { } raster)
        {
            return null;
        }

        RasterRectangle? rectangle = null;
        if (raster.TryGetProperty(RectangleMember, out JsonElement edges)
            && edges.ValueKind == JsonValueKind.Array
            && edges.GetArrayLength() == 4
            && edges.EnumerateArray().All(edge => edge.ValueKind == JsonValueKind.Number))
        {
            double[] values = [.. edges.EnumerateArray().Select(edge => edge.GetDouble())];
            rectangle = new RasterRectangle(values[0], values[1], values[2], values[3]);
        }

        return new RasterGrid(
            PositiveInt(raster, WidthMember),
            PositiveInt(raster, HeightMember),
            raster.OptionalStr(RasterFrameMember),
            rectangle);
    }

    private static int PositiveInt(JsonElement element, string member) =>
        element.TryGetProperty(member, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int number)
        && number > 0
            ? number
            : 0;

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
