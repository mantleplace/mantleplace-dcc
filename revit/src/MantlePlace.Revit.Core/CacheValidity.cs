using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>Why a cached bundle is not usable.</summary>
public enum CacheInvalidReason
{
    /// <summary>It is usable.</summary>
    None,
    Missing,
    SizeMismatch,
    Sha256Mismatch,
    ManifestTooOld,
}

/// <summary>What the UI shows for a cache entry.</summary>
public enum CacheState
{
    NotCached,
    CachedValid,
    CachedStale,
}

/// <summary>The verdict on one cached file.</summary>
/// <param name="IsValid">Whether it can be imported.</param>
/// <param name="Reason">Why not, when it cannot.</param>
/// <param name="IntegrityChecked">
/// Whether a hash comparison was actually PERFORMED. Valid-and-unchecked is a real, common and
/// honest state; reporting it as verified is a lie and reporting it as corrupt makes every legacy
/// bundle un-openable.
/// </param>
public readonly record struct CacheVerdict(bool IsValid, CacheInvalidReason Reason, bool IntegrityChecked)
{
    public CacheState State(bool fileExists) => !fileExists
        ? CacheState.NotCached
        : IsValid ? CacheState.CachedValid : CacheState.CachedStale;
}

/// <summary>⛔<c>HPS-27</c>: is this cached bundle usable, and was it verified?</summary>
public static class CacheValidity
{
    /// <summary>
    /// Decides in the fixed precedence: missing → size → hash → manifest floor → valid.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A null expected hash means <em>unknown</em>, not <em>absent</em>.</b> When no hash was
    /// advertised, or none could be computed, the integrity check is simply not performed: the entry
    /// is valid but UNVERIFIED, and the host says which. That is the single row this whole table
    /// exists for — "unknown" and "corrupt" look identical to a naive implementation and have
    /// opposite correct responses.
    /// </para>
    /// <para>
    /// Size is checked before the hash because it is the cheapest discriminator, and it holds even
    /// when the hash legs are unknown: a file that is demonstrably the wrong length is wrong,
    /// whatever nobody knows about its digest.
    /// </para>
    /// </remarks>
    /// <param name="computedSha256">Empty when nothing was computed.</param>
    /// <param name="expectedSha256"><c>null</c> when the platform advertised none.</param>
    /// <param name="expectedSizeBytes"><c>null</c> when the platform advertised none.</param>
    /// <param name="manifestVersion"><c>null</c> when unknown — never a reason to refuse.</param>
    public static CacheVerdict Decide(
        bool fileExists,
        long onDiskSizeBytes,
        string computedSha256,
        string? expectedSha256,
        long? expectedSizeBytes,
        int? manifestVersion)
    {
        if (!fileExists)
        {
            return new CacheVerdict(false, CacheInvalidReason.Missing, IntegrityChecked: false);
        }

        if (expectedSizeBytes is { } expectedSize && expectedSize != onDiskSizeBytes)
        {
            return new CacheVerdict(false, CacheInvalidReason.SizeMismatch, IntegrityChecked: false);
        }

        // Both halves must be present for a comparison to mean anything. Either one missing is the
        // valid-but-unverified path, NOT a mismatch.
        bool comparable = !string.IsNullOrWhiteSpace(computedSha256) && !string.IsNullOrWhiteSpace(expectedSha256);
        if (comparable && !Sha256Digest.Equal(computedSha256, expectedSha256))
        {
            return new CacheVerdict(false, CacheInvalidReason.Sha256Mismatch, IntegrityChecked: true);
        }

        if (manifestVersion is { } version && version < ManifestVersions.MinSupportedManifestVersion)
        {
            return new CacheVerdict(false, CacheInvalidReason.ManifestTooOld, IntegrityChecked: comparable);
        }

        return new CacheVerdict(true, CacheInvalidReason.None, IntegrityChecked: comparable);
    }
}

/// <summary>
/// What the plugin remembers about a downloaded bundle, written beside it as <c>cache.json</c>.
/// </summary>
/// <remarks>
/// The sidecar exists because the vault listing that carried the integrity facts is not available
/// offline, and a curator opening Revit on a plane must still be told whether the bundle on their
/// disk was verified when it arrived. Every field is what the platform said at download time, not a
/// re-derivation.
/// </remarks>
public sealed class CacheSidecar
{
    /// <summary>The RAW order id, before <c>HPS-30</c> sanitisation — the directory name is lossy.</summary>
    public required string OrderId { get; init; }

    public string BundleFileName { get; init; } = BundleCacheFileNames.Bundle;

    public long SizeBytes { get; init; }

    /// <summary><c>null</c> means the platform advertised no digest, NOT that the file has none.</summary>
    public string? Sha256 { get; init; }

    public int? ManifestVersion { get; init; }

    public string DownloadedAtUtc { get; init; } = string.Empty;

    /// <summary>Whether a hash comparison was performed at download time (<c>HPS-27</c>).</summary>
    public bool IntegrityChecked { get; init; }
}

/// <summary>The four names a bundle's cache directory holds. Corpus <c>cache.keySanitisation.fileNames</c>.</summary>
public static class BundleCacheFileNames
{
    public const string Bundle = "bundle.zip";
    public const string Partial = "bundle.zip.part";
    public const string Sidecar = "cache.json";
}

/// <summary>Reading and writing <c>cache.json</c>. Pure — the file I/O is the client's.</summary>
public static class CacheSidecars
{
    public static string Serialize(CacheSidecar sidecar)
    {
        ArgumentNullException.ThrowIfNull(sidecar);

        StringBuilder json = new();
        json.Append("{\n");
        json.Append("  \"orderId\": ").Append(Quote(sidecar.OrderId)).Append(",\n");
        json.Append("  \"bundleFileName\": ").Append(Quote(sidecar.BundleFileName)).Append(",\n");
        json.Append("  \"sizeBytes\": ").Append(sidecar.SizeBytes.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        json.Append("  \"sha256\": ").Append(sidecar.Sha256 is null ? "null" : Quote(sidecar.Sha256)).Append(",\n");
        json.Append("  \"manifestVersion\": ")
            .Append(sidecar.ManifestVersion?.ToString(CultureInfo.InvariantCulture) ?? "null").Append(",\n");
        json.Append("  \"downloadedAtUtc\": ").Append(Quote(sidecar.DownloadedAtUtc)).Append(",\n");
        json.Append("  \"integrityChecked\": ").Append(sidecar.IntegrityChecked ? "true" : "false").Append('\n');
        json.Append("}\n");
        return json.ToString();
    }

    /// <summary>
    /// Reads a sidecar. <c>null</c> when it is missing, unreadable or names no order — all of which
    /// mean "nothing is remembered about this file", never "an error happened".
    /// </summary>
    public static CacheSidecar? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string orderId = root.Str("orderId");
            if (orderId.Length == 0)
            {
                return null;
            }

            return new CacheSidecar
            {
                OrderId = orderId,
                BundleFileName = root.Str("bundleFileName") is { Length: > 0 } name ? name : BundleCacheFileNames.Bundle,
                SizeBytes = (long)(root.OptionalDouble("sizeBytes") ?? 0.0),
                Sha256 = root.OptionalStr("sha256"),
                ManifestVersion = root.OptionalInt("manifestVersion"),
                DownloadedAtUtc = root.Str("downloadedAtUtc"),
                IntegrityChecked = root.Bool("integrityChecked"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);
}
