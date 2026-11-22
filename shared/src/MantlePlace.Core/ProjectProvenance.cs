using System.Text.Encodings.Web;
using System.Text.Json;

namespace MantlePlace.Core;

public sealed class ProjectProvenance
{
    public required string OrderId { get; init; }

    public required string JobId { get; init; }

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

public static class ProvenanceStorage
{
    public static readonly Guid SchemaGuid = new("c077cc4a-aace-4a09-9b43-d282a6fc4f5d");

    public const string SchemaName = "MantlePlaceProvenance";

    public const string VendorId = "MNTL";

    public const string OrderIdField = "OrderId";

    public const string JobIdField = "JobId";

    public const string ManifestVersionField = "ManifestVersion";

    public const string SourcesField = "Sources";

    public const string NoteTextField = "NoteText";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string EncodeSources(IReadOnlyList<AttributionSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, WriterOptions))
        {
            writer.WriteStartArray();
            foreach (AttributionSource source in sources)
            {
                AttributionSourceJson.Write(writer, source);
            }

            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

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
                    sources.Add(AttributionSourceJson.Read(entry));
                }
            }

            return sources;
        }
        catch (JsonException)
        {
            return [];
        }
    }

}

internal static class AttributionSourceJson
{
    private const string ProviderId = "provider_id";
    private const string AttributionText = "attribution_text";
    private const string License = "license";
    private const string LicenseUrl = "license_url";

    internal static AttributionSource Read(JsonElement entry) => new(
        entry.Str(ProviderId),
        entry.OptionalStr(AttributionText),
        entry.OptionalStr(License),
        entry.OptionalStr(LicenseUrl));

    internal static void Write(Utf8JsonWriter writer, AttributionSource source)
    {
        writer.WriteStartObject();
        writer.WriteString(ProviderId, source.ProviderId);
        WriteOptional(writer, AttributionText, source.AttributionText);
        WriteOptional(writer, License, source.License);
        WriteOptional(writer, LicenseUrl, source.LicenseUrl);
        writer.WriteEndObject();
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
