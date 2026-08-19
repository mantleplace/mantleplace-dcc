using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>
/// Where one order's bundle lives on disk: the download, its in-flight <c>.part</c>, its sidecar and
/// its extracted files, all under a single directory keyed by the order id.
/// </summary>
/// <remarks>
/// <para>
/// One root per order, not one per source. The zip a curator downloaded by hand from the website and
/// the zip the vault client fetched for them are the same purchase, and before this they landed in
/// different directories keyed off different strings — the local path keyed off the zip's file name,
/// which meant two orders whose files were both called <c>download.zip</c> shared a directory, and
/// the Revit links from the first pointed at the second's extracted files.
/// </para>
/// <para>
/// The directory name comes from ⛔<see cref="CacheKeySanitiser"/> (<c>HPS-30</c>) so this host and
/// every other one derive the same path from the same order. The four file names are pinned by
/// corpus <c>cache.keySanitisation.fileNames</c> and are cross-host contract, not local convention:
/// a support instruction that says "delete <c>bundle.zip.part</c>" has to be true everywhere.
/// </para>
/// <para>
/// Nothing here evicts. <c>HPS-44</c>: a purchased bundle stays until the curator removes it, and a
/// host that reclaims disk on their behalf has converted an owned asset into a streamed one.
/// </para>
/// </remarks>
public sealed class BundleCacheLayout
{
    /// <summary>Extracted entries, which Revit links point into and which therefore outlive the import.</summary>
    public const string ExtractedDirectoryName = "extracted";

    private BundleCacheLayout(string root, SanitisedCacheKey key)
    {
        Root = root;
        Key = key;
    }

    /// <summary>The per-order directory. Every other path here hangs off it.</summary>
    public string Root { get; }

    /// <summary>The sanitisation result, so a caller can report WHY a directory is named as it is.</summary>
    public SanitisedCacheKey Key { get; }

    public string BundleZipPath => Path.Combine(Root, BundleCacheFileNames.Bundle);

    public string PartialZipPath => Path.Combine(Root, BundleCacheFileNames.Partial);

    public string SidecarPath => Path.Combine(Root, BundleCacheFileNames.Sidecar);

    public string ExtractedRoot => Path.Combine(Root, ExtractedDirectoryName);

    /// <summary>The layout for a real order — the normal case, and the one both sources converge on.</summary>
    public static BundleCacheLayout ForOrder(string orderId) => ForOrder(orderId, CacheRoot());

    /// <summary>As <see cref="ForOrder(string)"/>, with an explicit cache root. For tests.</summary>
    public static BundleCacheLayout ForOrder(string orderId, string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);
        ArgumentException.ThrowIfNullOrEmpty(cacheRoot);

        SanitisedCacheKey key = CacheKeySanitiser.Sanitise(orderId);
        return new BundleCacheLayout(Path.Combine(cacheRoot, key.DirectoryName), key);
    }

    /// <summary>
    /// The layout for a zip that names no order — a manifest too old to parse, or a file that is not
    /// a bundle at all.
    /// </summary>
    /// <remarks>
    /// Keyed off the zip's FULL path rather than its file name. The file name is the collision that
    /// started all of this: every bundle the platform emits used to be called <c>download.zip</c>.
    /// A full path is unique on the machine, and running it through the same <c>HPS-30</c> mapping
    /// means the separators are neutralised and the hash suffix is appended by the ordinary rule
    /// rather than by a special case.
    /// </remarks>
    public static BundleCacheLayout ForLooseZip(string zipPath) => ForLooseZip(zipPath, CacheRoot());

    /// <summary>As <see cref="ForLooseZip(string)"/>, with an explicit cache root. For tests.</summary>
    public static BundleCacheLayout ForLooseZip(string zipPath, string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(zipPath);
        ArgumentException.ThrowIfNullOrEmpty(cacheRoot);

        SanitisedCacheKey key = CacheKeySanitiser.Sanitise(Path.GetFullPath(zipPath));
        return new BundleCacheLayout(Path.Combine(cacheRoot, key.DirectoryName), key);
    }

    private static string CacheRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MantlePlace",
        "bundles");
}
