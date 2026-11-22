using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MantlePlace.Core;

public readonly record struct LevelSize(
    FidelityLevel Level,
    long SizeBytes,
    long Rows,
    long Features,
    long Triangles,
    long Pixels);

public readonly record struct LevelExtra(string Id, string Name, long SizeBytes, bool Locked = false);

public sealed class LevelOffer
{
    public required FidelityLevel Recommended { get; init; }

    public required IReadOnlyList<LevelSize> Levels { get; init; }

    public IReadOnlyList<LevelExtra> Extras { get; init; } = [];

    public LevelSize SizeOf(FidelityLevel level) => Levels.First(size => size.Level == level);
}

public sealed record NotInBundleRetry(IReadOnlyList<string> Remaining, IReadOnlyList<string> Absent);

public static class DownloadLevels
{
    public const string ThreeVersion = "3.0";

    private const string OfferMember = "mpb3";

    public static IReadOnlyList<FidelityLevel> Offered { get; } = [FidelityLevel.Max, FidelityLevel.Med, FidelityLevel.Min];

    public static string Word(FidelityLevel level) => level switch
    {
        FidelityLevel.Max => "Max",
        FidelityLevel.Med => "Med",
        FidelityLevel.Min => "Min",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Not a level a 3.0 download offers."),
    };

    public static FidelityLevel? Parse(string? word) => word switch
    {
        "Max" => FidelityLevel.Max,
        "Med" => FidelityLevel.Med,
        "Min" => FidelityLevel.Min,
        _ => null,
    };

    public static string SlotLevel(VaultBundle bundle, FidelityLevel? chosen = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return bundle.Levels is { } offer
            ? FidelityLevelNames.Token(chosen ?? offer.Recommended)
            : HostViews.EveryEntryLevel;
    }

    public static string BuildRequestBody(FidelityLevel level, IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer))
        {
            json.WriteStartObject();
            json.WriteString("format", PresignedDownloads.WholeBundleFormat);
            json.WriteStartObject(OfferMember);
            json.WriteString("version", ThreeVersion);
            json.WriteString("level", Word(level));
            if (files.Count > 0)
            {
                json.WriteStartArray("files");
                foreach (string file in files)
                {
                    json.WriteStringValue(file);
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public const int NotInBundleStatus = 404;

    public const string FileNotInBundleCode = "file_not_in_bundle";

    public static NotInBundleRetry? FilesLeftAfterNotInBundle(int status, string? body, IReadOnlyList<string> asked)
    {
        ArgumentNullException.ThrowIfNull(asked);
        if (status != NotInBundleStatus || string.IsNullOrEmpty(body))
        {
            return null;
        }

        List<string> absent = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("code", out JsonElement code)
                || code.ValueKind != JsonValueKind.String
                || code.GetString() != FileNotInBundleCode
                || !root.TryGetProperty("files", out JsonElement named)
                || named.ValueKind != JsonValueKind.Array
                || named.GetArrayLength() == 0)
            {
                return null;
            }

            foreach (JsonElement value in named.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String
                    || value.GetString() is not { } name
                    || !asked.Contains(name, StringComparer.Ordinal))
                {
                    return null;
                }

                if (!absent.Contains(name, StringComparer.Ordinal))
                {
                    absent.Add(name);
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        List<string> remaining = [.. asked.Where(file => !absent.Contains(file, StringComparer.Ordinal))];
        return remaining.Count == 0 ? null : new NotInBundleRetry(remaining, absent);
    }

    public static LevelOffer? ReadOffer(JsonElement row, out string? warning)
    {
        warning = null;
        if (row.Object(OfferMember) is not { } offer)
        {
            return null;
        }

        FidelityLevel? recommended = Parse(offer.OptionalStr("recommendedLevel"));
        JsonElement? levels = offer.Object("levels");
        List<LevelSize> sizes = [];
        foreach (FidelityLevel level in Offered)
        {
            if (levels?.Object(Word(level)) is { } entry && Long(entry, "sizeBytes") is { } bytes)
            {
                JsonElement? counts = entry.Object("counts");
                sizes.Add(new LevelSize(
                    level,
                    bytes,
                    counts is { } c ? Long(c, "rows") ?? 0 : 0,
                    counts is { } f ? Long(f, "features") ?? 0 : 0,
                    counts is { } t ? Long(t, "triangles") ?? 0 : 0,
                    counts is { } p ? Long(p, "pixels") ?? 0 : 0));
            }
        }

        if (recommended is null || sizes.Count != Offered.Count)
        {
            warning = "its 3.0 levels could not be read, so its level cannot be chosen";
            return null;
        }

        List<LevelExtra> extras = [];
        if (offer.Array("extras") is { } listed)
        {
            foreach (JsonElement extra in listed.EnumerateArray())
            {
                if (extra.ValueKind == JsonValueKind.Object && extra.Str("id") is { Length: > 0 } id)
                {
                    extras.Add(new LevelExtra(
                        id,
                        extra.Str("name"),
                        Long(extra, "sizeBytes") ?? 0,
                        extra.TryGetProperty("locked", out JsonElement locked) && locked.ValueKind == JsonValueKind.True));
                }
            }
        }

        return new LevelOffer { Recommended = recommended.Value, Levels = sizes, Extras = extras };
    }

    private static long? Long(JsonElement parent, string field)
        => parent.TryGetProperty(field, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long number)
            && number >= 0
                ? number
                : null;

    internal static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
