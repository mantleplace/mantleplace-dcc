using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MantlePlace.Core;

public enum CacheInvalidReason
{
    None,
    Missing,
    SizeMismatch,
    Sha256Mismatch,
    ManifestTooOld,

    NotThisSlot,
}

public enum CacheState
{
    NotCached,
    CachedValid,
    CachedStale,
}

public readonly record struct CacheVerdict(bool IsValid, CacheInvalidReason Reason, bool IntegrityChecked)
{
    public CacheState State(bool fileExists) => !fileExists || Reason == CacheInvalidReason.NotThisSlot
        ? CacheState.NotCached
        : IsValid ? CacheState.CachedValid : CacheState.CachedStale;
}

public static class CacheValidity
{
    public static CacheVerdict Decide(
        bool fileExists,
        long onDiskSizeBytes,
        string computedSha256,
        string? expectedSha256,
        long? expectedSizeBytes,
        string? manifestVersion)
    {
        if (!fileExists)
        {
            return new CacheVerdict(false, CacheInvalidReason.Missing, IntegrityChecked: false);
        }

        if (expectedSizeBytes is { } expectedSize && expectedSize != onDiskSizeBytes)
        {
            return new CacheVerdict(false, CacheInvalidReason.SizeMismatch, IntegrityChecked: false);
        }

        bool comparable = !string.IsNullOrWhiteSpace(computedSha256) && !string.IsNullOrWhiteSpace(expectedSha256);
        if (comparable && !Sha256Digest.Equal(computedSha256, expectedSha256))
        {
            return new CacheVerdict(false, CacheInvalidReason.Sha256Mismatch, IntegrityChecked: true);
        }

        if (manifestVersion is { } version
            && ManifestVersion.IsBelowFloor(version, ManifestVersions.MinSupportedManifestVersion))
        {
            return new CacheVerdict(false, CacheInvalidReason.ManifestTooOld, IntegrityChecked: comparable);
        }

        return new CacheVerdict(true, CacheInvalidReason.None, IntegrityChecked: comparable);
    }
}

public sealed class CacheSidecar
{
    public required string OrderId { get; init; }

    public string BundleFileName { get; init; } = BundleCacheFileNames.Bundle;

    public long SizeBytes { get; init; }

    public string? Sha256 { get; init; }

    public string? ManifestVersion { get; init; }

    public string DownloadedAtUtc { get; init; } = string.Empty;

    public bool IntegrityChecked { get; init; }

    public bool HoldsView { get; init; }

    public string? Host { get; init; }

    public string? Level { get; init; }

    public bool IsSlotOf(string host, string level)
        => Host is not null && Level is not null
            && string.Equals(Host, host, StringComparison.Ordinal)
            && string.Equals(Level, level, StringComparison.Ordinal);
}

public static class BundleCacheFileNames
{
    public const string Bundle = "bundle.zip";
    public const string Partial = "bundle.zip.part";
    public const string Sidecar = "cache.json";
}

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
            .Append(sidecar.ManifestVersion is null ? "null" : Quote(sidecar.ManifestVersion)).Append(",\n");
        json.Append("  \"downloadedAtUtc\": ").Append(Quote(sidecar.DownloadedAtUtc)).Append(",\n");
        json.Append("  \"integrityChecked\": ").Append(sidecar.IntegrityChecked ? "true" : "false");

        if (sidecar.Host is not null)
        {
            json.Append(",\n  \"host\": ").Append(Quote(sidecar.Host));
        }

        if (sidecar.Level is not null)
        {
            json.Append(",\n  \"level\": ").Append(Quote(sidecar.Level));
        }

        if (sidecar.HoldsView)
        {
            json.Append(",\n  \"view\": true");
        }

        json.Append('\n');
        json.Append("}\n");
        return json.ToString();
    }

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
                ManifestVersion = root.OptionalStr("manifestVersion")
                    ?? root.OptionalInt("manifestVersion")?.ToString(CultureInfo.InvariantCulture),
                DownloadedAtUtc = root.Str("downloadedAtUtc"),
                IntegrityChecked = root.Bool("integrityChecked"),
                HoldsView = root.Bool("view"),
                Host = root.OptionalStr("host"),
                Level = root.OptionalStr("level"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);
}
