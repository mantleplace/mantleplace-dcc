using System.Text.Json;

namespace MantlePlace.Core;

public static class BundleReader
{
    public const string ManifestPath = "Manifest.json";

    public const string ManifestPathBeforeThree = "Metadata/manifest.json";

    public static BundleManifest? Read(IEnumerable<string> entryNames, Func<string, string?> readEntry)
    {
        ArgumentNullException.ThrowIfNull(entryNames);
        ArgumentNullException.ThrowIfNull(readEntry);

        BundleEntryIndex index = new(entryNames);
        string? rootEntry = index.Resolve(ManifestPath);
        string? twoEntry = index.Resolve(ManifestPathBeforeThree);

        if (rootEntry is not null && !string.Equals(rootEntry, twoEntry, StringComparison.Ordinal))
        {
            return ManifestThreeReader.Read(readEntry(rootEntry) ?? string.Empty, index);
        }

        if (twoEntry is not null)
        {
            string text = readEntry(twoEntry) ?? string.Empty;
            BundleManifest manifest = BundleManifestReader.Parse(text);
            manifest.Contents = ContentsBeforeThree(manifest, text, index);
            return manifest;
        }

        return null;
    }

    private static BundleContents ContentsBeforeThree(BundleManifest manifest, string text, BundleEntryIndex index)
    {
        ManifestVersion version = ManifestVersion.Parse(manifest.Version);
        int major = version.IsValid ? version.Major : 0;

        if (!manifest.IsValid)
        {
            return new BundleContents
            {
                Major = major,
                ManifestPath = ManifestPathBeforeThree,
                Refusal = major > ManifestVersions.NewestSupportedMajor
                    ? BundleRefusal.MajorAhead
                    : BundleRefusal.Unreadable,
            };
        }

        List<BundleFile> found = [];
        List<SkippedFile> skipped = [];
        using (JsonDocument document = JsonDocument.Parse(text))
        {
            if (document.RootElement.Object("file_entries") is { } catalog)
            {
                foreach (JsonProperty entry in catalog.EnumerateObject())
                {
                    if (index.Resolve(entry.Name) is null)
                    {
                        skipped.Add(new SkippedFile(entry.Name, SkipCause.FileMissing, null));
                    }
                    else
                    {
                        found.Add(new BundleFile(
                            entry.Name,
                            FamilyBeforeThree(entry.Value),
                            null,
                            entry.Value.ValueKind == JsonValueKind.Object ? entry.Value.OptionalStr("sha256") : null));
                    }
                }
            }
        }

        return new BundleContents
        {
            Major = major,
            ManifestPath = ManifestPathBeforeThree,
            Files = [.. found.OrderBy(file => file.Path, StringComparer.Ordinal)],
            Skipped = [.. skipped.OrderBy(file => file.Path, StringComparer.Ordinal)],
        };
    }

    private static FileFamily FamilyBeforeThree(JsonElement entry) =>
        entry.ValueKind == JsonValueKind.Object && entry.Object("frame") is { } frame
            ? frame.OptionalStr("coordinate_mode") switch
            {
                "absolute" => FileFamily.Georeferenced,
                "offset" => FileFamily.HostReady,
                _ => FileFamily.None,
            }
            : FileFamily.None;
}
