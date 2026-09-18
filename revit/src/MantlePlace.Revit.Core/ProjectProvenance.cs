using System.Text.Encodings.Web;
using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>
/// Which bundle a project was imported from: the record the import leaves on Project Information.
/// </summary>
/// <remarks>
/// What a later "which bundle is this project from" feature reads. Everything in it is the
/// manifest's own statement, copied: the vault order id (the join key, <c>HPS-37</c>), the job id
/// that says which build of that order, the manifest version, and the attribution sources.
/// </remarks>
public sealed class ProjectProvenance
{
    /// <summary>The vault order id — top-level <c>order_id</c>, never <c>attribution.order_id</c>.</summary>
    public required string OrderId { get; init; }

    /// <summary>The ETL job id, which changes on every rebuild of the order.</summary>
    public required string JobId { get; init; }

    /// <summary>The manifest's <c>version</c>, verbatim.</summary>
    public required string ManifestVersion { get; init; }

    public required IReadOnlyList<AttributionSource> Sources { get; init; }

    public static ProjectProvenance From(BundleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return new ProjectProvenance
        {
            OrderId = manifest.OrderId,
            JobId = manifest.JobId,
            ManifestVersion = manifest.Version,
            Sources = manifest.AttributionSources,
        };
    }
}

/// <summary>
/// The ExtensibleStorage schema <see cref="ProjectProvenance"/> is stored under, and the encoding of
/// the one field that is not a plain string.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>A schema is immutable once any document holds it.</b> Revit keys it by
/// <see cref="SchemaGuid"/>, and a project saved with this schema carries its field list for good:
/// adding, removing or retyping a field under the same GUID makes Revit refuse the new definition in
/// every project that already has the old one. A change to the fields is a new GUID, and a reader
/// that wants old projects reads both.
/// </para>
/// <para>
/// The sources are one JSON string rather than an array of sub-entities. It is the smallest API
/// surface that holds a list of records, and the only part of this record whose shape matters
/// beyond Revit — so its encoding lives here, where a headless test holds it to a round trip.
/// </para>
/// </remarks>
public static class ProvenanceStorage
{
    /// <summary>Revit's key for the schema. Never reused, never changed; see the remarks above.</summary>
    public static readonly Guid SchemaGuid = new("c077cc4a-aace-4a09-9b43-d282a6fc4f5d");

    public const string SchemaName = "MantlePlaceProvenance";

    /// <summary>
    /// The vendor the schema's write access is granted to. It must be the add-in manifest's own
    /// <c>VendorId</c>, which the headless suite checks, because a mismatch compiles and fails only
    /// inside Revit.
    /// </summary>
    public const string VendorId = "MNTL";

    public const string OrderIdField = "OrderId";

    public const string JobIdField = "JobId";

    public const string ManifestVersionField = "ManifestVersion";

    /// <summary>The sources, as <see cref="EncodeSources"/> writes them.</summary>
    public const string SourcesField = "Sources";

    /// <summary>Keeps <c>©</c> and <c>§</c> readable in the stored string rather than escaped.</summary>
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The sources as a JSON array, keyed exactly as the manifest keys them, so the stored record
    /// reads like the block it was copied from.
    /// </summary>
    public static string EncodeSources(IReadOnlyList<AttributionSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, WriterOptions))
        {
            writer.WriteStartArray();
            foreach (AttributionSource source in sources)
            {
                writer.WriteStartObject();
                writer.WriteString("provider_id", source.ProviderId);
                WriteOptional(writer, "attribution_text", source.AttributionText);
                WriteOptional(writer, "license", source.License);
                WriteOptional(writer, "license_url", source.LicenseUrl);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Reads back what <see cref="EncodeSources"/> wrote. Anything unreadable is no sources — the
    /// record is evidence about the past, and a reader that throws over it helps nobody.
    /// </summary>
    public static IReadOnlyList<AttributionSource> DecodeSources(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(stored);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            List<AttributionSource> sources = [];
            foreach (JsonElement entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object)
                {
                    sources.Add(new AttributionSource(
                        entry.Str("provider_id"),
                        entry.OptionalStr("attribution_text"),
                        entry.OptionalStr("license"),
                        entry.OptionalStr("license_url")));
                }
            }

            return sources;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }
}
