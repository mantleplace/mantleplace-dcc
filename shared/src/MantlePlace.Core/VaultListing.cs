using System.Globalization;
using System.Text.Json;

namespace MantlePlace.Core;

public enum BundleStatus
{
    Unknown,
    Available,
    RefreshPending,
    Refunded,
    Failed,
}

public readonly record struct BundleLayers(bool Imagery, bool Basemap, bool Elevation);

public readonly record struct BundleDownloadFormat(string Format, long ByteSize);

public sealed class VaultBundle
{
    public required string OrderId { get; init; }

    public string AoiLabel { get; init; } = string.Empty;

    public string CreatedAt { get; init; } = string.Empty;

    public double? AreaKm2 { get; init; }

    public BundleStatus Status { get; init; }

    public BundleLayers? Layers { get; init; }

    public string? ManifestVersion { get; init; }

    public long? SizeBytes { get; init; }

    public string? Sha256 { get; init; }

    public IReadOnlyList<string> Formats { get; init; } = [];

    public IReadOnlyList<BundleDownloadFormat> DownloadFormats { get; init; } = [];

    public CuratorExtent? Extent { get; init; }

    public LevelOffer? Levels { get; init; }

    public bool IsDownloadable => Status == BundleStatus.Available;
}

public sealed class VaultListing
{
    public IReadOnlyList<VaultBundle> Bundles { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public static class VaultListingReader
{
    public static string? TryParse(string body, out VaultListing listing)
    {
        listing = new VaultListing();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body ?? string.Empty);
        }
        catch (JsonException)
        {
            return "The vault listing response was not valid JSON.";
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return "The vault listing response was not valid JSON.";
            }

            if (PlatformErrors.TryRead(root, out PlatformError error))
            {
                return error.Message;
            }

            if (!root.TryGetProperty("bundles", out JsonElement bundles) || bundles.ValueKind != JsonValueKind.Array)
            {
                return "The vault listing response carried no `bundles` array, so it is not a vault listing.";
            }

            List<VaultBundle> parsed = [];
            List<string> warnings = [];
            int index = 0;

            foreach (JsonElement row in bundles.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    warnings.Add($"vault row {index} is not an object and was skipped");
                    index++;
                    continue;
                }

                string orderId = row.Str("id");
                if (orderId.Length == 0)
                {
                    warnings.Add($"vault row {index} has no id and was skipped");
                    index++;
                    continue;
                }

                parsed.Add(ReadBundle(row, orderId, out string? levelWarning));
                if (levelWarning is not null)
                {
                    warnings.Add($"vault row {index} ({orderId}): {levelWarning}");
                }

                index++;
            }

            listing = new VaultListing { Bundles = parsed, Warnings = warnings };
            return null;
        }
    }

    public static BundleStatus ParseStatus(string? status) => (status ?? string.Empty).Trim() switch
    {
        var word when word.Equals("available", StringComparison.OrdinalIgnoreCase) => BundleStatus.Available,
        var word when word.Equals("refresh-pending", StringComparison.OrdinalIgnoreCase) => BundleStatus.RefreshPending,
        var word when word.Equals("refunded", StringComparison.OrdinalIgnoreCase) => BundleStatus.Refunded,
        var word when word.Equals("failed", StringComparison.OrdinalIgnoreCase) => BundleStatus.Failed,
        _ => BundleStatus.Unknown,
    };

    private static VaultBundle ReadBundle(JsonElement row, string orderId, out string? levelWarning) => new()
    {
        Levels = DownloadLevels.ReadOffer(row, out levelWarning),
        OrderId = orderId,
        AoiLabel = row.Str("aoiLabel"),
        CreatedAt = row.Str("createdAt"),
        AreaKm2 = row.OptionalDouble("areaKm2"),
        Status = ParseStatus(row.Str("status")),
        Layers = ReadLayers(row),
        ManifestVersion = row.OptionalStr("manifestVersion")
            ?? row.OptionalInt("manifestVersion")?.ToString(CultureInfo.InvariantCulture),
        SizeBytes = ReadOptionalLong(row, "sizeBytes"),
        Sha256 = row.OptionalStr("sha256"),
        Formats = ReadStrings(row, "formats"),
        DownloadFormats = ReadDownloadFormats(row),
        Extent = ReadExtent(row),
    };

    private static CuratorExtent? ReadExtent(JsonElement row)
        => row.Object("extent") is { } extent
            && extent.OptionalDouble("west") is { } west
            && extent.OptionalDouble("south") is { } south
            && extent.OptionalDouble("east") is { } east
            && extent.OptionalDouble("north") is { } north
                ? new CuratorExtent(west, south, east, north)
                : null;

    private static BundleLayers? ReadLayers(JsonElement row)
        => row.Object("layers") is { } layers
            ? new BundleLayers(layers.Bool("imagery"), layers.Bool("basemap"), layers.Bool("elevation"))
            : null;

    private static IReadOnlyList<string> ReadStrings(JsonElement row, string field)
    {
        if (row.Array(field) is not { } array)
        {
            return [];
        }

        List<string> values = [];
        foreach (JsonElement element in array.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static IReadOnlyList<BundleDownloadFormat> ReadDownloadFormats(JsonElement row)
    {
        if (row.Object("download") is not { } download || download.Array("formats") is not { } formats)
        {
            return [];
        }

        List<BundleDownloadFormat> parsed = [];
        foreach (JsonElement element in formats.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string format = element.Str("format");
            if (format.Length == 0)
            {
                continue;
            }

            parsed.Add(new BundleDownloadFormat(format, ReadOptionalLong(element, "byteSize") ?? 0L));
        }

        return parsed;
    }

    private static long? ReadOptionalLong(JsonElement parent, string field)
        => parent.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? (long)value.GetDouble()
            : null;
}
