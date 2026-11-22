using System.IO.Compression;
using MantlePlace.Core;

namespace MantlePlace.Client;

public sealed class LocalBundleArchive : IDisposable
{
    private const string ManifestEntrySuffix = "Metadata/manifest.json";

    private readonly ZipArchive _archive;
    private readonly string _scratchDirectory;
    private readonly HashSet<string> _verified = new(StringComparer.Ordinal);

    private LocalBundleArchive(
        ZipArchive archive,
        string? manifestText,
        BundleManifest? manifest,
        BundleCacheLayout layout,
        string scratchDirectory)
    {
        _archive = archive;
        _scratchDirectory = scratchDirectory;
        ManifestText = manifestText;
        Manifest = manifest;
        Layout = layout;
        EntryNames = [.. archive.Entries.Select(entry => entry.FullName)];
    }

    public BundleManifest? Manifest { get; }

    public string? ManifestText { get; }

    public BundleCacheLayout Layout { get; }

    public IReadOnlyList<string> EntryNames { get; }

    public string RetainedDirectory => Layout.ExtractedRoot;

    public static LocalBundleArchive Open(string zipPath) => Open(zipPath, cacheRoot: null);

    public static LocalBundleArchive Open(string zipPath, string? cacheRoot)
    {
        ZipArchive archive = ZipFile.OpenRead(zipPath);

        string? manifestText = ReadManifestText(archive);
        BundleManifest? manifest = BundleReader.Read(archive.Entries.Select(entry => entry.FullName), name => ReadEntryText(archive, name));

        BundleCacheLayout layout = ResolveLayout(manifest, zipPath, cacheRoot);

        string scratch = Path.Combine(
            Path.GetTempPath(),
            "MantlePlace",
            layout.Key.Stem + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);

        return new LocalBundleArchive(archive, manifestText, manifest, layout, scratch);
    }

    public string? VerifyPlan(BundleImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        foreach (ImportStep step in plan.Steps)
        {
            if (step.EntryName.Length == 0)
            {
                continue;
            }

            if (Verify(step.EntryName, step.ExpectedSha256) is { } failure)
            {
                return failure;
            }
        }

        return null;
    }

    public ImageSize? ProbeImageSize(string entryName)
    {
        if (string.IsNullOrEmpty(entryName) || _archive.GetEntry(entryName) is not { } entry)
        {
            return null;
        }

        Span<byte> prefix = stackalloc byte[PngHeader.PrefixLength];

        try
        {
            using Stream stream = entry.Open();

            int read = 0;
            while (read < prefix.Length)
            {
                int got = stream.Read(prefix[read..]);
                if (got <= 0)
                {
                    return null;
                }

                read += got;
            }
        }
        catch (InvalidDataException)
        {
            return null;
        }

        return PngHeader.TryReadSize(prefix);
    }

    public string Extract(string entryName, ExtractionLifetime lifetime, string? expectedSha256)
    {
        if (Verify(entryName, expectedSha256) is { } failure)
        {
            throw new InvalidOperationException(failure);
        }

        ZipArchiveEntry entry = _archive.GetEntry(entryName)
            ?? throw new InvalidOperationException($"bundle entry '{entryName}' vanished between planning and import");

        string root = lifetime == ExtractionLifetime.Retained ? Layout.ExtractedRoot : _scratchDirectory;
        Directory.CreateDirectory(root);

        string relative = entry.FullName.Replace('\\', '/').TrimStart('/');
        string destination = Path.GetFullPath(Path.Combine(root, relative));

        string rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"bundle entry '{entryName}' resolves outside the extraction directory");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (lifetime == ExtractionLifetime.Retained && !string.IsNullOrWhiteSpace(expectedSha256) && IsAlready(destination, expectedSha256))
        {
            return destination;
        }

        entry.ExtractToFile(destination, overwrite: true);
        return destination;
    }

    private static bool IsAlready(string path, string sha256)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using FileStream existing = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Sha256Digest.Equal(Sha256Digest.OfStream(existing), sha256);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _archive.Dispose();
        try
        {
            if (Directory.Exists(_scratchDirectory))
            {
                Directory.Delete(_scratchDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string? Verify(string entryName, string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256) || _verified.Contains(entryName))
        {
            return null;
        }

        ZipArchiveEntry? entry = _archive.GetEntry(entryName);
        if (entry is null)
        {
            return $"Integrity check could not read bundle entry '{entryName}'.";
        }

        string actual;
        using (Stream bytes = entry.Open())
        {
            actual = Sha256Digest.OfStream(bytes);
        }

        if (!Sha256Digest.Equal(actual, expectedSha256))
        {
            return $"Integrity check failed for '{entryName}': the manifest declares sha256 "
                + $"{expectedSha256} but the bundle's bytes hash to {actual}. Nothing was imported. "
                + "Re-download this bundle from your vault at mantle.place/vault.";
        }

        _verified.Add(entryName);
        return null;
    }

    private static BundleCacheLayout ResolveLayout(BundleManifest? manifest, string zipPath, string? cacheRoot)
    {
        string? filingKey = manifest?.FilingKey;

        if (string.IsNullOrWhiteSpace(filingKey))
        {
            return cacheRoot is null
                ? BundleCacheLayout.ForLooseZip(zipPath)
                : BundleCacheLayout.ForLooseZip(zipPath, cacheRoot);
        }

        return cacheRoot is null
            ? BundleCacheLayout.ForOrder(filingKey)
            : BundleCacheLayout.ForOrder(filingKey, cacheRoot);
    }

    public static string ManifestSha256(string zipPath)
    {
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            if (FindEitherManifest(archive) is not { } entry)
            {
                return string.Empty;
            }

            using Stream stream = entry.Open();
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    public BundleManifest? ReadBundle() => BundleReader.Read(EntryNames, name => ReadEntryText(_archive, name));

    private static string? ReadEntryText(ZipArchive archive, string entryName)
    {
        if (archive.GetEntry(entryName) is not { } entry)
        {
            return null;
        }

        using StreamReader reader = new(entry.Open());
        return reader.ReadToEnd();
    }

    private static ZipArchiveEntry? FindEitherManifest(ZipArchive archive)
    {
        BundleEntryIndex index = new(archive.Entries.Select(entry => entry.FullName));
        string? root = index.Resolve(BundleReader.ManifestPath);
        string? beforeThree = index.Resolve(BundleReader.ManifestPathBeforeThree);
        string? name = root is not null && !string.Equals(root, beforeThree, StringComparison.Ordinal) ? root : beforeThree;
        return name is null ? FindManifest(archive) : archive.GetEntry(name);
    }

    private static ZipArchiveEntry? FindManifest(ZipArchive archive)
        => archive.Entries.FirstOrDefault(candidate =>
            candidate.FullName.Replace('\\', '/').EndsWith(ManifestEntrySuffix, StringComparison.OrdinalIgnoreCase));

    private static string? ReadManifestText(ZipArchive archive)
    {
        ZipArchiveEntry? entry = FindManifest(archive);

        if (entry is null)
        {
            return null;
        }

        using StreamReader reader = new(entry.Open());
        return reader.ReadToEnd();
    }
}
