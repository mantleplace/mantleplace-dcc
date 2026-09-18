using System.IO.Compression;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>
/// Read access to a bundle zip on disk. I/O plus the manifest's own integrity check — it makes no
/// import decisions.
/// </summary>
/// <remarks>
/// <para>
/// The local-zip path is the permanent fallback: even once the vault client lands, a
/// curator who already has the file on disk must be able to import it. Revit's importers take file
/// paths rather than streams, so entries are extracted to disk first.
/// </para>
/// <para>
/// <b>Two lifetimes, and the difference is load-bearing.</b> An imported toposurface is copied into
/// the model; a linked DXF or IFC is a live reference to a path. Extracting both to one temporary
/// directory and deleting it on dispose destroys the links the import just created — silently,
/// because the failure only shows up the next time the project is opened. Which lifetime a step gets
/// is decided by <see cref="ImportStepKinds.LifetimeOf"/> in the pure core, not here and not in the
/// Revit shim.
/// </para>
/// </remarks>
public sealed class LocalBundleArchive : IDisposable
{
    private const string ManifestEntrySuffix = "Metadata/manifest.json";

    private readonly ZipArchive _archive;
    private readonly string _scratchDirectory;
    private readonly HashSet<string> _verified = new(StringComparer.Ordinal);

    private LocalBundleArchive(
        ZipArchive archive,
        BundleManifest? manifest,
        BundleCacheLayout layout,
        string scratchDirectory)
    {
        _archive = archive;
        _scratchDirectory = scratchDirectory;
        Manifest = manifest;
        Layout = layout;
        EntryNames = [.. archive.Entries.Select(entry => entry.FullName)];
    }

    /// <summary>
    /// The parsed manifest, or <c>null</c> when the zip carries no <c>Metadata/manifest.json</c> at
    /// all — which is how "this file is not a Mantle Place bundle" is distinguished from "this
    /// bundle is too old", a refusal the manifest itself explains.
    /// </summary>
    public BundleManifest? Manifest { get; }

    /// <summary>Where this bundle's files live, keyed by order id where the manifest names one.</summary>
    public BundleCacheLayout Layout { get; }

    public IReadOnlyList<string> EntryNames { get; }

    /// <summary>Where linked files live, so the UI can tell the curator what not to delete.</summary>
    public string RetainedDirectory => Layout.ExtractedRoot;

    /// <summary>Opens a bundle and resolves where its files belong.</summary>
    public static LocalBundleArchive Open(string zipPath) => Open(zipPath, cacheRoot: null);

    /// <summary>As <see cref="Open(string)"/>, with an explicit cache root. For tests.</summary>
    public static LocalBundleArchive Open(string zipPath, string? cacheRoot)
    {
        ZipArchive archive = ZipFile.OpenRead(zipPath);

        string? manifestText = ReadManifestText(archive);
        BundleManifest? manifest = manifestText is null ? null : BundleManifestReader.Parse(manifestText);

        // An order id is what makes the hand-downloaded zip and the vault download the same cache
        // entry. A bundle too old to parse still yields one where the reader could recover it
        // (HPS-37, accumulate-then-refuse); only a zip that names no order at all falls back.
        BundleCacheLayout layout = ResolveLayout(manifest, zipPath, cacheRoot);

        string scratch = Path.Combine(
            Path.GetTempPath(),
            "MantlePlace",
            layout.Key.Stem + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);

        return new LocalBundleArchive(archive, manifest, layout, scratch);
    }

    /// <summary>
    /// Verifies every artifact the plan will touch, before any of them is extracted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per-step verification alone would abort the first bad artifact, but only after the earlier
    /// steps had already created a toposolid and a link in the user's model. The reference host
    /// checks the whole set up front and creates nothing on a mismatch
    /// (<c>MantlePlaceImporterLibrary.cpp</c>'s <c>VerifyEntrySha256</c> sweep); this is the same
    /// shape (⛔<c>HPS-26</c>).
    /// </para>
    /// <para>
    /// A step whose manifest advertised no hash is SKIPPED, not failed: below v19 the Revit
    /// deliverables carried none at all, and calling those bundles corrupt makes every one of them
    /// un-importable (HPS-27).
    /// </para>
    /// </remarks>
    /// <returns><c>null</c> when everything checked out, or the reason to abort.</returns>
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

    /// <summary>
    /// The pixel dimensions of an entry that is a PNG, or <c>null</c> when it is not one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Handed to <see cref="BundleImportPlanner.Plan"/> so the drape's ground extent can be
    /// corroborated against the image's own grid while the plan is still being made — before
    /// anything is extracted, and while a refusal is still just a skipped step with a sentence
    /// attached.
    /// </para>
    /// <para>
    /// It reads <see cref="PngHeader.PrefixLength"/> bytes and stops. A drape is tens of megabytes
    /// and this runs during planning, so decoding it — or even extracting it — would trade a free
    /// refusal for an expensive one. Zip entry streams are forward-only, which is exactly enough.
    /// </para>
    /// </remarks>
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
                    // Short of a header is not a PNG, which is the same answer as the wrong magic.
                    return null;
                }

                read += got;
            }
        }
        catch (InvalidDataException)
        {
            // A corrupt deflate stream fails here rather than at extraction. Same answer: the grid
            // cannot be read, so the caller refuses to place anything against it.
            return null;
        }

        return PngHeader.TryReadSize(prefix);
    }

    /// <summary>Extracts one entry and returns its full path on disk.</summary>
    /// <remarks>
    /// The expected digest is a REQUIRED argument rather than an optional one. The hash was parsed
    /// off the manifest and then consumed by nothing at all — an overload that let a caller not
    /// mention it is how that happened, so there is no longer one.
    /// </remarks>
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

        // Mirror the entry's own sub-path rather than flattening to the leaf name: two entries can
        // share a file name across folders, and a flattened extraction would have one silently
        // overwrite the other.
        string relative = entry.FullName.Replace('\\', '/').TrimStart('/');
        string destination = Path.GetFullPath(Path.Combine(root, relative));

        // Zip-slip guard: an entry named `../../evil` must not escape the directory we created.
        string rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"bundle entry '{entryName}' resolves outside the extraction directory");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        entry.ExtractToFile(destination, overwrite: true);
        return destination;
    }

    /// <summary>
    /// Disposes the archive and removes the TRANSIENT scratch directory only. The retained
    /// directory is left alone on purpose — Revit links point into it, and <c>HPS-44</c> makes
    /// eviction explicit and per-order.
    /// </summary>
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
            // Leaving a temp directory behind is a far better outcome than throwing out of an
            // import that otherwise succeeded.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Hashes one entry's bytes and compares to the manifest's declared digest, fail-closed.
    /// </summary>
    /// <remarks>
    /// An absent or blank expected hex is a skip — that is a v18 bundle, which is valid-but-unverified
    /// rather than corrupt. The comparison itself is <see cref="Sha256Digest.Equal"/>, which treats
    /// two empties as NOT equal: "unknown equals unknown" would report a verified match on a bundle
    /// nothing checked (⛔<c>HPS-27</c>, <c>HPS-28</c>).
    /// </remarks>
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
        string? orderId = manifest?.OrderId;

        if (string.IsNullOrWhiteSpace(orderId))
        {
            return cacheRoot is null
                ? BundleCacheLayout.ForLooseZip(zipPath)
                : BundleCacheLayout.ForLooseZip(zipPath, cacheRoot);
        }

        return cacheRoot is null
            ? BundleCacheLayout.ForOrder(orderId)
            : BundleCacheLayout.ForOrder(orderId, cacheRoot);
    }

    private static string? ReadManifestText(ZipArchive archive)
    {
        ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(candidate =>
            candidate.FullName.Replace('\\', '/').EndsWith(ManifestEntrySuffix, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            return null;
        }

        using StreamReader reader = new(entry.Open());
        return reader.ReadToEnd();
    }
}
