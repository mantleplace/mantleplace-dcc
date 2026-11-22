using MantlePlace.Core;

namespace MantlePlace.Client;

public sealed class BundleCacheLayout
{
    public const string ExtractedDirectoryName = "extracted";

    private BundleCacheLayout(string root, SanitisedCacheKey key)
    {
        Root = root;
        Key = key;
    }

    public string Root { get; }

    public SanitisedCacheKey Key { get; }

    public string BundleZipPath => Path.Combine(Root, BundleCacheFileNames.Bundle);

    public string PartialZipPath => Path.Combine(Root, BundleCacheFileNames.Partial);

    public string SidecarPath => Path.Combine(Root, BundleCacheFileNames.Sidecar);

    public string ExtractedRoot => Path.Combine(Root, ExtractedDirectoryName);

    public static BundleCacheLayout ForOrder(string filingKey) => ForOrder(filingKey, CacheRoot());

    public static BundleCacheLayout ForOrder(string filingKey, string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(filingKey);
        ArgumentException.ThrowIfNullOrEmpty(cacheRoot);

        SanitisedCacheKey key = CacheKeySanitiser.Sanitise(filingKey);
        return new BundleCacheLayout(Path.Combine(cacheRoot, key.DirectoryName), key);
    }

    public static BundleCacheLayout ForLooseZip(string zipPath) => ForLooseZip(zipPath, CacheRoot());

    public static BundleCacheLayout ForLooseZip(string zipPath, string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(zipPath);
        ArgumentException.ThrowIfNullOrEmpty(cacheRoot);

        SanitisedCacheKey key = CacheKeySanitiser.Sanitise(Path.GetFullPath(zipPath));
        return new BundleCacheLayout(Path.Combine(cacheRoot, key.DirectoryName), key);
    }

    public static string DefaultRoot => CacheRoot();

    private static string CacheRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MantlePlace",
        "bundles");
}
