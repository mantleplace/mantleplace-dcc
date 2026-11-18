using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MantlePlace.Core;

/// <summary>What one level of a 3.0 order downloads as: its size and its counts, as the vault lists them.</summary>
/// <param name="Level">Max, Med or Min.</param>
/// <param name="SizeBytes">The size of this level's download, every file, as a zip.</param>
/// <param name="Rows">Rows across the level's CSV files.</param>
/// <param name="Features">Features across the GeoPackages, the same at every level.</param>
/// <param name="Triangles">Triangles across the meshes.</param>
/// <param name="Pixels">Pixels of the imagery.</param>
public readonly record struct LevelSize(
    FidelityLevel Level,
    long SizeBytes,
    long Rows,
    long Features,
    long Triangles,
    long Pixels);

/// <summary>One Extra of a 3.0 order: a free download outside the bundle.</summary>
/// <param name="Id">The platform's id for it, which the request names.</param>
/// <param name="Name">The name a curator sees.</param>
/// <param name="SizeBytes">Its size.</param>
public readonly record struct LevelExtra(string Id, string Name, long SizeBytes);

/// <summary>
/// The levels a 3.0 order offers, as one vault listing row states them: the level the platform
/// recommends for the asking host and site, the size and counts of Max, Med and Min, and the Extras.
/// </summary>
/// <remarks>
/// A 2.x row has none (<see cref="VaultBundle.Levels"/> is <c>null</c>), and every 2.x behaviour is
/// as it was. The recommendation is the platform's to work out; this plugin preselects it and never
/// derives one.
/// </remarks>
public sealed class LevelOffer
{
    /// <summary>The level the platform recommends for this host and site.</summary>
    public required FidelityLevel Recommended { get; init; }

    /// <summary>Max, Med and Min, in that order, each with its size and counts.</summary>
    public required IReadOnlyList<LevelSize> Levels { get; init; }

    /// <summary>The Extras, each free; empty when the row listed none.</summary>
    public IReadOnlyList<LevelExtra> Extras { get; init; } = [];

    /// <summary>The size and counts of <paramref name="level"/>.</summary>
    public LevelSize SizeOf(FidelityLevel level) => Levels.First(size => size.Level == level);
}

/// <summary>
/// What a refused 3.0 level request is asked again for, when the refusal names promised files the
/// bundle does not hold (<see cref="DownloadLevels.FilesLeftAfterNotInBundle"/>).
/// </summary>
/// <param name="Remaining">The files to ask for again, in the order they were asked; never empty.</param>
/// <param name="Absent">The files the bundle does not hold, as the refusal named them; never empty.</param>
public sealed record NotInBundleRetry(IReadOnlyList<string> Remaining, IReadOnlyList<string> Absent);

/// <summary>
/// The 3.0 download: the levels a listing row offers, the level a download is cached at, and the
/// request that names this host's files and one level (platform route <c>POST …/download</c>).
/// </summary>
/// <remarks>
/// <para>
/// In 3.0 the level belongs to the download, so the curator chooses it before the bundle is
/// fetched (<see cref="LevelControl"/>). The request names the promised files this host reads, by
/// path from the bundle root, which each host declares once beside its import steps, and one level.
/// There is no file list per host on the platform: a 3.0 request never comes with a host view.
/// </para>
/// <para>
/// A 2.x order keeps its request (<see cref="HostViews.BuildRequestBody"/>) and its cache level
/// (<see cref="HostViews.EveryEntryLevel"/>), unchanged.
/// </para>
/// </remarks>
public static class DownloadLevels
{
    /// <summary>The version word a 3.0 request and listing carry.</summary>
    public const string ThreeVersion = "3.0";

    /// <summary>The listing row's member that carries the 3.0 offer.</summary>
    private const string OfferMember = "mpb3";

    /// <summary>The three levels a 3.0 download offers, most to least: the order the control lists them in.</summary>
    public static IReadOnlyList<FidelityLevel> Offered { get; } = [FidelityLevel.Max, FidelityLevel.Med, FidelityLevel.Min];

    /// <summary>The level as a 3.0 request, listing and manifest spell it: <c>Max</c>, <c>Med</c> or <c>Min</c>.</summary>
    public static string Word(FidelityLevel level) => level switch
    {
        FidelityLevel.Max => "Max",
        FidelityLevel.Med => "Med",
        FidelityLevel.Min => "Min",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Not a level a 3.0 download offers."),
    };

    /// <summary>Reads a 3.0 level word, exactly as spelled; anything else is not one.</summary>
    public static FidelityLevel? Parse(string? word) => word switch
    {
        "Max" => FidelityLevel.Max,
        "Med" => FidelityLevel.Med,
        "Min" => FidelityLevel.Min,
        _ => null,
    };

    /// <summary>
    /// The level a bundle's cache slot is for: a 2.x order's is the one it has always been; a 3.0
    /// order's is the level chosen, or the recommendation when none was (an export from the web
    /// vault names none).
    /// </summary>
    public static string SlotLevel(VaultBundle bundle, FidelityLevel? chosen = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return bundle.Levels is { } offer
            ? FidelityLevelNames.Token(chosen ?? offer.Recommended)
            : HostViews.EveryEntryLevel;
    }

    /// <summary>
    /// The 3.0 request body: the whole-bundle format, one level, and the promised files this host
    /// reads. With no files named the platform sends every file of the level, so a host always names its own.
    /// </summary>
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

    /// <summary>The status a refusal of promised files this bundle does not hold carries.</summary>
    public const int NotInBundleStatus = 404;

    /// <summary>The code a refusal of promised files this bundle does not hold carries.</summary>
    public const string FileNotInBundleCode = "file_not_in_bundle";

    /// <summary>
    /// The files a refused 3.0 level request is asked again for. A promised file the bundle does not
    /// hold (a layer the site lacks) is refused by name: HTTP 404, code <c>file_not_in_bundle</c>, the
    /// names in <c>files</c>. An absent file is not an error, so the request goes again naming only the rest.
    /// </summary>
    /// <returns>
    /// The rest (in the order asked) and the absent names; <c>null</c> for any other refusal, one naming
    /// no file or a file not asked for, or one that would leave nothing to ask for, which is then
    /// reported as it stands. The rest is always shorter than <paramref name="asked"/>, so asking again
    /// ends within the file count.
    /// </returns>
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
                // A name this request never asked for: not a refusal it can answer by asking less.
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

    /// <summary>
    /// Reads a listing row's 3.0 offer: <c>null</c> on a 2.x row, and on a row whose offer this build
    /// cannot read, which <paramref name="warning"/> then says.
    /// </summary>
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
                    extras.Add(new LevelExtra(id, extra.Str("name"), Long(extra, "sizeBytes") ?? 0));
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

    /// <summary>A count, grouped, for the control's tooltip.</summary>
    internal static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
