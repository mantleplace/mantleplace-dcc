using System.Text.Json;

namespace MantlePlace.Core;

/// <summary>
/// Reads a bundle of either major: finds its manifest, reads it by the path its location says, and
/// returns the <see cref="BundleManifest"/> the import planners consume, with
/// <see cref="BundleManifest.Contents"/> saying what was found. Pure: entry names and a way to read
/// one entry's text in, facts out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which path.</b> <c>Manifest.json</c> at the bundle's root is a 3.x bundle, read by
/// <see cref="ManifestThreeReader"/>; <c>Metadata/manifest.json</c> is a 2.x or 1.x bundle, read by
/// <see cref="BundleManifestReader"/> exactly as it always has been; a zip with neither is not a bundle
/// (<c>spec/format.md</c> §11). The root is looked at first. Both are found through
/// <see cref="BundleEntryIndex"/>, so a back-slashed entry, a different case or one wrapper folder
/// from a re-zipped download does not hide a manifest.
/// </para>
/// <para>
/// <b>The same result from both.</b> For a 2.x bundle, the contents are the catalog's names the bundle
/// holds, each in the family its own frame statement gives it (<c>absolute</c> is georeferenced,
/// <c>offset</c> host-ready), with no level and no count, which a 2.x manifest does not state. The
/// 2.x reading itself is untouched: the contents are read beside it, from the same text.
/// </para>
/// </remarks>
public static class BundleReader
{
    /// <summary>Where a 3.x bundle keeps its manifest, in every later major too (<c>spec/format.md</c> §2).</summary>
    public const string ManifestPath = "Manifest.json";

    /// <summary>Where a bundle before 3.0 keeps its manifest.</summary>
    public const string ManifestPathBeforeThree = "Metadata/manifest.json";

    /// <summary>
    /// Reads the bundle whose entries are <paramref name="entryNames"/>.
    /// </summary>
    /// <param name="entryNames">Every entry's path as the archive or folder spells it.</param>
    /// <param name="readEntry">The text of one entry, given its own spelling; <c>null</c> when it cannot be read.</param>
    /// <returns>
    /// <c>null</c> when the bundle holds neither manifest. Otherwise the reading, which may be a
    /// refusal (<see cref="BundleManifest.IsValid"/> <c>false</c>); its <see cref="BundleManifest.Contents"/>
    /// is never <c>null</c>.
    /// </returns>
    public static BundleManifest? Read(IEnumerable<string> entryNames, Func<string, string?> readEntry)
    {
        ArgumentNullException.ThrowIfNull(entryNames);
        ArgumentNullException.ThrowIfNull(readEntry);

        BundleEntryIndex index = new(entryNames);
        string? rootEntry = index.Resolve(ManifestPath);
        string? twoEntry = index.Resolve(ManifestPathBeforeThree);

        // The index retries a path under the one folder every entry shares, and matches regardless of
        // case, so a zip that holds nothing but Metadata/manifest.json resolves the root name to that
        // same entry. It is the 2.x manifest, in the 2.x place.
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

    /// <summary>What a 2.x reading found, read beside it from the same manifest text.</summary>
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

    /// <summary>A 2.x file's family, from its own frame statement: absolute or offset, or neither.</summary>
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
