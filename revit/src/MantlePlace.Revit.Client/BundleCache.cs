using System.Globalization;
using System.Net.Http;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>What the cache knows about one order.</summary>
/// <param name="Layout">Where its files are.</param>
/// <param name="Verdict">Whether the bundle on disk is usable, and whether it was verified.</param>
/// <param name="Sidecar">What was recorded at download time, or <c>null</c>.</param>
public readonly record struct CacheEntry(BundleCacheLayout Layout, CacheVerdict Verdict, CacheSidecar? Sidecar)
{
    public CacheState State => Verdict.State(File.Exists(Layout.BundleZipPath));

    /// <summary>
    /// A sentence for the panel. Unverified is never phrased as a problem — it is the normal state
    /// of every bundle whose Revit artifacts the ETL published no digest for.
    /// </summary>
    public string Describe() => State switch
    {
        CacheState.NotCached => "Not downloaded.",
        CacheState.CachedValid when Verdict.IntegrityChecked => "Downloaded and verified.",
        CacheState.CachedValid => "Downloaded. The platform published no checksum for this bundle, so it "
            + "could not be verified — that is expected, not a problem.",
        _ => Verdict.Reason switch
        {
            CacheInvalidReason.SizeMismatch => "The downloaded file is the wrong size. Download it again.",
            CacheInvalidReason.Sha256Mismatch => "The downloaded file does not match its checksum. Download it again.",
            CacheInvalidReason.ManifestTooOld => "This bundle predates the format this plugin reads. "
                + "Re-download it from your vault to get a current one.",
            _ => "Not downloaded.",
        },
    };
}

/// <summary>
/// The on-disk bundle cache: ⛔<c>HPS-26</c> promote-by-rename, <c>HPS-27</c> validity,
/// <c>HPS-44</c> explicit eviction.
/// </summary>
/// <remarks>
/// In <c>MantlePlace.Revit.Client</c> and not in the Revit shim precisely so this can be tested. The
/// promote-by-rename path is a ⛔ rule whose second enforcer is <c>automation-test</c>, and a shim
/// that CI cannot build can only ever satisfy the first.
/// </remarks>
public sealed class BundleCache
{
    private readonly string? _root;

    /// <summary>The cache under <c>%LOCALAPPDATA%</c>.</summary>
    public BundleCache() => _root = null;

    /// <summary>As the default constructor, with an explicit root. For tests.</summary>
    public BundleCache(string root) => _root = root;

    public BundleCacheLayout LayoutFor(string orderId)
        => _root is null ? BundleCacheLayout.ForOrder(orderId) : BundleCacheLayout.ForOrder(orderId, _root);

    /// <summary>
    /// Inspects what is on disk for an order, hashing the file when one is there.
    /// </summary>
    /// <remarks>
    /// <b>No size cap on hashing.</b> The reference host skips files above ~2 GB and reports them
    /// valid-but-unverified; this host does not, because reading a 2 GB file at disk speed costs a
    /// few seconds once per import and the alternative is telling a curator their largest and most
    /// expensive purchase is the one nobody checked. The uncomputed path still exists in
    /// <see cref="CacheValidity"/> — it is reachable from a legacy sidecar — it is just not something
    /// this host chooses.
    /// </remarks>
    public CacheEntry Inspect(string orderId, long? expectedSizeBytes, string? expectedSha256, string? manifestVersion)
    {
        BundleCacheLayout layout = LayoutFor(orderId);
        CacheSidecar? sidecar = ReadSidecar(layout);

        FileInfo bundle = new(layout.BundleZipPath);
        if (!bundle.Exists)
        {
            return new CacheEntry(
                layout,
                CacheValidity.Decide(false, 0, string.Empty, expectedSha256, expectedSizeBytes, manifestVersion),
                sidecar);
        }

        // Fall back to what the sidecar recorded when the caller has no live listing — the offline
        // case, where the facts the vault would have supplied are not available.
        string? expectedHash = expectedSha256 ?? sidecar?.Sha256;
        long? expectedSize = expectedSizeBytes ?? sidecar?.SizeBytes;
        string? expectedVersion = manifestVersion ?? sidecar?.ManifestVersion;

        string computed = ComputeSha256(layout.BundleZipPath);

        return new CacheEntry(
            layout,
            CacheValidity.Decide(true, bundle.Length, computed, expectedHash, expectedSize, expectedVersion),
            sidecar);
    }

    /// <summary>
    /// ⛔<c>HPS-26</c>: streams to <c>.part</c>, verifies, and only then renames over the final path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rename is what makes the cache crash-safe. A host that streams onto the final path leaves
    /// a truncated bundle that looks cached, and the next run imports it.
    /// </para>
    /// <para>
    /// A failed verification deletes the <c>.part</c>. So does a cancellation, and a cancellation
    /// fires no completion — the caller already knows it cancelled, and a completion event would
    /// race the UI back into a "done" state.
    /// </para>
    /// </remarks>
    /// <returns><c>null</c> on success; the entry is then valid and the sidecar written.</returns>
    public async Task<string?> PromoteAsync(
        string orderId,
        Func<Stream, CancellationToken, Task> writeBody,
        long? expectedSizeBytes,
        string? expectedSha256,
        string? manifestVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeBody);

        BundleCacheLayout layout = LayoutFor(orderId);
        Directory.CreateDirectory(layout.Root);

        string partial = layout.PartialZipPath;

        // A .part left by a killed process is dead weight, never a resume point: nothing recorded
        // how many of its bytes are good.
        TryDelete(partial);

        try
        {
            using (FileStream file = new(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await writeBody(file, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(partial);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            TryDelete(partial);
            return $"The download did not finish: {ex.Message}";
        }

        long size = new FileInfo(partial).Length;
        string computed = ComputeSha256(partial);

        CacheVerdict verdict = CacheValidity.Decide(
            true,
            size,
            computed,
            expectedSha256,
            expectedSizeBytes,
            manifestVersion);

        if (!verdict.IsValid)
        {
            TryDelete(partial);
            return verdict.Reason switch
            {
                CacheInvalidReason.SizeMismatch =>
                    "The download finished at the wrong size and was discarded. Try again.",
                CacheInvalidReason.Sha256Mismatch =>
                    "The download did not match its checksum and was discarded. Try again.",
                _ => "The download could not be verified and was discarded. Try again.",
            };
        }

        // Only now. File.Move with overwrite is atomic enough for this: the rename either happened
        // or it did not, and a reader never sees a half-written bundle.zip.
        File.Move(partial, layout.BundleZipPath, overwrite: true);

        WriteSidecar(layout, new CacheSidecar
        {
            OrderId = orderId,
            SizeBytes = size,
            Sha256 = string.IsNullOrEmpty(expectedSha256) ? null : expectedSha256,
            ManifestVersion = manifestVersion,
            DownloadedAtUtc = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            IntegrityChecked = verdict.IntegrityChecked,
        });

        return null;
    }

    /// <summary>
    /// <c>HPS-44</c>: removes one order's cache, and only when asked.
    /// </summary>
    /// <remarks>
    /// There is no size-based or LRU eviction anywhere in this class. That is the anti-streaming
    /// guarantee and a product commitment: a host that "helpfully" reclaims disk has silently
    /// converted an owned asset into a streamed one.
    /// </remarks>
    public void Remove(string orderId)
    {
        BundleCacheLayout layout = LayoutFor(orderId);
        try
        {
            if (Directory.Exists(layout.Root))
            {
                Directory.Delete(layout.Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file held open by Revit's own link resolution is the likely cause, and the curator
            // asked to free disk rather than to see a dialog.
        }
    }

    private static CacheSidecar? ReadSidecar(BundleCacheLayout layout)
    {
        try
        {
            return File.Exists(layout.SidecarPath)
                ? CacheSidecars.TryParse(File.ReadAllText(layout.SidecarPath))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteSidecar(BundleCacheLayout layout, CacheSidecar sidecar)
    {
        try
        {
            File.WriteAllText(layout.SidecarPath, CacheSidecars.Serialize(sidecar));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The bundle is already promoted and importable. Losing the sidecar costs the panel its
            // "verified" badge on a later run, which is not worth failing a completed download over.
        }
    }

    private static string ComputeSha256(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Sha256Digest.OfStream(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Empty means "nothing computed", which HPS-27 reads as valid-but-unverified rather than
            // as corrupt.
            return string.Empty;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
