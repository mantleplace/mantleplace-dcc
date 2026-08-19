using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>Where a bundle stands, as an open vocabulary (<c>HPS-22</c>).</summary>
public enum BundleStatus
{
    /// <summary>Not a parse error — the platform added a word this build has not met.</summary>
    Unknown,
    Available,
    RefreshPending,
    Refunded,
    Failed,
}

/// <summary>Which layer families a listing row advertises. Only meaningful when known.</summary>
public readonly record struct BundleLayers(bool Imagery, bool Basemap, bool Elevation);

/// <summary>One downloadable format and its recorded size.</summary>
/// <param name="Format">Format token as the platform spells it.</param>
/// <param name="ByteSize">
/// <c>0</c> means UNRECORDED, not an empty file. A host that hides zero-size formats hides half the
/// download menu.
/// </param>
public readonly record struct BundleDownloadFormat(string Format, long ByteSize);

/// <summary>
/// One row of the vault listing.
/// </summary>
/// <remarks>
/// ⛔<c>HPS-20</c>: every optional integrity fact is nullable, and <c>null</c> means <em>unknown</em>
/// — never <em>absent</em> and never <em>zero</em>. A host that coerces a null
/// <see cref="SizeBytes"/> to <c>0</c> later compares a real 134 MB download against an expected
/// size of zero and declares a mismatch on a bundle it never knew the size of.
/// </remarks>
public sealed class VaultBundle
{
    public required string OrderId { get; init; }

    public string AoiLabel { get; init; } = string.Empty;

    public string CreatedAt { get; init; } = string.Empty;

    public double? AreaKm2 { get; init; }

    public BundleStatus Status { get; init; }

    /// <summary><c>null</c> when the row said nothing — not "all false".</summary>
    public BundleLayers? Layers { get; init; }

    public int? ManifestVersion { get; init; }

    public long? SizeBytes { get; init; }

    public string? Sha256 { get; init; }

    public IReadOnlyList<string> Formats { get; init; } = [];

    public IReadOnlyList<BundleDownloadFormat> DownloadFormats { get; init; } = [];

    /// <summary>Whether this row can be downloaded right now.</summary>
    public bool IsDownloadable => Status == BundleStatus.Available;
}

/// <summary>A parsed listing, plus what was skipped getting there.</summary>
public sealed class VaultListing
{
    public IReadOnlyList<VaultBundle> Bundles { get; init; } = [];

    /// <summary>
    /// One line per malformed row. Surfaced rather than swallowed: ⛔<c>HPS-21</c> keeps the vault
    /// usable when one row is odd, and a silent skip would hide platform corruption indefinitely.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Reading a vault listing response.</summary>
public static class VaultListingReader
{
    /// <summary>
    /// Parses a listing body.
    /// </summary>
    /// <remarks>
    /// ⛔<c>HPS-21</c>, and the distinction is the whole rule: a row that is not an object, or that
    /// lacks a non-empty <c>id</c>, is skipped with a warning and the call SUCCEEDS. A missing or
    /// non-array top-level <c>bundles</c> is a contract violation and fails closed. Collapsing the
    /// two either hides corruption or tells a paying curator their vault is empty.
    /// </remarks>
    /// <returns><c>null</c> on success, or the message to show.</returns>
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

            // An explicit platform error outranks the shape check: "your token expired" is more
            // useful than "this is not a vault listing", and both are true.
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

                parsed.Add(ReadBundle(row, orderId));
                index++;
            }

            listing = new VaultListing { Bundles = parsed, Warnings = warnings };
            return null;
        }
    }

    /// <summary>
    /// <c>HPS-22</c>: an unrecognised status word is <see cref="BundleStatus.Unknown"/>, never a
    /// parse error. Matching is case-insensitive.
    /// </summary>
    /// <remarks>
    /// A host that switches on exact strings silently drops every bundle the day the platform adds
    /// a synonym — and it drops them from a paying curator's vault, which is the worst possible
    /// place to be strict.
    /// </remarks>
    public static BundleStatus ParseStatus(string? status) => (status ?? string.Empty).Trim() switch
    {
        var word when word.Equals("available", StringComparison.OrdinalIgnoreCase) => BundleStatus.Available,
        var word when word.Equals("refresh-pending", StringComparison.OrdinalIgnoreCase) => BundleStatus.RefreshPending,
        var word when word.Equals("refunded", StringComparison.OrdinalIgnoreCase) => BundleStatus.Refunded,
        var word when word.Equals("failed", StringComparison.OrdinalIgnoreCase) => BundleStatus.Failed,
        _ => BundleStatus.Unknown,
    };

    private static VaultBundle ReadBundle(JsonElement row, string orderId) => new()
    {
        OrderId = orderId,
        AoiLabel = row.Str("aoiLabel"),
        CreatedAt = row.Str("createdAt"),
        AreaKm2 = row.OptionalDouble("areaKm2"),
        Status = ParseStatus(row.Str("status")),
        Layers = ReadLayers(row),
        ManifestVersion = row.OptionalInt("manifestVersion"),
        SizeBytes = ReadOptionalLong(row, "sizeBytes"),
        Sha256 = row.OptionalStr("sha256"),
        Formats = ReadStrings(row, "formats"),
        DownloadFormats = ReadDownloadFormats(row),
    };

    /// <summary>
    /// <c>null</c> when the row's <c>layers</c> is absent or null — nothing is known, and no flag
    /// may be read. Reading "the object is present" as "all three true" passes a laxer check and is
    /// wrong.
    /// </summary>
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

    /// <summary>
    /// A file size outgrows <see cref="int"/> at 2 GB, and bundles do.
    /// </summary>
    private static long? ReadOptionalLong(JsonElement parent, string field)
        => parent.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? (long)value.GetDouble()
            : null;
}
