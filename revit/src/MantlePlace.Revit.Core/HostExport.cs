using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>An export's status, in the order of its life (the platform export contract, section 4).</summary>
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

/// <summary>The wire spellings of <see cref="ExportStatus"/>, and which statuses are terminal.</summary>
public static class ExportStatuses
{
    /// <summary>The status as the platform spells it (<c>waiting_for_bundle</c>).</summary>
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

    /// <summary>
    /// Reads a wire status. <c>false</c> for a status this protocol does not define: a new status is
    /// a new protocol number, so a plugin on this one does not know how to act on it (section 7).
    /// </summary>
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

    /// <summary>No transition leaves a terminal status.</summary>
    public static bool IsTerminal(ExportStatus status)
        => status is ExportStatus.Imported or ExportStatus.Failed or ExportStatus.Cancelled;
}

/// <summary>One export: a row that names a bundle and a target, and carries no file and no URL.</summary>
/// <param name="Id">The export's id; how it is addressed.</param>
/// <param name="OrderId">The order whose bundle it hands over.</param>
/// <param name="Host">The host it targets (<c>unreal</c> or <c>revit</c>).</param>
/// <param name="TargetConnectionId">The connection id it targets.</param>
/// <param name="TargetDocument">The document the curator picked, or <c>null</c>.</param>
/// <param name="TargetMachine">The machine the curator picked, or <c>null</c>.</param>
/// <param name="TargetPluginVersion">The plugin version it was sent to, or <c>null</c>.</param>
/// <param name="Status">Where it is in its life.</param>
/// <param name="Progress">0 to 100 while importing, else <c>null</c>.</param>
/// <param name="FailureReason">The code it failed with, or <c>null</c>.</param>
/// <param name="FailureMessage">The words it failed with, or <c>null</c>.</param>
/// <param name="CreatedAt">As the platform wrote it.</param>
/// <param name="UpdatedAt">As the platform wrote it.</param>
/// <param name="EndedAt">As the platform wrote it; <c>null</c> until a terminal status.</param>
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
    /// <summary>
    /// Whether this plugin acts on the export: only one that targets its own connection id, on this
    /// plugin's host (the contract: an export is acted on only when the target connection id equals
    /// the plugin's own).
    /// </summary>
    public bool IsFor(string connectionId)
        => connectionId.Length > 0
           && string.Equals(TargetConnectionId, connectionId, StringComparison.Ordinal)
           && string.Equals(Host, ConnectedHosts.HostId, StringComparison.Ordinal);
}

/// <summary>
/// Reads an export from either of its two spellings into one <see cref="HostExport"/>.
/// </summary>
/// <remarks>
/// <para>
/// The routes return camelCase with the target nested (<c>targetConnectionId</c>,
/// <c>target.document</c>); a Realtime row change carries the table's snake_case columns, flat
/// (<c>target_connection_id</c>, <c>target_document</c>). Both mean the same, so a plugin treats a
/// row from the list and a row from a change the same way: one reader, field by field, camelCase
/// first.
/// </para>
/// <para>
/// Additive-only compatibility (section 7): a field this reader does not know is ignored. A status
/// it does not know is NOT, because a new status needs a new protocol number, so such a row reads
/// as not understood and is left alone.
/// </para>
/// </remarks>
public static class HostExports
{
    /// <summary>Reads one export object (a route's DTO, or a change's record).</summary>
    /// <returns><c>null</c> on success; otherwise why this plugin cannot act on the row.</returns>
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

    /// <summary>
    /// Reads the catch-up list body (an object with an <c>exports</c> array). A row this plugin
    /// cannot understand is left out, not fatal to the rest; the body itself being unreadable is.
    /// </summary>
    /// <returns><c>null</c> on success.</returns>
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

    /// <summary>The exports that target <paramref name="connectionId"/>, in order.</summary>
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

    // The route nests the target's fields under `target`; the row change flattens them with a
    // `target_` prefix.
    private static string? OptionalEither(JsonElement root, JsonElement? target, string nested, string flat)
    {
        string value = target is { } t ? t.Str(nested) : string.Empty;
        return Optional(value.Length > 0 ? value : root.Str(flat));
    }

    private static string? Optional(string value) => value.Length == 0 ? null : value;
}
