using System.Globalization;
using System.Net.Http;
using MantlePlace.Core;

namespace MantlePlace.Client;

public readonly record struct CacheEntry(BundleCacheLayout Layout, CacheVerdict Verdict, CacheSidecar? Sidecar)
{
    public CacheState State => Verdict.State(File.Exists(Layout.BundleZipPath));

    public string Describe() => State switch
    {
        CacheState.NotCached => "Not downloaded.",
        CacheState.CachedValid when Verdict.IntegrityChecked => "Downloaded and verified.",
        CacheState.CachedValid => "Downloaded. Your vault publishes no checksum for the zip as a whole, "
            + "so the file itself was not verified — the files inside it are checked against the "
            + "manifest's own checksums when you import.",
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

public sealed class BundleCache
{
    private readonly string? _root;
    private readonly string _host;

    public BundleCache()
    {
        _root = null;
        _host = HostIdentity.Id;
    }

    public BundleCache(string root)
    {
        _root = root;
        _host = HostIdentity.Id;
    }

    public BundleCache(string root, string host)
    {
        _root = root;
        _host = host;
    }

    public string Host => _host;

    public const string HighestLevel = HostViews.EveryEntryLevel;

    public BundleCacheLayout LayoutFor(string orderId)
        => _root is null ? BundleCacheLayout.ForOrder(orderId) : BundleCacheLayout.ForOrder(orderId, _root);

    public CacheEntry Inspect(
        string orderId,
        long? expectedSizeBytes,
        string? expectedSha256,
        string? manifestVersion,
        string level = HighestLevel)
        => Inspect(orderId, expectedSizeBytes, expectedSha256, manifestVersion, level, hashTheFile: true);

    public CacheEntry InspectQuick(
        string orderId,
        long? expectedSizeBytes,
        string? expectedSha256,
        string? manifestVersion,
        string level = HighestLevel)
        => Inspect(orderId, expectedSizeBytes, expectedSha256, manifestVersion, level, hashTheFile: false);

    private CacheEntry Inspect(
        string orderId,
        long? expectedSizeBytes,
        string? expectedSha256,
        string? manifestVersion,
        string level,
        bool hashTheFile)
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

        if (sidecar is null || !sidecar.IsSlotOf(_host, level))
        {
            return new CacheEntry(
                layout,
                new CacheVerdict(false, CacheInvalidReason.NotThisSlot, IntegrityChecked: false),
                sidecar);
        }

        bool holdsView = sidecar?.HoldsView == true;
        string? expectedHash = holdsView ? sidecar!.Sha256 : expectedSha256 ?? sidecar?.Sha256;
        long? expectedSize = holdsView ? sidecar!.SizeBytes : expectedSizeBytes ?? sidecar?.SizeBytes;
        string? expectedVersion = manifestVersion ?? sidecar?.ManifestVersion;

        string computed = hashTheFile ? ComputeSha256(layout.BundleZipPath) : string.Empty;

        return new CacheEntry(
            layout,
            CacheValidity.Decide(true, bundle.Length, computed, expectedHash, expectedSize, expectedVersion),
            sidecar);
    }

    public async Task<string?> PromoteAsync(
        string orderId,
        Func<Stream, CancellationToken, Task> writeBody,
        long? expectedSizeBytes,
        string? expectedSha256,
        string? manifestVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        bool holdsView = false,
        string? expectedManifestSha256 = null,
        string level = HighestLevel)
    {
        ArgumentNullException.ThrowIfNull(writeBody);

        BundleCacheLayout layout = LayoutFor(orderId);
        Directory.CreateDirectory(layout.Root);

        string partial = layout.PartialZipPath;

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

        if (!string.IsNullOrWhiteSpace(expectedManifestSha256)
            && !Sha256Digest.Equal(LocalBundleArchive.ManifestSha256(partial), expectedManifestSha256))
        {
            TryDelete(partial);
            return "The download's manifest did not match its checksum and was discarded. Try again.";
        }

        File.Move(partial, layout.BundleZipPath, overwrite: true);

        WriteSidecar(layout, new CacheSidecar
        {
            OrderId = orderId,
            SizeBytes = size,
            Sha256 = string.IsNullOrEmpty(expectedSha256) ? null : expectedSha256,
            ManifestVersion = manifestVersion,
            DownloadedAtUtc = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            IntegrityChecked = verdict.IntegrityChecked,
            HoldsView = holdsView,
            Host = _host,
            Level = level,
        });

        return null;
    }

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
