using System.Text.Json;

namespace MantlePlace.Core;

public enum ExportStatus
{
    Sent,
    Received,
    WaitingForBundle,
    AwaitingOptions,
    Importing,
    Imported,
    Failed,
    Cancelled,
}

public static class ExportStatuses
{
    public static string ToWire(ExportStatus status) => status switch
    {
        ExportStatus.Sent => "sent",
        ExportStatus.Received => "received",
        ExportStatus.WaitingForBundle => "waiting_for_bundle",
        ExportStatus.AwaitingOptions => "awaiting_options",
        ExportStatus.Importing => "importing",
        ExportStatus.Imported => "imported",
        ExportStatus.Failed => "failed",
        ExportStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static bool TryParse(string? wire, out ExportStatus status)
    {
        foreach (ExportStatus candidate in Enum.GetValues<ExportStatus>())
        {
            if (string.Equals(ToWire(candidate), wire, StringComparison.Ordinal))
            {
                status = candidate;
                return true;
            }
        }

        status = default;
        return false;
    }

    public static bool IsTerminal(ExportStatus status)
        => status is ExportStatus.Imported or ExportStatus.Failed or ExportStatus.Cancelled;
}

public readonly record struct HostExport(
    string Id,
    string OrderId,
    string Host,
    string TargetConnectionId,
    string? TargetDocument,
    string? TargetMachine,
    string? TargetPluginVersion,
    ExportStatus Status,
    int? Progress,
    string? FailureReason,
    string? FailureMessage,
    string CreatedAt,
    string UpdatedAt,
    string? EndedAt)
{
    public bool IsFor(string connectionId)
        => connectionId.Length > 0
           && string.Equals(TargetConnectionId, connectionId, StringComparison.Ordinal)
           && string.Equals(Host, ConnectedHosts.HostId, StringComparison.Ordinal);
}

public static class HostExports
{
    public static string? TryParse(string json, out HostExport export)
    {
        export = default;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json ?? string.Empty);
        }
        catch (JsonException)
        {
            return "The export was not valid JSON.";
        }

        using (document)
        {
            return TryRead(document.RootElement, out export);
        }
    }

    public static string? TryParseList(string body, out IReadOnlyList<HostExport> exports)
    {
        exports = [];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body ?? string.Empty);
        }
        catch (JsonException)
        {
            return "The export list was not valid JSON.";
        }

        using (document)
        {
            if (document.RootElement.Array("exports") is not { } rows)
            {
                return "The export list named no exports.";
            }

            List<HostExport> read = [];
            foreach (JsonElement row in rows.EnumerateArray())
            {
                if (TryRead(row, out HostExport export) is null)
                {
                    read.Add(export);
                }
            }

            exports = read;
            return null;
        }
    }

    public static IReadOnlyList<HostExport> ForConnection(IEnumerable<HostExport> exports, string connectionId)
    {
        ArgumentNullException.ThrowIfNull(exports);
        return [.. exports.Where(export => export.IsFor(connectionId))];
    }

    private static string? TryRead(JsonElement root, out HostExport export)
    {
        export = default;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return "The export was not an object.";
        }

        string id = Either(root, "id", "id");
        if (id.Length == 0)
        {
            return "The export named no id.";
        }

        string wireStatus = Either(root, "status", "status");
        if (!ExportStatuses.TryParse(wireStatus, out ExportStatus status))
        {
            return $"The export status '{wireStatus}' is not one this plugin's protocol defines.";
        }

        JsonElement? target = root.Object("target");

        export = new HostExport(
            id,
            Either(root, "orderId", "order_id"),
            Either(root, "host", "host"),
            Either(root, "targetConnectionId", "target_connection_id"),
            OptionalEither(root, target, "document", "target_document"),
            OptionalEither(root, target, "machine", "target_machine"),
            OptionalEither(root, target, "pluginVersion", "target_plugin_version"),
            status,
            root.OptionalInt("progress"),
            Optional(Either(root, "failureReason", "failure_reason")),
            Optional(Either(root, "failureMessage", "failure_message")),
            Either(root, "createdAt", "created_at"),
            Either(root, "updatedAt", "updated_at"),
            Optional(Either(root, "endedAt", "ended_at")));
        return null;
    }

    private static string Either(JsonElement root, string camel, string snake)
    {
        string value = root.Str(camel);
        return value.Length > 0 ? value : root.Str(snake);
    }

    private static string? OptionalEither(JsonElement root, JsonElement? target, string nested, string flat)
    {
        string value = target is { } t ? t.Str(nested) : string.Empty;
        return Optional(value.Length > 0 ? value : root.Str(flat));
    }

    private static string? Optional(string value) => value.Length == 0 ? null : value;
}
