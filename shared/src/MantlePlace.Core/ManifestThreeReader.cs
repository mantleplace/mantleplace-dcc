using System.Globalization;
using System.Text.Json;

namespace MantlePlace.Core;

internal static class ManifestThreeReader
{
    private const string LevelMember = "level";

    private const string OrderMember = "order";

    private const string OrderIdMember = "id";

    private const string TablesMember = "tables";

    private const string TableMember = "table";

    private const string FoliageTypesMember = "foliage_types";

    private const string VocabularyMember = "vocabulary";

    private const string CreditsMember = "credits";

    private const string CreditTextMember = "text";

    private const string CreditLinkMember = "link";

    private const string CatalogMember = "catalog";

    private const string CountMember = "count";

    private const string CountValueMember = "value";

    private const string DigestMember = "sha256";

    private const string NotHeldMember = "not_held";

    private const string ReasonMember = "reason";

    private const string PathMember = "path";

    private const string RasterMember = "raster";

    private const string WidthMember = "width";

    private const string HeightMember = "height";

    private const string RasterFrameMember = "frame";

    private const string RectangleMember = "rectangle";

    private static readonly HashSet<string> Georeferenced =
        new(["tif", "gpkg", "dxf", "landxml", "ifc"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> HostReady =
        new(["glb", "usdz", "obj", "png", "csv"], StringComparer.OrdinalIgnoreCase);

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
                Unit = family switch
                {
                    FileFamily.HostReady => frames.Local.Unit.Unit,
                    FileFamily.Georeferenced => frames.Georeferenced.HorizontalUnit.Unit,
                    _ => LinearUnit.Unspecified,
                },
                Raster = entry.Value.ValueKind == JsonValueKind.Object ? Raster(entry.Value) : null,
                Tables = entry.Value.ValueKind == JsonValueKind.Object ? entry.Value.StringArray(TablesMember) : [],
                FoliageTypeVocabulary = entry.Value.ValueKind == JsonValueKind.Object ? FoliageVocabulary(entry.Value) : null,
            });
        }

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

        BundleManifestReader.ReadFileEntries(manifest, root, CatalogMember);

        manifest.OrderId = root.Object(OrderMember)?.OptionalStr(OrderIdMember) ?? string.Empty;
        manifest.FilingKey = manifest.OrderId;

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

    private static FidelityLevel? ParseLevel(string? text) => text switch
    {
        "Max" => FidelityLevel.Max,
        "Med" => FidelityLevel.Med,
        "Min" => FidelityLevel.Min,
        _ => null,
    };

    private static long? Count(JsonElement entry) =>
        entry.TryGetProperty(CountMember, out JsonElement counted)
        && counted.ValueKind == JsonValueKind.Object
        && counted.TryGetProperty(CountValueMember, out JsonElement count)
        && count.ValueKind == JsonValueKind.Number
        && count.TryGetInt64(out long value)
        && value >= 0
            ? value
            : null;

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

    private static string? FoliageVocabulary(JsonElement entry) =>
        entry.Object(TableMember)?.Object(FoliageTypesMember)?.OptionalStr(VocabularyMember);

    private static int PositiveInt(JsonElement element, string member) =>
        element.TryGetProperty(member, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int number)
        && number > 0
            ? number
            : 0;

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
